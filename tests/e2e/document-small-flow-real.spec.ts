import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { documentFixturesDir, sha256 } from './support/documents';

// T067 (DGX, browser): percorso piccolo US1 su STACK REALE (nginx + API + PostgreSQL + parser Docling + DGX reale, nessun fake e nessuna
// route simulata): upload dei quattro formati -> Ready (parser e estrazione reali) -> chat con fonti -> Analizza -> modifica PM ->
// conferma -> Genera -> Markdown con fonti e attribuzioni. Le attese sono annotate qui PRIMA della prova (stesso gold del caso N1 del test
// backend DocumentSmallFlowMafRealTests) e sono vincolanti: nessun errore gestito vale PASS. Un solo viewport (desktop-1440): il DGX e a
// slot unico, quindi non si ripete il percorso a 390 px.
// Variabili: E2E_BASE_URL (stack gia avviato), E2E_USER, E2E_PASSWORD (account PM), E2E_MODEL_MODE=real. Senza, la suite risulta ignorata,
// mai superata. Eseguire da sola, mai in parallelo ad altre prove sul DGX.

const user = process.env['E2E_USER'];
const password = process.env['E2E_PASSWORD'];
const mode = process.env['E2E_MODEL_MODE'];

test.skip(!process.env['E2E_BASE_URL'] || !user || !password, 'E2E_BASE_URL, E2E_USER ed E2E_PASSWORD non impostate: stack reale non disponibile.');
test.skip(mode !== 'real', 'E2E_MODEL_MODE=real non impostata: serve uno stack con parser e DGX raggiungibili.');

const screens = 'test-results/screens';
const nodeRequire = createRequire(process.cwd() + '/package.json');
const AXE_PATH = nodeRequire.resolve('axe-core/axe.min.js');
const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];
const fixture = (name: string) => readFileSync(join(documentFixturesDir, name));
const mediaTypes: Record<string, string> = {
  pdf: 'application/pdf',
  docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  md: 'text/markdown',
  txt: 'text/plain',
};
const asFile = (name: string) => ({ name, mimeType: mediaTypes[name.split('.').pop()!] ?? 'application/octet-stream', buffer: fixture(name) });
const fold = (s: string) => s.normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/[’‘]/g, "'").toLowerCase().replace(/\s+/g, ' ').trim();
const log = (label: string, value: unknown) => console.log(`[real-t067] ${label}: ${typeof value === 'string' ? value : JSON.stringify(value)}`);
const unique = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

const FILES = ['pdf-text.pdf', 'docx-basic.docx', 'md-basic.md', 'txt-negations.txt'];
const NEGATIVE = /\b(non|nessun\w*|senza|vietat\w*|esclus\w*|escluder\w*|mai|tranne)\b/;

async function login(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill(user!);
  await page.getByLabel('Password').fill(password!);
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible({ timeout: 20_000 });
}

async function createProject(page: Page, name: string) {
  const before = page.url();
  await page.getByRole('button', { name: 'Nuovo progetto' }).click();
  await page.getByLabel('Nome', { exact: true }).fill(name);
  await page.getByRole('button', { name: 'Crea progetto' }).click();
  await expect(page).not.toHaveURL(before);
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
}

async function axeViolations(page: Page): Promise<string[]> {
  await page.mouse.move(0, 0);
  await page.waitForTimeout(400);
  await page.addScriptTag({ path: AXE_PATH });
  return page.evaluate(async (tags) => {
    const axe = (window as unknown as { axe: { run: (c: unknown, o: unknown) => Promise<{ violations: { id: string; impact: string; nodes: { target: string[] }[] }[] }> } }).axe;
    return (await axe.run(document, { runOnly: { type: 'tag', values: tags } })).violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`);
  }, WCAG_TAGS);
}

test('[DGX reale] documenti -> chat con fonti -> Analizza -> correzione PM -> conferma -> Genera (parser, DB, MAF e modello reali)', async ({ page }, info) => {
  test.skip(info.project.name !== 'desktop-1440', 'DGX a slot unico: un solo viewport');
  test.setTimeout(75 * 60_000);
  const problems: string[] = [];
  page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`));
  page.on('console', (m) => {
    if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) problems.push(`console: ${m.text()}`);
  });
  page.on('response', (r) => {
    if (r.status() >= 500 && r.url().includes('/api/')) problems.push(`HTTP ${r.status()} ${new URL(r.url()).pathname}`);
  });
  // Solo osservazione: nessuna route e simulata. Si registrano le scritture per provare che nessun comando AI parte da solo.
  const writes: string[] = [];
  page.on('request', (r) => {
    if (r.method() !== 'GET' && r.url().includes('/api/') && !r.url().includes('/api/auth/')) writes.push(`${r.method()} ${new URL(r.url()).pathname}`);
  });

  await login(page);
  await createProject(page, `T067 reale ${unique()}`);
  await page.getByTestId('tab-documents').click();
  await expect(page.getByRole('heading', { name: 'Documenti', exact: true })).toBeVisible();

  // --- 1. Upload dei quattro formati: nessun upload avvia analisi o chat; i documenti diventano Pronto col parser e l'estrazione reali.
  for (const name of FILES) {
    await page.getByTestId('docs-file').setInputFiles(asFile(name));
    await page.getByTestId('docs-upload').click();
    await expect(page.getByTestId('docs-item').filter({ hasText: name })).toHaveCount(1);
  }
  expect(writes.filter((w) => !/\/documents(\/|$)/.test(w) && w !== 'POST /api/projects'), 'nessun comando oltre a creazione progetto e upload').toEqual([]);
  for (const name of FILES) {
    await expect(page.getByTestId('docs-item').filter({ hasText: name }).getByTestId('docs-status'), `${name} Pronto (parser + estrazione reali)`).toHaveText('Pronto', { timeout: 12 * 60_000 });
  }
  log('documenti', 'tutti Pronto');

  // --- originale scaricabile e identico (hash) per il PDF.
  const pdf = page.getByTestId('docs-item').filter({ hasText: 'pdf-text.pdf' });
  await pdf.getByTestId('docs-versions').click();
  const [download] = await Promise.all([page.waitForEvent('download'), pdf.getByTestId('docs-download').first().click()]);
  expect(sha256(readFileSync((await download.path())!))).toBe(sha256(fixture('pdf-text.pdf')));

  // --- 2. Chat: risposta con fonti apribili. Attese: la risposta riporta 24 mesi; una fonte citata contiene «24 mesi» (passaggio reale).
  await page.getByTestId('tab-work').click();
  const box = page.getByLabel("Messaggio per l'assistente");
  await box.fill("Per quanto tempo restano visibili gli ordini e a chi non e' ammesso l'accesso ai dati degli ordini?");
  await box.press('Control+Enter');
  const assistant = page.locator('article[data-role="assistant"]');
  await expect(assistant).toHaveCount(1, { timeout: 8 * 60_000 });
  const answer = await assistant.innerText();
  log('risposta chat', answer.slice(0, 400));
  expect(fold(answer)).toContain('24');
  const chips = assistant.getByTestId('chat-source');
  const chipCount = await chips.count();
  expect(chipCount, 'la risposta cita almeno una fonte').toBeGreaterThan(0);
  let found24 = false;
  for (let i = 0; i < chipCount; i++) {
    await chips.nth(i).click();
    const quote = fold(await assistant.getByTestId('part-quote').first().innerText());
    if (quote.includes('24 mesi')) found24 = true;
    await expect(assistant.getByTestId('passage-open').first()).toBeVisible();
  }
  expect(found24, 'una fonte citata dalla chat e il passaggio «24 mesi»').toBe(true);
  expect(await page.getByTestId('source-chip-unknown').count(), 'nessun marcatore di fonte sconosciuta').toBe(0);

  // --- Monitor: classificazione reale del root (regime e ricevuta) con il payload misurato.
  if ((page.viewportSize()?.width ?? 0) < 1200) await page.getByTestId('usage-toggle').click();
  await expect(page.getByTestId('execution-regime')).toContainText('Regime: Ordinario');
  await expect(page.getByTestId('execution-receipt')).toBeVisible();
  log('monitor chat', await page.getByTestId('execution-regime').innerText());
  await page.screenshot({ path: `${screens}/T067-real-chat.png`, fullPage: true });

  // --- 3. Analizza (comando esplicito): attese annotate del caso N1 (gold) sulle voci mostrate.
  expect(writes.filter((w) => /\/analyses$/.test(w))).toEqual([]);
  await page.getByTestId('analyze').click();
  await expect(page.getByTestId('run-state')).toBeVisible();
  // Si attende l'esito (anteprima oppure errore del run): un errore gestito del run NON e un PASS, ma non si aspetta inutilmente 40 minuti.
  await expect.poll(async () => (await page.getByTestId('preview').count()) + (await page.getByTestId('run-error').count()), { timeout: 40 * 60_000, intervals: [2_000] }).toBeGreaterThan(0);
  if ((await page.getByTestId('run-error').count()) > 0) log('run di analisi fallito (errore mostrato dalla UI)', await page.getByTestId('run-error').innerText());
  await expect(page.getByTestId('run-error'), 'analisi: il run non deve fallire (errore gestito = FAIL)').toHaveCount(0);
  await expect(page.getByTestId('preview'), 'anteprima dell analisi').toBeVisible();
  const itemsLoc = page.locator('[data-testid=item]');
  const count = await itemsLoc.count();
  expect(count).toBeGreaterThan(0);
  const texts: string[] = [];
  for (let i = 0; i < count; i++) texts.push(fold(await itemsLoc.nth(i).getByTestId('item-text').inputValue()));
  log('voci', texts);
  const has = (...tokens: string[]) => texts.some((t) => tokens.every((k) => t.includes(k)));
  expect(has('24', 'mesi'), 'attesa: visibilita 24 mesi').toBe(true);
  expect(texts.some((t) => t.includes('anonim') && NEGATIVE.test(t)), 'attesa: accesso anonimo NEGATO').toBe(true);
  expect(texts.some((t) => t.includes('annullat') && NEGATIVE.test(t)), 'attesa: ordini annullati ESCLUSI').toBe(true);
  expect(has('rapporto', 'mensile'), 'attesa: rapporto mensile').toBe(true);
  expect(has('quinto', 'lavorativo'), 'attesa: quinto giorno lavorativo').toBe(true);
  expect(has('operatore', 'lettura'), 'attesa: Operatore sola lettura').toBe(true);
  expect(texts.filter((t) => t.includes('anonim') && !NEGATIVE.test(t)), 'nessuna inversione: accesso anonimo affermato').toEqual([]);
  // Attribuzioni visibili: ogni voce rimanda ai documenti e il passaggio si apre con la citazione reale.
  await expect(page.getByTestId('validation-badge')).toBeVisible();
  const first = itemsLoc.first();
  await expect(first.getByTestId('item-sources')).toHaveText('Vedi nei documenti');
  await first.getByTestId('item-sources').click();
  await expect(first.getByTestId('passage-open').first()).toBeVisible();
  await expect(page.getByTestId('execution-regime')).toContainText('Regime:');
  await page.screenshot({ path: `${screens}/T067-real-anteprima.png`, fullPage: true });
  expect(await axeViolations(page), 'axe WCAG 2.2 AA sull anteprima').toEqual([]);

  // --- 4. Correzione PM: la voce «24 mesi» e precisata; nuova revisione, fonti documentali intatte.
  const targetIndex = texts.findIndex((t) => t.includes('24') && t.includes('mesi'));
  expect(targetIndex, 'voce «24 mesi» da correggere').toBeGreaterThanOrEqual(0);
  const field = itemsLoc.nth(targetIndex).getByTestId('item-text');
  const before = await field.inputValue();
  await field.fill(`${before} Il PM precisa che la visibilita vale anche per gli ordini in garanzia.`);
  await page.getByTestId('save').click();
  await expect(page.getByTestId('dirty-badge')).toBeHidden({ timeout: 30_000 });
  expect(writes.filter((w) => /\/preview$/.test(w))).toHaveLength(1);

  // --- 5. Conferma (nessuna generazione implicita) e Genera.
  await page.getByTestId('confirm').click();
  await expect(page.getByTestId('confirmed-badge')).toBeVisible({ timeout: 30_000 });
  expect(writes.filter((w) => /document-generations$/.test(w)), 'la conferma non genera').toEqual([]);
  await page.getByTestId('generate').click();
  await expect(page.getByTestId('generation-state')).toBeVisible();
  await expect.poll(async () => (await page.getByTestId('draft').count()) + (await page.getByTestId('generation-error').count()), { timeout: 40 * 60_000, intervals: [2_000] }).toBeGreaterThan(0);
  if ((await page.getByTestId('generation-error').count()) > 0) log('generazione fallita (errore mostrato dalla UI)', await page.getByTestId('generation-error').innerText());
  await expect(page.getByTestId('generation-error'), 'generazione: nessun errore gestito').toHaveCount(0);
  await expect(page.getByTestId('draft'), 'bozza').toBeVisible();
  await expect(page.getByTestId('draft-label')).toContainText('Bozza AI — da revisionare');
  await expect(page.getByTestId('draft-status')).toContainText(/Proposta|proposta|Non approvata|non approvata/);
  const markdown = fold(await page.getByTestId('markdown').innerText());
  log('bozza', markdown.slice(0, 600));
  expect(markdown, 'la bozza riflette la correzione PM').toContain('garanzia');
  expect(markdown).toContain('24');
  expect(markdown).toContain('anonim');
  const draftChips: Locator = page.getByTestId('markdown').getByTestId('source-chip');
  expect(await draftChips.count(), 'la bozza cita le fonti').toBeGreaterThan(0);
  await expect(page.getByTestId('source-chip-unknown')).toHaveCount(0);
  await draftChips.first().click();
  await expect(page.getByTestId('focused-source')).toBeVisible();
  await page.screenshot({ path: `${screens}/T067-real-bozza.png`, fullPage: true });
  expect(await axeViolations(page), 'axe WCAG 2.2 AA sulla bozza').toEqual([]);

  // Un solo comando per passo e nessun errore in console.
  const commands = writes.filter((w) => !/\/documents(\/|$)/.test(w));
  log('comandi', commands);
  expect(commands.filter((w) => /\/messages$/.test(w))).toHaveLength(1);
  expect(commands.filter((w) => /\/analyses$/.test(w))).toHaveLength(1);
  expect(commands.filter((w) => /\/confirm$/.test(w))).toHaveLength(1);
  expect(commands.filter((w) => /document-generations$/.test(w))).toHaveLength(1);
  expect(problems).toEqual([]);
});
