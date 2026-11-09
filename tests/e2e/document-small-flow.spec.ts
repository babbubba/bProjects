import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { expect, test, type Page } from '@playwright/test';
import { FakeParsing } from './support/fake-parsing';
import {
  FlowStack, completeDocumentAnalysis, completeDocumentDraft, documentSources, executionOf, installFlowRoutes, type DocSource,
} from './support/fake-document-flow';
import { metricsOf, tv, usageOf } from './support/context-usage';
import { documentFixturesDir, sha256 } from './support/documents';

// T066 (E2E-OFFLINE, browser): percorso piccolo US1 con API simulate nel browser (fake-stack separato: support/fake-stack.ts +
// fake-parsing.ts + fake-document-flow.ts), a 1440 e 390 px (progetti desktop-1440 / mobile-390), usando la TASTIERA per i comandi
// del percorso (focus + Invio / Ctrl+Invio). Prova la UI e il contratto HTTP letto dal browser: upload -> consulta -> chat con fonti ->
// Analizza -> modifica/conferma -> Genera -> Markdown con fonti. NON prova server, parser, persistenza, classificazione Pending ->
// Classified o validazione delle citazioni: quelle sono in backend/tests/bProjects.IntegrationTests/DocumentSmallFlowTests.cs
// (API+PostgreSQL+dispatcher reali con modello finto). Nessun DGX, nessun PostgreSQL, nessuna variabile d'ambiente.
//
// LIMITE: il parsing simulato serve blocchi canned per formato (non il contenuto dei byte del corpus T002): gli originali caricati sono
// le fixture reali e si scaricano identici, ma i passaggi mostrati sono quelli del fake.

const P = 'p-1';
const screens = 'test-results/screens';
const nodeRequire = createRequire(process.cwd() + '/package.json');
const AXE_PATH = nodeRequire.resolve('axe-core/axe.min.js');
const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

const fixture = (name: string) => readFileSync(join(documentFixturesDir, name));
const mediaTypes: Record<string, string> = {
  pdf: 'application/pdf', docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', md: 'text/markdown', txt: 'text/plain',
};
const asFile = (name: string, buffer: Buffer = fixture(name)) => ({ name, mimeType: mediaTypes[name.split('.').pop()!] ?? 'application/octet-stream', buffer });

let fake: FlowStack;
let parsing: FakeParsing;
let sources: DocSource[];
let consoleErrors: string[];

test.beforeEach(async ({ page }) => {
  fake = new FlowStack();
  parsing = new FakeParsing();
  fake.parsing = parsing;
  sources = [];
  await fake.install(page);
  await installFlowRoutes(page, fake, () => sources);
  consoleErrors = [];
  page.on('pageerror', (e) => consoleErrors.push(`pageerror: ${e.message}`));
  page.on('console', (m) => {
    if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) consoleErrors.push(`console: ${m.text()}`);
  });
  page.on('response', (r) => {
    if (r.status() >= 500 && r.url().includes('/api/')) consoleErrors.push(`HTTP ${r.status()} ${new URL(r.url()).pathname}`);
  });
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

/** Attiva un controllo con la tastiera: focus reale (verificato) e Invio, mai un clic del puntatore. */
async function activate(page: Page, locator: ReturnType<Page['getByTestId']>) {
  // Il focus può essere ripreso dal rendering appena avvenuto (cambio scheda, aggiornamento della lista): si riprova finché è stabile.
  await expect(async () => {
    await locator.focus({ timeout: 1_000 });
    await expect(locator).toBeFocused({ timeout: 500 });
  }).toPass({ timeout: 8_000 });
  await page.keyboard.press('Enter');
}

const tab = async (page: Page, id: 'tab-work' | 'tab-documents') => {
  await page.getByTestId(id).focus();
  await page.keyboard.press('Enter');
};

const items = (page: Page) => page.getByTestId('docs-item');
const item = (page: Page, name: string) => items(page).filter({ hasText: name });

async function upload(page: Page, name: string) {
  await page.getByTestId('docs-file').setInputFiles(asFile(name));
  await activate(page, page.getByTestId('docs-upload'));
  await expect(item(page, name)).toHaveCount(1);
}

async function noHorizontalScroll(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), 'scroll orizzontale').toBe(true);
}

interface AxeViolation { id: string; impact: string; nodes: { target: string[] }[] }
async function axeViolations(page: Page): Promise<string[]> {
  await page.mouse.move(0, 0);
  await page.waitForTimeout(400);
  await page.addScriptTag({ path: AXE_PATH });
  return page.evaluate(async (tags) => {
    const axe = (window as unknown as { axe: { run: (c: unknown, o: unknown) => Promise<{ violations: AxeViolation[] }> } }).axe;
    return (await axe.run(document, { runOnly: { type: 'tag', values: tags } })).violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`);
  }, WCAG_TAGS);
}

/** Scritture inviate dal browser, escluse autenticazione e upload: nessun comando AI parte da solo. */
const commands = () => fake.calls.filter((c) => c.method !== 'GET' && !c.path.startsWith('/api/auth/') && !/\/documents(\/|$)/.test(c.path));

// ============================================================================================================== percorso nominale

test.describe('[E2E fake] percorso piccolo: documenti -> chat -> Analizza -> conferma -> Genera', () => {
  test('upload, consultazione, chat con fonti, Analizza, modifica, conferma e Markdown con fonti, da tastiera', async ({ page }, info) => {
    await openProject(page);
    await connected(page);
    await tab(page, 'tab-documents');
    await expect(page.getByRole('heading', { name: 'Documenti', exact: true })).toBeVisible();

    // --- Upload dei quattro formati: gli originali sono le fixture del corpus T002; nessun upload avvia chat, analisi o generazione.
    const files = ['md-basic.md', 'txt-conflict-a.txt', 'pdf-text.pdf', 'docx-basic.docx'];
    for (const name of files) await upload(page, name);
    for (const name of files) await expect(item(page, name).getByTestId('docs-status')).toHaveText('Pronto');
    expect(commands(), 'nessun comando oltre agli upload').toEqual([]);

    // --- Consultazione: passaggio di un blocco e originale identico alla fixture (hash).
    const md = item(page, 'md-basic.md');
    await md.getByTestId('docs-versions').focus();
    await page.keyboard.press('Enter');
    await md.getByTestId('docs-read').first().focus();
    await page.keyboard.press('Enter');
    await expect(md.getByTestId('passage-status')).toContainText('Pronto');
    await expect(md.getByTestId('passage-block')).toHaveCount(3);
    const [download] = await Promise.all([page.waitForEvent('download'), (async () => { await md.getByTestId('docs-download').first().focus(); await page.keyboard.press('Enter'); })()]);
    expect(sha256(readFileSync((await download.path())!))).toBe(sha256(fixture('md-basic.md')));
    await noHorizontalScroll(page);
    expect(commands()).toEqual([]);

    // --- Fonti documentali pronte (stesse del server simulato) per chat e analisi.
    sources = documentSources(fake);
    expect(sources.length).toBeGreaterThanOrEqual(4);
    await tab(page, 'tab-work');

    // --- Chat: Ctrl+Invio; la risposta cita fonti [fonte:id] che il browser risolve nelle evidenze documentali e apre da tastiera.
    const question = 'Cosa dicono i documenti sul portale?';
    const box = page.getByLabel("Messaggio per l'assistente");
    await box.fill(question);
    await box.press('Control+Enter');
    await expect(page.locator('article[data-role="user"]')).toHaveCount(1);
    const turnId = fake.project().turns[0]['id'] as string;
    const cited = sources.find((s) => s.documentName === 'md-basic.md')!;
    const done = fake.completeTurn(P, turnId, `Il portale è per i clienti interni [fonte:${cited.sourceId}], con esclusioni [fonte:${sources[1].sourceId}].`, metricsOf({ phase: 'chat' }));
    fake.project().usage = usageOf({
      kind: 'chat', refId: done.message['id'] as string, metrics: metricsOf({ phase: 'chat' }),
      items: [
        { key: 'system_instructions', label: 'Istruzioni di sistema', tokens: tv(300), messageCount: null },
        { key: 'user_messages', label: 'Messaggi del PM', tokens: tv(40), messageCount: 1 },
        { key: 'document_sources', label: 'Fonti documentali', tokens: tv(520), messageCount: 1 },
      ],
    });
    (fake.project().usage!['last'] as Record<string, unknown>)['execution'] = executionOf({ regime: 'Ordinary', receipt: 'ab12cd34'.repeat(8) });
    await fake.send(page, 'chat.state', done.turn);
    const assistant = page.locator('article[data-role="assistant"]');
    await expect(assistant).toHaveCount(1);
    const chips = assistant.getByTestId('chat-source');
    await expect(chips).toHaveCount(2);
    await expect(chips.nth(0)).toHaveText('F1');
    await activate(page, chips.nth(0));
    await expect(assistant.getByTestId('chat-source-open')).toBeVisible();
    await expect(assistant.getByTestId('part-quote')).toContainText('Il portale è per i clienti interni');
    // «Vedi la fonte» apre subito il passaggio (parti espanse): lo si richiude e riapre da tastiera.
    await expect(assistant.getByTestId('passage-open')).toContainText('Il portale è per i clienti interni');
    await activate(page, assistant.getByTestId('open-part'));
    await expect(assistant.getByTestId('passage-open')).toHaveCount(0);
    await activate(page, assistant.getByTestId('open-part'));
    await expect(assistant.getByTestId('passage-open')).toContainText('Il portale è per i clienti interni');
    expect(commands().map((c) => `${c.method} ${c.path}`), 'la chat invia solo il messaggio').toEqual([`POST /api/projects/${P}/messages`]);

    // --- Telemetria: la composizione attribuisce le fonti documentali e l'esecuzione mostra regime e ricevuta del preflight.
    if (page.viewportSize()!.width < 1200) await activate(page, page.getByTestId('usage-toggle'));
    await expect(page.getByTestId('execution-regime')).toContainText('Regime: Ordinario');
    await expect(page.getByTestId('execution-receipt')).toBeVisible();
    await expect(page.getByTestId('execution-deadline')).toContainText('non è una durata prevista');
    await expect(page.getByTestId('execution-extended-note')).toHaveCount(0);
    await activate(page, page.getByTestId('composition-toggle'));
    await expect(page.getByTestId('composition').locator('tr[data-key="document_sources"]')).toBeVisible();
    await expect(page.getByTestId('composition').locator('tr[data-key="document_sources"]')).toContainText('Fonti documentali');
    await expect(page.getByTestId('composition-caption')).toContainText('questa richiesta');
    await noHorizontalScroll(page);

    // --- Analizza (comando esplicito): nessun invio prima del clic; il risultato arriva dal server simulato.
    expect(fake.count('POST', /\/analyses$/)).toBe(0);
    await activate(page, page.getByTestId('analyze'));
    await expect(page.getByTestId('run-state')).toBeVisible();
    expect(fake.count('POST', /\/analyses$/)).toBe(1);
    const run = fake.project().runs[0];
    await completeDocumentAnalysis(page, fake, run['id'] as string, sources);
    fake.project().usage = usageOf({ kind: 'analysis', refId: run['id'] as string, metrics: metricsOf({ phase: 'analysis' }) });
    (fake.project().usage!['last'] as Record<string, unknown>)['execution'] = executionOf({ regime: 'Extended', reason: 'workload_unknown' });
    await fake.send(page, 'chat.state', done.turn);   // il monitor si riallinea a un evento del progetto

    await expect(page.getByTestId('preview')).toBeVisible();
    await expect(page.getByTestId('validation-badge')).toBeVisible();
    await expect(page.locator('[data-section=requirements] [data-testid=item]')).toHaveCount(sources.length);
    await expect(page.getByTestId('execution-regime')).toContainText('Regime: Esteso');
    await expect(page.getByTestId('execution-extended-note')).toContainText('prosegue in background');
    await expect(page.getByTestId('execution-reason')).toContainText(/.+/);

    // Ogni voce Documented rimanda al suo passaggio (Vedi nei documenti) e il passaggio citato è quello del documento.
    const firstItem = page.locator('[data-section=requirements] [data-testid=item]').first();
    await expect(firstItem.getByTestId('item-sources')).toHaveText('Vedi nei documenti');
    await activate(page, firstItem.getByTestId('item-sources'));
    await expect(firstItem.getByTestId('part-quote')).toContainText(sources[0].quote.slice(0, 20));
    await expect(firstItem.getByTestId('passage-open')).toContainText(sources[0].quote.slice(0, 20));
    await page.screenshot({ path: `${screens}/T066-fake-${info.project.name}-anteprima.png`, fullPage: true });

    // --- Modifica PM: lo schema 1.1.0 si conserva e le fonti documentali restano identiche; nessuna conferma implicita.
    const before = JSON.stringify((fake.project().revisions[0]['content'] as { sources: unknown[] }).sources);
    const text = page.locator('[data-section=requirements] [data-testid=item-text]').first();
    await text.fill(`${sources[0].statement} (verificato dal PM)`);
    await activate(page, page.getByTestId('save'));
    await expect(page.getByTestId('dirty-badge')).toBeHidden();
    const puts = fake.callsTo('PUT', /\/preview$/);
    expect(puts).toHaveLength(1);
    const body = puts[0].body as { schemaVersion: string; sources: unknown[]; requirements: { classification: string; text: string }[] };
    expect(body.schemaVersion).toBe('1.1.0');
    expect(JSON.stringify(body.sources)).toBe(before);
    expect(body.requirements[0].classification).toBe('Documented');
    expect(puts[0].ifMatch).toBe('"rev-1"');
    expect(fake.project().revisions).toHaveLength(2);
    expect(fake.callsTo('POST', /\/confirm$/)).toHaveLength(0);

    // --- Conferma (nessuna generazione implicita), poi Genera come comando esplicito.
    await activate(page, page.getByTestId('confirm'));
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    expect(fake.callsTo('POST', /\/confirm$/)).toHaveLength(1);
    expect(fake.count('POST', /document-generations$/), 'la conferma non genera').toBe(0);
    await activate(page, page.getByTestId('generate'));
    await expect(page.getByTestId('generation-state')).toBeVisible();
    expect(fake.callsTo('POST', /document-generations$/)).toHaveLength(1);
    expect((fake.callsTo('POST', /document-generations$/)[0].body as { revisionId: string }).revisionId).toBe('rev-2');
    const generation = fake.project().runs.find((r) => r['kind'] === 'Document')!;
    await completeDocumentDraft(page, fake, generation['id'] as string);

    // --- Markdown con fonti: proposta, mai approvata, ogni marcatore e un pulsante che apre il passaggio storico.
    await expect(page.getByTestId('draft')).toBeVisible();
    await expect(page.getByTestId('draft-label')).toContainText('Bozza AI — da revisionare');
    await expect(page.getByTestId('draft-status')).toContainText(/Proposta|proposta|Non approvata|non approvata/);
    const chipsDraft = page.getByTestId('markdown').getByTestId('source-chip');
    await expect(chipsDraft).toHaveCount(sources.length);
    await expect(page.getByTestId('source-chip-unknown')).toHaveCount(0);
    await activate(page, chipsDraft.nth(0));
    await expect(page.getByTestId('focused-source')).toBeVisible();
    await page.screenshot({ path: `${screens}/T066-fake-${info.project.name}-bozza.png`, fullPage: true });
    await noHorizontalScroll(page);
    expect(await axeViolations(page)).toEqual([]);

    // Un solo comando per ciascun passo, nessun comando AI nascosto, nessun errore in console.
    expect(commands().map((c) => `${c.method} ${c.path}`)).toEqual([
      `POST /api/projects/${P}/messages`, `POST /api/projects/${P}/analyses`, `PUT /api/projects/${P}/analyses/r-1/preview`,
      `POST /api/projects/${P}/analyses/r-1/confirm`, `POST /api/projects/${P}/analyses/r-1/document-generations`,
    ]);
    expect(consoleErrors).toEqual([]);
  });
});

// ============================================================================================================== errori e falsi Completed

test.describe('[E2E fake] errori: nessun falso Completed', () => {
  test('documento non pronto blocca Analizza; run Failed e Genera fallita non producono anteprima, conferma o bozza; retry da tastiera', async ({ page }, info) => {
    let blockAnalyze = true;
    await page.route(/\/api\/projects\/p-1\/analyses$/, async (route) => {
      if (blockAnalyze && route.request().method() === 'POST') {
        return route.fulfill({
          status: 409, contentType: 'application/json',
          body: JSON.stringify({ status: 409, code: 'sources_not_ready', title: 'sources_not_ready', blockingSources: [{ displayName: 'pdf-text.pdf', version: 1, status: 'Failed' }] }),
        });
      }
      return route.fallback();
    });
    parsing.outcomes = ['Failed', 'Ready'];
    fake.addPm(P, 'Cosa prevede il capitolato?');
    await openProject(page);
    await connected(page);
    await tab(page, 'tab-documents');
    await upload(page, 'pdf-text.pdf');
    const row = item(page, 'pdf-text.pdf');
    await expect(row.getByTestId('docs-status')).toHaveText('Preparazione non riuscita');
    await expect(page.getByTestId('docs-blocking')).toBeVisible();
    expect(sha256(Buffer.from(fake.project().documents[0].versions[0].bytes))).toBe(sha256(fixture('pdf-text.pdf')));   // l'originale e quello caricato

    // Analizza con una fonte non pronta: nessun run, il PM vede quale documento blocca.
    await tab(page, 'tab-work');
    await activate(page, page.getByTestId('analyze'));
    await expect(page.getByTestId('command-error')).toContainText('pdf-text.pdf v1 (Failed)');
    await expect(page.getByTestId('run-state')).toHaveCount(0);
    await expect(page.getByTestId('preview')).toHaveCount(0);
    expect(fake.project().runs).toHaveLength(0);

    // Retry esplicito e da tastiera del parsing (un solo nuovo job); la fonte diventa pronta.
    await tab(page, 'tab-documents');
    await activate(page, row.getByTestId('docs-versions'));
    await activate(page, row.getByTestId('docs-retry'));
    await expect(row.getByTestId('docs-status')).toHaveText('Pronto');
    expect(parsing.retryJobsCreated).toBe(1);
    sources = documentSources(fake);
    blockAnalyze = false;

    // Run di analisi fallito (output del modello rifiutato dal server): nessuna anteprima, nessuna conferma, nessuna bozza.
    await tab(page, 'tab-work');
    await activate(page, page.getByTestId('analyze'));
    await expect(page.getByTestId('run-state')).toBeVisible();
    const failedRun = fake.project().runs[0];
    const failed = fake.setRun(P, failedRun['id'] as string, { status: 'Failed', completedAt: '2030-01-01T10:06:00Z', error: { code: 'work_output_invalid', message: null } });
    await fake.send(page, 'processing.state', failed);
    await expect(page.getByTestId('run-error')).toBeVisible();
    await expect(page.getByTestId('preview')).toHaveCount(0);
    await expect(page.getByTestId('confirm')).toHaveCount(0);
    await expect(page.getByTestId('generate')).toHaveCount(0);
    await expect(page.getByTestId('draft')).toHaveCount(0);
    expect(fake.project().analysisId).toBeNull();
    await page.screenshot({ path: `${screens}/T066-fake-${info.project.name}-run-fallito.png`, fullPage: true });

    // Nuova analisi (chiave nuova) riuscita: conferma e Genera; il run di generazione fallisce => nessuna bozza e Genera resta disponibile.
    await activate(page, page.getByTestId('analyze'));
    await expect.poll(() => fake.project().runs.length).toBe(2);
    const keys = fake.callsTo('POST', /\/analyses$/).map((c) => c.key);
    expect(keys.at(-1)).not.toBe(keys[0]);
    await completeDocumentAnalysis(page, fake, fake.project().runs[1]['id'] as string, sources);
    await expect(page.getByTestId('preview')).toBeVisible();
    await activate(page, page.getByTestId('confirm'));
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    await activate(page, page.getByTestId('generate'));
    await expect(page.getByTestId('generation-state')).toBeVisible();
    const generation = fake.project().runs.find((r) => r['kind'] === 'Document')!;
    const failedGeneration = fake.setRun(P, generation['id'] as string, { status: 'Failed', completedAt: '2030-01-01T10:08:00Z', error: { code: 'work_output_invalid', message: null } });
    await fake.send(page, 'processing.state', failedGeneration);
    await expect(page.getByTestId('generation-error')).toBeVisible();
    await expect(page.getByTestId('draft')).toHaveCount(0);
    await expect(page.getByTestId('generate')).toBeVisible();
    expect(fake.project().draft).toBeNull();
    await noHorizontalScroll(page);
    expect(consoleErrors).toEqual([]);
  });
});

// ============================================================================================================== fonti storiche

test.describe('[E2E fake] fonti storiche', () => {
  test("dopo una nuova versione l'analisi confermata cita ancora il passaggio della versione originale e Genera usa la revisione confermata", async ({ page }) => {
    fake.addPm(P, 'Cosa prevede il documento?');
    await openProject(page);
    await connected(page);
    await tab(page, 'tab-documents');
    await upload(page, 'md-basic.md');
    await expect(item(page, 'md-basic.md').getByTestId('docs-status')).toHaveText('Pronto');
    sources = documentSources(fake);
    const v1Quote = sources[0].quote;
    await tab(page, 'tab-work');
    await activate(page, page.getByTestId('analyze'));
    await expect(page.getByTestId('run-state')).toBeVisible();
    await completeDocumentAnalysis(page, fake, fake.project().runs[0]['id'] as string, sources);
    await expect(page.getByTestId('preview')).toBeVisible();
    await activate(page, page.getByTestId('confirm'));
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();

    // Nuova versione v2 pronta dopo la conferma: il server simulato segnala che le fonti sono cambiate.
    const document = fake.project().documents[0];
    const v2 = { id: 'v-new', version: 2, originalName: 'md-basic.md', mediaType: 'text/markdown', bytes: Buffer.from('# Piano di rilascio v2\n'), uploadedAt: '2030-01-01T10:20:00Z' };
    document.versions.push(v2);
    parsing.onUpload(v2);
    fake.sourceChanges = true;
    const commandsBefore = commands().length;
    await page.reload();
    await expect(page.getByRole('heading', { level: 1, name: 'Aurora' })).toBeVisible();

    // La conferma resta valida, l'avviso lo dichiara senza toccare i comandi, la voce rimanda ancora al passaggio della v1.
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    await expect(page.getByTestId('source-changes')).toContainText('Le fonti documentali sono cambiate dopo questa analisi');
    await expect(page.getByTestId('generate')).toBeEnabled();
    const firstItem = page.locator('[data-section=requirements] [data-testid=item]').first();
    await activate(page, firstItem.getByTestId('item-sources'));
    await expect(firstItem.getByTestId('part-quote')).toContainText(v1Quote.slice(0, 20));
    await expect(firstItem.getByTestId('passage-open')).toContainText(v1Quote.slice(0, 20));
    expect(commands().length, 'rilettura e avviso non creano job né analisi').toBe(commandsBefore);

    // Genera usa la revisione confermata (non rielabora le fonti nuove).
    await activate(page, page.getByTestId('generate'));
    await expect(page.getByTestId('generation-state')).toBeVisible();
    expect((fake.callsTo('POST', /document-generations$/)[0].body as { revisionId: string }).revisionId).toBe('rev-1');
    await completeDocumentDraft(page, fake, fake.project().runs.find((r) => r['kind'] === 'Document')!['id'] as string);
    await expect(page.getByTestId('markdown').getByTestId('source-chip').first()).toBeVisible();
    await noHorizontalScroll(page);
    expect(consoleErrors).toEqual([]);
  });
});
