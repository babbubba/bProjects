import { createRequire } from 'node:module';
import { expect, type Page } from '@playwright/test';

// Controlli automatici di accessibilita' condivisi (T107): axe-core WCAG 2.0/2.1/2.2 A+AA, scroll orizzontale, audit del focus, contrasto dei campi.
// Trovano solo cio' che le regole sanno rilevare e NON attestano la conformita': la revisione con lettore di schermo resta umana.

const nodeRequire = createRequire(process.cwd() + '/package.json');
const AXE_PATH = nodeRequire.resolve('axe-core/axe.min.js');
const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

interface AxeResult {
  violations: { id: string; impact: string; nodes: { target: string[]; failureSummary: string }[] }[];
  passes: unknown[];
  incomplete: { id: string; nodes: { target: string[]; any?: { data?: { messageKey?: string } }[] }[] }[];
}
interface AxeWindow {
  axe: { run: (ctx: unknown, o: unknown) => Promise<AxeResult> };
}

/** Fasi controllate (etichetta, regole superate, incomplete): finiscono nell'output della prova per validation/accessibility.md. */
export const checked: string[] = [];

/** Una fase del percorso: nessuno scroll orizzontale, nessuna violazione axe (WCAG A+AA 2.2), nessuna regola "incomplete" ignorata in silenzio. */
export async function checkpoint(page: Page, label: string) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, `scroll orizzontale della pagina (${label})`).toBeLessThanOrEqual(0);
  await page.mouse.move(0, 0);
  await page.waitForTimeout(400);
  await page.addScriptTag({ path: AXE_PATH });
  const result = await page.evaluate(async (tags) => {
    const r = await (window as unknown as AxeWindow).axe.run(document, { runOnly: { type: 'tag', values: tags } });
    return {
      violations: r.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(' ') + ' => ' + n.failureSummary.replace(/\s+/g, ' ')).join(' | ')}`),
      passes: r.passes.length,
      incomplete: r.incomplete.map((i) => i.id + `[${i.nodes.length}: ${i.nodes.slice(0, 4).map((n) => n.target.join(' ') + ' ' + (n.any?.[0]?.data?.messageKey ?? '')).join('; ')}]`),
    };
  }, WCAG_TAGS);
  expect(result.violations, `axe (${label})`).toEqual([]);
  expect(result.passes, `axe non ha valutato regole (${label})`).toBeGreaterThan(10);
  checked.push(`${label}: ${result.passes} regole superate, 0 violazioni${result.incomplete.length ? `, da rivedere a mano: ${[...new Set(result.incomplete)].join(' | ')}` : ''}`);
}

/** Contrasto calcolato a mano (WCAG 1.4.3, >= 4.5:1) per i campi che axe segnala come "incomplete" (testo parzialmente coperto dalla maniglia di ridimensionamento). */
export async function fieldContrast(page: Page, label: string): Promise<void> {
  const rows = await page.evaluate(() => {
    const lum = (c: number[]) => {
      const [r, g, b] = c.map((v) => { const x = v / 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; });
      return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    };
    const rgba = (v: string) => (v.match(/[\d.]+/g) ?? []).map(Number);
    const background = (el: Element | null): number[] => {
      for (let e = el; e; e = e.parentElement) {
        const c = rgba(getComputedStyle(e).backgroundColor);
        if (c.length >= 3 && (c[3] ?? 1) === 1) return c;
      }
      return [255, 255, 255];
    };
    return [...document.querySelectorAll<HTMLElement>('textarea, input[type=text], input:not([type])')]
      .filter((e) => e.offsetParent !== null)
      .map((e) => {
        const fg = rgba(getComputedStyle(e).color);
        const a = lum(fg);
        const b = lum(background(e));
        return { name: e.getAttribute('aria-label') ?? e.id ?? e.tagName, ratio: (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05) };
      });
  });
  expect(rows.length, `campi di testo valutati (${label})`).toBeGreaterThan(0);
  expect(rows.filter((r) => r.ratio < 4.5), `contrasto dei campi (${label})`).toEqual([]);
  checked.push(`${label}: contrasto calcolato di ${rows.length} campi di testo, minimo ${Math.min(...rows.map((r) => r.ratio)).toFixed(1)}:1`);
}

/** Ogni elemento focalizzabile visibile ha un indicatore di focus, un'area e un nome accessibile, e gli id non sono duplicati. */
export async function focusAudit(page: Page, scope: string, label: string) {
  const count = await page.locator(scope).evaluate((root) => {
    const sel = 'a[href], button, input, select, textarea, summary, [tabindex]:not([tabindex="-1"])';
    return [...root.querySelectorAll<HTMLElement>(sel)].filter((e) => e.offsetParent !== null && !(e as HTMLInputElement).disabled).length;
  });
  expect(count, `elementi focalizzabili in ${label}`).toBeGreaterThan(0);
  await page.locator(scope).first().evaluate((root) => (root.querySelector<HTMLElement>('a[href], button:not([disabled]), input:not([disabled])') as HTMLElement | null)?.focus());
  const seen: string[] = [];
  for (let i = 0; i < count + 2; i++) {
    const info = await page.evaluate((s) => {
      const el = document.activeElement as HTMLElement | null;
      if (!el || el === document.body || !el.closest(s)) return null;
      const cs = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      const labelled = (el as HTMLInputElement).labels?.length ? (el as HTMLInputElement).labels![0].textContent!.trim() : '';
      const name = el.getAttribute('aria-label') || el.getAttribute('aria-labelledby') || labelled || el.textContent?.trim() || '';
      return {
        name: name.slice(0, 40) || el.tagName,
        hasName: name.length > 0,
        area: r.width * r.height,
        indicator: (cs.outlineStyle !== 'none' && parseFloat(cs.outlineWidth) >= 2) || cs.boxShadow !== 'none',
        tag: el.tagName,
      };
    }, scope);
    if (!info) break;
    expect(info.hasName, `senza nome accessibile: ${info.tag} in ${label}`).toBe(true);
    expect(info.area, `senza area: ${info.name}`).toBeGreaterThan(0);
    expect(info.indicator, `focus non visibile su "${info.name}" (${info.tag}) in ${label}`).toBe(true);
    seen.push(info.name);
    await page.keyboard.press('Tab');
  }
  expect(seen.length, `tabulazione in ${label}`).toBeGreaterThanOrEqual(Math.min(count, 5));
  const dup = await page.evaluate(() => {
    const ids = [...document.querySelectorAll('[id]')].map((e) => e.id);
    return ids.filter((id, i) => ids.indexOf(id) !== i);
  });
  expect(dup, `id duplicati (${label})`).toEqual([]);
}

