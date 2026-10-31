import { createRequire } from 'node:module';
import { expect, test, type Page } from '@playwright/test';
import { FakeStack } from './support/fake-stack';
import { metricsOf, tv, usageOf } from './support/context-usage';

// T051: responsive e accessibilita' a 1440 e 390 px (progetti desktop-1440 / mobile-390 della configurazione).
// Le prove automatiche (axe-core, regole WCAG 2.2 AA incluse contrasto e dimensione dei target) NON sostituiscono il
// controllo manuale con screen reader e zoom, e non attestano la conformita' WCAG: trovano solo i difetti che le regole
// sanno rilevare. Stack: API finta (support/fake-stack.ts), nessuna dipendenza da DGX o PostgreSQL.

const nodeRequire = createRequire(process.cwd() + '/package.json');
const AXE_PATH = nodeRequire.resolve('axe-core/axe.min.js');
const P = 'p-1';
const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

let fake: FakeStack;

test.beforeEach(async ({ page }) => {
  fake = new FakeStack();
  await fake.install(page);
});

async function login(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill('pm');
  await page.getByLabel('Password').fill('non-un-segreto');
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible();
}

async function openProject(page: Page) {
  await login(page);
  await page.goto(`/projects/${P}`);
  await expect(page.getByRole('heading', { level: 1, name: 'Aurora' })).toBeVisible();
}

async function connected(page: Page) {
  await expect.poll(async () => (await fake.connections(page)).filter((c) => c.open && c.project === P).length).toBe(1);
}

interface AxeViolation {
  id: string;
  impact: string;
  nodes: { target: string[]; failureSummary: string }[];
}
interface AxeWindow {
  axe: { run: (ctx: unknown, o: unknown) => Promise<{ violations: AxeViolation[] }> };
}

async function axeViolations(page: Page): Promise<string[]> {
  // Il puntatore fermo sopra un pulsante e le transizioni hover in corso falsano il contrasto misurato: si attende lo stato a riposo.
  await page.mouse.move(0, 0);
  await page.waitForTimeout(400);
  await page.addScriptTag({ path: AXE_PATH });
  return page.evaluate(async (tags) => {
    const result = await (window as unknown as AxeWindow).axe.run(document, { runOnly: { type: 'tag', values: tags } });
    return result.violations.map(
      (v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(' ') + ' => ' + n.failureSummary.replace(/\s+/g, ' ')).join(' | ')}`,
    );
  }, WCAG_TAGS);
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, 'scroll orizzontale della pagina').toBeLessThanOrEqual(0);
}

const isMobile = (page: Page) => page.viewportSize()!.width < 992;

async function openSidebar(page: Page) {
  if (!isMobile(page)) return;
  const toggle = page.locator('.shell-toggle');
  if ((await toggle.getAttribute('aria-expanded')) === 'false') await toggle.click();
}

test.describe('layout e axe-core', () => {
  test('login: senza scroll orizzontale, senza violazioni axe, campi con label', async ({ page }) => {
    await page.goto('/login');
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await noHorizontalScroll(page);
    await expect(page.getByLabel('Nome utente')).toBeVisible();
    await expect(page.getByLabel('Password')).toBeVisible();
    expect(await axeViolations(page)).toEqual([]);
  });

  test('progetto con analisi da confermare: senza scroll orizzontale e senza violazioni axe', async ({ page }) => {
    fake.seedAnalysis();
    await openProject(page);
    await connected(page);
    await expect(page.getByTestId('preview')).toBeVisible();
    await openSidebar(page);
    await noHorizontalScroll(page);
    expect(await axeViolations(page)).toEqual([]);
  });

  test('progetto con metriche sotto le risposte e monitor del contesto aperto: senza scroll orizzontale e senza violazioni axe', async ({ page }) => {
    const metrics = metricsOf({ prompt: 21256, completion: 1347, finishReason: 'length' });
    const { turn } = fake.addPm(P, 'Voglio un portale clienti');
    const done = fake.completeTurn(P, turn['id'] as string, 'Ho capito: portale clienti con ticket.', metrics);
    const old = fake.addPm(P, 'Domanda storica');
    fake.completeTurn(P, old.turn['id'] as string, 'Risposta storica senza telemetria', null);
    fake.project().usage = usageOf({
      metrics,
      refId: done.message['id'] as string,
      compaction: { at: '2030-01-01T10:04:00Z', tokensBefore: tv(24800), tokensAfter: tv(1700, 'calculated'), tokensSaved: tv(23100, 'calculated'), messagesSummarized: 40 },
      items: [
        { key: 'system_instructions', label: 'Istruzioni di sistema', tokens: tv(406), messageCount: null },
        { key: 'derived_summary', label: 'Riassunto derivato', tokens: tv(1500, 'estimated'), messageCount: 40 },
        { key: 'user_messages', label: 'Messaggi del PM', tokens: tv(9800), messageCount: 12 },
      ],
    });
    await openProject(page);
    await connected(page);
    await expect(page.getByTestId('usage-headline')).toBeVisible();
    if (isMobile(page)) await page.getByTestId('usage-toggle').click();
    await page.getByTestId('composition-toggle').click();
    await expect(page.getByTestId('composition')).toBeVisible();
    await expect(page.getByRole('region', { name: "Dialogo con l'assistente" }).getByTestId('metrics-truncated')).toBeVisible();
    await noHorizontalScroll(page);
    expect(await axeViolations(page)).toEqual([]);
  });

  test('progetto con analisi confermata: senza scroll orizzontale e senza violazioni axe', async ({ page }) => {
    fake.seedAnalysis();
    await openProject(page);
    await page.getByTestId('confirm').click();
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    await noHorizontalScroll(page);
    expect(await axeViolations(page)).toEqual([]);
  });

  test('sidebar: su schermo piccolo si apre e si richiude (aria-expanded), su desktop e sempre visibile', async ({ page }) => {
    await openProject(page);
    const sidebar = page.locator('#project-sidebar');
    if (isMobile(page)) {
      const toggle = page.locator('.shell-toggle');
      await expect(toggle).toHaveAttribute('aria-expanded', 'false');
      await expect(sidebar).toBeHidden();
      await toggle.focus();
      await page.keyboard.press('Enter');
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      await expect(sidebar).toBeVisible();
      await noHorizontalScroll(page);
      await page.keyboard.press('Enter');
      await expect(toggle).toHaveAttribute('aria-expanded', 'false');
      await expect(sidebar).toBeHidden();
      await expect(toggle).toBeFocused();
    } else {
      await expect(page.locator('.shell-toggle')).toBeHidden();
      await expect(sidebar).toBeVisible();
    }
  });
});

test.describe('tastiera e focus', () => {
  test('ordine di tabulazione coerente e focus sempre visibile', async ({ page }) => {
    fake.seedAnalysis();
    await openProject(page);
    await connected(page);
    await expect(page.getByTestId('preview')).toBeVisible();

    const visited: string[] = [];
    for (let i = 0; i < 80; i++) {
      await page.keyboard.press('Tab');
      const info = await page.evaluate(() => {
        const el = document.activeElement as HTMLElement | null;
        if (!el || el === document.body) return null;
        const cs = getComputedStyle(el);
        const r = el.getBoundingClientRect();
        return {
          name: el.getAttribute('aria-label') || el.textContent?.trim().slice(0, 40) || el.id || el.tagName,
          clipped: r.width === 0 || r.height === 0,
          outlineStyle: cs.outlineStyle,
          outlineWidth: parseFloat(cs.outlineWidth),
          boxShadow: cs.boxShadow,
          tag: el.tagName,
        };
      });
      if (!info) break;
      expect(info.clipped, `elemento focalizzato senza area: ${info.name}`).toBe(false);
      const indicator = (info.outlineStyle !== 'none' && info.outlineWidth >= 2) || info.boxShadow !== 'none';
      expect(indicator, `focus non visibile su "${info.name}" (${info.tag})`).toBe(true);
      visited.push(info.name);
    }
    expect(visited.length).toBeGreaterThan(8);
    expect(visited[0]).toBe('Vai al contenuto');
    const chatIdx = visited.findIndex((n) => n === 'chat-text');
    const sidebarIdx = visited.findIndex((n) => n === 'Nuovo progetto' || n === 'Chiudi elenco' || n === 'Progetti');
    expect(sidebarIdx).toBeGreaterThanOrEqual(0);
    expect(chatIdx, 'la chat segue la sidebar nell\'ordine di tabulazione').toBeGreaterThan(sidebarIdx);
  });

  test('il link "Vai al contenuto" porta il focus nel contenuto principale', async ({ page }) => {
    await openProject(page);
    await page.keyboard.press('Tab');
    const skip = page.getByRole('link', { name: 'Vai al contenuto' });
    await expect(skip).toBeFocused();
    await expect(skip).toBeInViewport();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/#main$/);
    await expect(page.locator('#main')).toBeInViewport();
  });

  test('comando Analizza azionabile da tastiera', async ({ page }) => {
    fake.addPm(P, 'Voglio un portale clienti');
    await openProject(page);
    await connected(page);
    const analyze = page.getByTestId('analyze');
    await analyze.focus();
    await expect(analyze).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('run-state')).toBeVisible();
  });
});

test.describe('label, ruoli e live region', () => {
  test('modulo di creazione progetto: label associate, nessuna violazione axe', async ({ page }) => {
    await openProject(page);
    await openSidebar(page);
    await page.getByRole('button', { name: 'Nuovo progetto' }).click();
    await expect(page.getByLabel('Nome', { exact: true })).toBeVisible();
    await expect(page.getByLabel('Descrizione (facoltativa)')).toBeVisible();
    expect(await axeViolations(page)).toEqual([]);
  });

  test('landmark e titoli presenti; nessuna live region che avvolge controlli o altre live region', async ({ page }) => {
    fake.seedAnalysis();
    await openProject(page);
    await expect(page.getByRole('main')).toHaveCount(1);
    await expect(page.getByRole('banner')).toHaveCount(1);
    await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1);
    const wrapping = await page.evaluate(() =>
      [...document.querySelectorAll('[aria-live]')]
        .filter((el) => el.querySelector('input, textarea, button, form, [aria-live], [role=alert], [role=status]'))
        .map((el) => el.className || el.tagName),
    );
    // Il registro del dialogo (role=log) puo' contenere solo messaggi, non l'intera area di lavoro.
    expect(wrapping, 'live region che avvolgono controlli o altre live region').toEqual([]);
  });

  test('streaming: il testo provvisorio non e annunciato token per token, il messaggio finale si', async ({ page }) => {
    const { turn } = fake.addPm(P, 'Voglio un portale clienti');
    fake.setTurn(P, turn['id'] as string, { status: 'Running' });
    await openProject(page);
    await connected(page);

    await fake.send(page, 'chat.delta', { chatTurnId: turn['id'], attemptCount: 1, deltaIndex: 0, text: 'bozza provvisoria' });
    const stream = page.locator('[data-role=assistant-streaming]');
    await expect(stream).toContainText('bozza provvisoria');
    const silent = await stream.evaluate((el) => {
      const live = el.closest('[aria-live]');
      return live === null || live.getAttribute('aria-live') === 'off';
    });
    expect(silent, 'il testo in streaming e dentro una live region attiva').toBe(true);

    const done = fake.completeTurn(P, turn['id'] as string, 'Risposta definitiva');
    await fake.send(page, 'chat.state', done.turn);
    const finalMessage = page.locator('article[data-role="assistant"]');
    await expect(finalMessage).toContainText('Risposta definitiva');
    const finalRole = await finalMessage.evaluate((el) => el.closest('[aria-live]:not([aria-live=off])')?.getAttribute('role') ?? null);
    expect(finalRole, 'il messaggio finale deve stare nel registro (role=log)').toBe('log');
  });
});

test.describe('stati di errore e caricamento accessibili', () => {
  test('login rifiutato: errore con role=alert e nessuna violazione axe', async ({ page }) => {
    await page.route('**/api/auth/login', (route) =>
      route.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ status: 401, code: 'invalid_credentials', title: 'x' }) }),
    );
    await page.goto('/login');
    await page.getByLabel('Nome utente').fill('pm');
    await page.getByLabel('Password').fill('sbagliata');
    await page.getByRole('button', { name: 'Accedi' }).click();
    const alert = page.getByRole('alert');
    await expect(alert).toBeVisible();
    await expect(alert).not.toBeEmpty();
    expect(await axeViolations(page)).toEqual([]);
  });

  test('esito incerto del messaggio: role=alert con azione da tastiera, senza token nel testo', async ({ page }) => {
    fake.fail('postMessage', 'drop-after-save');
    await openProject(page);
    await page.getByLabel("Messaggio per l'assistente").fill('Voglio un portale');
    await page.getByRole('button', { name: 'Invia', exact: true }).click();
    const error = page.locator('.send-error');
    await expect(error).toBeVisible();
    await expect(error).toHaveAttribute('role', 'alert');
    const retry = error.getByRole('button', { name: 'Verifica e riprova' });
    await retry.focus();
    await expect(retry).toBeFocused();
    expect(await error.innerText()).not.toMatch(/tok-1|Bearer|eyJ/);
    expect(await axeViolations(page)).toEqual([]);
  });

  test('run in corso e run fallito: stato annunciato (status/alert), spinner decorativo nascosto', async ({ page }) => {
    fake.addPm(P, 'Voglio un portale clienti');
    await openProject(page);
    await connected(page);
    await page.getByTestId('analyze').click();
    const state = page.getByTestId('run-state');
    await expect(state).toHaveAttribute('role', 'status');
    await expect(state.locator('.spinner-border')).toHaveAttribute('aria-hidden', 'true');
    expect(await axeViolations(page)).toEqual([]);

    const failed = fake.setRun(P, 'r-1', { status: 'Failed', completedAt: '2030-01-01T10:05:00Z', error: { code: 'attempts_exhausted', message: null } });
    await fake.send(page, 'processing.state', failed);
    const err = page.getByTestId('run-error');
    await expect(err).toBeVisible();
    await expect(err).toHaveAttribute('role', 'alert');
    expect(await axeViolations(page)).toEqual([]);
  });

  test('caricamento della lista progetti: stato role=status visibile mentre l\'API risponde', async ({ page }) => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => (release = resolve));
    await page.route(/\/api\/projects(\?.*)?$/, async (route) => {
      await gate;
      await route.fallback();
    });
    await page.goto('/login');
    await page.getByLabel('Nome utente').fill('pm');
    await page.getByLabel('Password').fill('non-un-segreto');
    await page.getByRole('button', { name: 'Accedi' }).click();
    await openSidebar(page);
    const loading = page.locator('#project-sidebar [role=status]');
    await expect(loading).toBeVisible();
    await expect(loading).toHaveText(/Caricamento/);
    release();
    await expect(loading).toHaveCount(0);
  });
});

test.describe('movimento ridotto e una sola azione primaria per fase', () => {
  test('prefers-reduced-motion: animazioni azzerate', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    fake.addPm(P, 'Voglio un portale clienti');
    await openProject(page);
    await connected(page);
    await page.getByTestId('analyze').click();
    const spinner = page.getByTestId('run-state').locator('.spinner-border');
    await expect(spinner).toBeVisible();
    const style = await spinner.evaluate((el) => {
      const cs = getComputedStyle(el);
      return { duration: cs.animationDuration, count: cs.animationIterationCount };
    });
    expect(parseFloat(style.duration)).toBeLessThanOrEqual(0.001);
    expect(style.count).toBe('1');
  });

  const primaries = (page: Page) =>
    page
      .locator('app-analysis-preview .btn-primary:visible, app-document-view .btn-primary:visible')
      .evaluateAll((els) => els.map((e) => (e.textContent ?? '').trim()));

  test('fase analisi: una sola azione primaria (Analizza)', async ({ page }) => {
    fake.addPm(P, 'Voglio un portale clienti');
    await openProject(page);
    await connected(page);
    expect(await primaries(page)).toEqual(['Analizza']);
  });

  test('fasi anteprima e documento: una sola azione primaria per fase', async ({ page }) => {
    fake.seedAnalysis();
    await openProject(page);
    await connected(page);
    await expect(page.getByTestId('preview')).toBeVisible();
    expect(await primaries(page), 'fase anteprima/conferma').toHaveLength(1);

    await page.getByTestId('confirm').click();
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    expect(await primaries(page), 'fase generazione').toHaveLength(1);
  });
});
