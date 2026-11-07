import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { FakeParsing } from './support/fake-parsing';
import { FlowStack, completeDocumentAnalysis, documentSources, evidenceEnvelopes, installFlowRoutes, type DocSource } from './support/fake-document-flow';
import { coverageReport, installEvidenceRoutes, multipassExecution, setExecution, twoGroupSourceMap } from './support/fake-long-context';
import { metricsOf, usageOf } from './support/context-usage';
import { documentFixturesDir } from './support/documents';

// T092 (E2E-OFFLINE, browser): documenti oltre contesto con API simulate nel browser (fake-stack: support/fake-stack.ts + fake-parsing.ts +
// fake-document-flow.ts + fake-long-context.ts), a 1440 e 390 px, con la TASTIERA per i comandi. Prova la UI e il contratto HTTP letto dal
// browser: progressi multipasso (unita pianificate finora, mai percentuali di completamento), regime letto dal server (nessuna
// riclassificazione N->E dedotta dalla latenza), chat con fonti lontane, copertura 100% senza certificazione di fedelta, copertura parziale,
// alternative preservate, errori e limiti senza falsi Completed, fonti multi-blocco con gruppi dalla mappa persistita e disconnessione.
// NON prova server, parser, scansione di tutte le unita, riduzione gerarchica, commit/assembly e ripresa: sono provati da
// backend/tests/bProjects.IntegrationTests (DocumentLongChatTests T084/T089, T091 e T078/T123 per analisi e routing). Il livello
// "backend + PostgreSQL + parser reali con modello finto" del task e le prove DGX NON girano da qui (vedi validation/I6-deterministic.md).

const P = 'p-1';
const screens = 'test-results/screens';
const mediaTypes: Record<string, string> = {
  pdf: 'application/pdf', docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', md: 'text/markdown', txt: 'text/plain',
};
const files = ['md-basic.md', 'txt-conflict-a.txt', 'pdf-text.pdf', 'docx-basic.docx'];

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
});

async function openProject(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill('pm');
  await page.getByLabel('Password').fill('non-un-segreto');
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible();
  await page.goto(`/projects/${P}`);
  await expect(page.getByRole('heading', { level: 1, name: 'Aurora' })).toBeVisible();
  await expect.poll(async () => (await fake.connections(page)).filter((c) => c.open && c.project === P).length).toBe(1);
}

/** Attiva un controllo con la tastiera: focus reale (verificato) e Invio, mai un clic del puntatore. */
async function activate(page: Page, locator: ReturnType<Page['getByTestId']>) {
  await expect(async () => {
    await locator.focus({ timeout: 1_000 });
    await expect(locator).toBeFocused({ timeout: 500 });
  }).toPass({ timeout: 8_000 });
  await page.keyboard.press('Enter');
}

const tab = (page: Page, id: 'tab-work' | 'tab-documents' | 'tab-evidence') => activate(page, page.getByTestId(id));

async function noHorizontalScroll(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), 'scroll orizzontale').toBe(true);
}

/** Carica i quattro formati (fixture reali) e rende disponibili le fonti documentali pronte del server simulato. */
async function prepareDocuments(page: Page) {
  await tab(page, 'tab-documents');
  for (const name of files) {
    await page.getByTestId('docs-file').setInputFiles({ name, mimeType: mediaTypes[name.split('.').pop()!], buffer: readFileSync(join(documentFixturesDir, name)) });
    await activate(page, page.getByTestId('docs-upload'));
    await expect(page.getByTestId('docs-item').filter({ hasText: name })).toHaveCount(1);
    await expect(page.getByTestId('docs-item').filter({ hasText: name }).getByTestId('docs-status')).toHaveText('Pronto');
  }
  sources = documentSources(fake);
  expect(sources.length).toBeGreaterThanOrEqual(4);
  await tab(page, 'tab-work');
}

/** Tre fonti di documenti diversi (zone lontane del corpus). */
function farSources(): DocSource[] {
  const byDocument = new Map<string, DocSource>();
  for (const s of sources) if (!byDocument.has(s.documentId)) byDocument.set(s.documentId, s);
  const far = [...byDocument.values()];
  expect(far.length).toBeGreaterThanOrEqual(3);
  return far.slice(0, 3);
}

async function ensureUsagePanel(page: Page) {
  if (page.viewportSize()!.width < 1200 && (await page.getByTestId('usage-execution').count()) === 0) await activate(page, page.getByTestId('usage-toggle'));
  await expect(page.getByTestId('usage-execution')).toBeVisible();
}

/** Scritture inviate dal browser, escluse autenticazione e upload. */
const commands = () => fake.calls.filter((c) => c.method !== 'GET' && !c.path.startsWith('/api/auth/') && !/\/documents(\/|$)/.test(c.path));

/** Il server completa Analizza senza alcuna notifica SSE (evento perso): la UI deve riconciliarsi con i GET. */
function completeAnalysisSilently(runId: string) {
  const p = fake.project();
  p.analysisId = runId;
  p.revisions = [{ revisionId: 'rev-1', origin: 'Ai', content: documentContent(runId), validationState: 'Unvalidated' }];
  fake.setRun(P, runId, { status: 'Completed', completedAt: '2030-01-01T10:06:00Z', analysisId: runId, resultRevisionId: 'rev-1' });
}

function documentContent(runId: string): Record<string, unknown> {
  // Stessa forma di completeDocumentAnalysis, senza inviare l'evento.
  const requirements = sources.map((s, i) => ({ id: `req-${i + 1}`, text: s.statement, classification: 'Documented', evidenceRefs: [s.sourceId] }));
  return {
    schemaVersion: '1.1.0', analysisId: runId, projectId: P, sourceCutoffSequence: fake.project().messages.length, validationState: 'Unvalidated',
    sources: sources.map((s) => ({
      id: s.sourceId, kind: 'DocumentPassage', messageId: null, messageRole: null, revisionId: null, documentId: s.documentId, documentVersionId: s.documentVersionId,
      preparationId: s.preparationId, artifactId: s.artifactId, blockId: s.blockId, start: s.start, end: s.end, quote: s.quote,
    })),
    goals: [], contextAndScope: { overview: [], included: [], excluded: [] }, requirements,
    operationalFlows: [], constraints: [], dependencies: [], assumptions: [], openQuestions: [], conflicts: [],
  };
}

// ============================================================================================================== chat multipasso

test.describe('[E2E fake] documenti oltre contesto: chat', () => {
  test('progressi multipasso come unita pianificate finora, regime dal server senza deduzioni dalla latenza e fonti di tre zone lontane', async ({ page }, info) => {
    fake.addPm(P, 'Cosa dicono le tre parti lontane del corpus?');
    await openProject(page);
    await prepareDocuments(page);
    const far = farSources();
    const turnId = fake.project().turns[0]['id'] as string;
    fake.setTurn(P, turnId, { status: 'Running', startedAt: '2030-01-01T10:00:05Z' });
    const running = fake.project().turns[0];
    fake.project().usage = usageOf({ kind: 'chat', refId: 'm-1', metrics: metricsOf({ phase: 'chat', state: 'running', completion: null }) });
    const progress = async (execution: Record<string, unknown>) => {
      setExecution(fake, execution);
      await fake.send(page, 'chat.state', running);
    };

    // a) Latenza lunga ma regime Ordinario deciso dal server: la UI non lo riclassifica in Esteso da sola.
    await progress(multipassExecution({ stage: 'scan', completed: 0, planned: 1, elapsedMs: 400_000, regime: 'Ordinary' }));
    await ensureUsagePanel(page);
    await expect(page.getByTestId('execution-regime')).toContainText('Regime: Ordinario');
    await expect(page.getByTestId('execution-regime-change')).toHaveCount(0);
    await expect(page.getByTestId('execution-extended-note')).toHaveCount(0);
    await expect(page.getByTestId('execution-units')).toHaveText('Unità: 0 di 1 pianificate finora');

    // b) Promozione decisa dal server (lavoro aggiuntivo): cambio dichiarato con ricevuta; le unita restano contatori, mai percentuali.
    await progress(multipassExecution({ stage: 'scan', completed: 1, planned: 3, elapsedMs: 410_000, regime: 'Extended', initialRegime: 'Ordinary' }));
    await expect(page.getByTestId('execution-regime-change')).toContainText('Da Ordinario a Esteso');
    await expect(page.getByTestId('execution-promotion-receipt')).toBeVisible();
    await expect(page.getByTestId('execution-extended-note')).toContainText('prosegue in background');
    await expect(page.getByTestId('execution-units')).toHaveText('Unità: 1 di 3 pianificate finora');

    // c) Il piano cresce con gli output intermedi: il denominatore aumenta e nessun avanzamento e presentato come percentuale o completamento.
    await progress(multipassExecution({ stage: 'scan', completed: 2, planned: 5, elapsedMs: 600_000, regime: 'Extended', initialRegime: 'Ordinary' }));
    await expect(page.getByTestId('execution-units')).toHaveText('Unità: 2 di 5 pianificate finora');
    await progress(multipassExecution({ stage: 'reduce', completed: 5, planned: 6, elapsedMs: 900_000, regime: 'Extended', initialRegime: 'Ordinary' }));
    await expect(page.getByTestId('execution-units')).toHaveText('Unità: 5 di 6 pianificate finora');
    expect(await page.getByTestId('usage-execution').innerText()).not.toMatch(/%|completat[oa] al|100/i);
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(0);   // nessun risultato finche il server non completa
    await page.screenshot({ path: `${screens}/T092-fake-${info.project.name}-chat-progresso.png`, fullPage: true });

    // d) Risposta finale con fonti di tre documenti diversi: ogni fonte si apre da tastiera sul proprio passaggio.
    const done = fake.completeTurn(P, turnId, `Zona A [fonte:${far[0].sourceId}]; zona B [fonte:${far[1].sourceId}]; zona C [fonte:${far[2].sourceId}].`, metricsOf({ phase: 'chat', calls: 6 }));
    await fake.send(page, 'chat.state', done.turn);
    const assistant = page.locator('article[data-role="assistant"]');
    await expect(assistant).toHaveCount(1);
    const chips = assistant.getByTestId('chat-source');
    await expect(chips).toHaveCount(3);
    for (let i = 0; i < 3; i++) {
      await activate(page, chips.nth(i));
      await expect(assistant.getByTestId('part-quote').first()).toContainText(far[i].quote.slice(0, 20));
    }
    expect(commands().map((c) => `${c.method} ${c.path}`), 'la chat invia solo il messaggio').toEqual([]);
    await noHorizontalScroll(page);
    expect(consoleErrors).toEqual([]);
  });

  test('limite documentale: nessuna risposta parziale presentata come completa e la chat resta utilizzabile', async ({ page }) => {
    fake.addPm(P, 'Riassumi tutti i documenti.');
    await openProject(page);
    await prepareDocuments(page);
    const turnId = fake.project().turns[0]['id'] as string;
    const failed = fake.failTurn(P, turnId, 'document_limit_exceeded', null);
    await fake.send(page, 'chat.state', failed);
    await expect(page.locator('.turn-error')).toContainText('superano la capacità gestibile dal modello');
    await expect(page.locator('.turn-error')).toContainText('Il messaggio resta salvato');
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(0);
    await expect(page.getByLabel("Messaggio per l'assistente")).toBeEnabled();
    await noHorizontalScroll(page);
    expect(consoleErrors).toEqual([]);
  });
});

// ============================================================================================================== analisi, copertura e bozza

test.describe('[E2E fake] documenti oltre contesto: analisi, copertura e Markdown', () => {
  test('analisi multipasso, copertura al 100% senza certificazione, copertura parziale, errore e alternative preservate', async ({ page }, info) => {
    fake.addPm(P, 'Analizza i documenti del progetto.');
    await openProject(page);
    await prepareDocuments(page);
    await activate(page, page.getByTestId('analyze'));
    await expect(page.getByTestId('run-state')).toBeVisible();
    const runId = fake.project().runs[0]['id'] as string;
    fake.project().usage = usageOf({ kind: 'analysis', refId: runId, metrics: metricsOf({ phase: 'analysis', state: 'running', completion: null }) });
    setExecution(fake, multipassExecution({ stage: 'scan', completed: 3, planned: 8, elapsedMs: 120_000 }));
    await fake.send(page, 'processing.state', fake.setRun(P, runId, { status: 'Running', startedAt: '2030-01-01T10:00:05Z' }));
    await ensureUsagePanel(page);
    await expect(page.getByTestId('execution-units')).toHaveText('Unità: 3 di 8 pianificate finora');
    await expect(page.getByTestId('preview')).toHaveCount(0);
    await completeDocumentAnalysis(page, fake, runId, sources);
    await expect(page.getByTestId('preview')).toBeVisible();

    // --- Copertura: 100% per ID + alternative e frontiera integralmente mostrate; nessun badge di fedelta o completezza.
    let reports: Record<string, unknown>[] = [];
    let next: () => { status: number; body: Record<string, unknown> };
    const stored: Record<string, unknown>[] = [];
    await installEvidenceRoutes(page, () => evidenceEnvelopes(sources), { onCreate: () => next(), stored });
    const n = sources.length;
    const base = { analysisId: runId, revisionId: 'rev-1', expected: n, represented: n };
    next = () => ({
      status: 201,
      body: coverageReport({
        ...base, id: 'cov-1', portionsExpected: 6, portionsProcessed: 6,
        findings: [{ category: 'SemanticRisk', code: 'negation_lost', message: 'Una negazione potrebbe essere stata persa.', evidenceId: 'ev-1', alias: null, itemId: 'req-1', section: 'requirements', confidence: 'low', details: null }],
        reconciliation: {
          projected: true, levels: [{ view: 'documents', level: 1, nodes: 6, pages: 2 }], candidates: [], patches: [], frontier: [{ candidateId: 'cand-9', reason: 'limite di profondità' }],
          openPoints: [{
            kind: 'conflict', id: 'conf-1', description: 'Le schede indicano scadenze diverse',
            alternatives: [
              { itemId: 'alt-1', text: 'Scadenza a 30 giorni', classification: 'Documented', evidenceRefs: [] },
              { itemId: 'alt-2', text: 'Scadenza a 60 giorni', classification: 'Documented', evidenceRefs: [] },
            ],
          }],
        },
      }),
    });
    reports = stored;
    await tab(page, 'tab-evidence');
    await expect(page.getByTestId('verify-coverage')).toBeEnabled();
    await activate(page, page.getByTestId('verify-coverage'));
    await expect(page.getByTestId('report')).toBeVisible();
    await expect(page.getByTestId('id-coverage')).toContainText(`${n} di ${n} evidenze attese collegate a un elemento`);
    await expect(page.getByTestId('id-coverage')).toContainText('porzioni elaborate 6 di 6');
    await expect(page.getByTestId('id-coverage-note')).toContainText('nessuna copertura, anche al 100%, certifica il significato');
    await expect(page.getByTestId('blocking-warning')).toHaveCount(0);
    await expect(page.getByTestId('group-SemanticRisk').getByTestId('finding')).toHaveCount(1);
    await expect(page.getByTestId('open-alternative')).toHaveText(['Scadenza a 30 giorni', 'Scadenza a 60 giorni']);   // alternative preservate, nessuna scelta
    await expect(page.getByTestId('reconciliation-frontier')).toContainText('cand-9');
    expect(await page.getByTestId('report').innerText()).not.toMatch(/\b(fedelt[àa] (verificata|certificata)|risultato completo|verifica completata|copertura completa)\b/i);
    expect(reports).toHaveLength(1);
    await page.screenshot({ path: `${screens}/T092-fake-${info.project.name}-copertura.png`, fullPage: true });

    // --- Copertura parziale: porzioni mancanti + segnalazione bloccante => la verifica non e chiusa.
    next = () => ({
      status: 201,
      body: coverageReport({
        ...base, id: 'cov-2', represented: n - 1, portionsExpected: 6, portionsProcessed: 4, blocking: 1,
        findings: [{ category: 'DeterministicError', code: 'portion_missing', message: 'Due porzioni dei documenti non sono state elaborate.', evidenceId: null, alias: null, itemId: null, section: null, confidence: null, details: null, blocking: true }],
      }),
    });
    await activate(page, page.getByTestId('verify-coverage'));
    await expect(page.getByTestId('report-meta')).toContainText('coverage-2');
    await expect(page.getByTestId('id-coverage')).toContainText(`${n - 1} di ${n} evidenze attese`);
    await expect(page.getByTestId('id-coverage')).toContainText('porzioni elaborate 4 di 6 (2 mancanti)');
    await expect(page.getByTestId('blocking-warning')).toContainText('la verifica non è chiusa');
    await expect(page.getByTestId('open-alternative')).toHaveCount(0);

    // --- Errore del server: messaggio esplicito, nessun nuovo report e niente conferma di esito.
    next = () => ({ status: 500, body: { status: 500, code: 'internal_error', title: 'internal_error' } });
    await activate(page, page.getByTestId('verify-coverage'));
    await expect(page.getByTestId('coverage-error')).toContainText('Verifica di copertura non riuscita');
    expect(stored).toHaveLength(2);
    await noHorizontalScroll(page);
    expect(commands().map((c) => `${c.method} ${c.path}`)).toEqual([`POST /api/projects/${P}/analyses`]);
    expect(consoleErrors).toEqual([]);
  });

  test('bozza con fonti multi-blocco: due citazioni con parti distinte restano distinte, anche dopo una nuova versione del documento', async ({ page }, info) => {
    fake.addPm(P, 'Analizza i documenti del progetto.');
    await openProject(page);
    await prepareDocuments(page);
    await activate(page, page.getByTestId('analyze'));
    await expect(page.getByTestId('run-state')).toBeVisible();
    await completeDocumentAnalysis(page, fake, fake.project().runs[0]['id'] as string, sources);
    await activate(page, page.getByTestId('confirm'));
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    await activate(page, page.getByTestId('generate'));
    await expect(page.getByTestId('generation-state')).toBeVisible();
    const generation = fake.project().runs.find((r) => r['kind'] === 'Document')!;

    // Bozza servita dal server: le citazioni sono definite dalla mappa persistita (stesso header, due gruppi), non dalla sequenza dei marcatori.
    const [a, b, c] = sources;
    const { map, groups } = twoGroupSourceMap(sources);
    const p = fake.project();
    p.draft = {
      id: 'd-1', projectId: P, processingRunId: generation['id'], analysisId: p.analysisId, analysisRevisionId: 'rev-1', analysisVersion: 1,
      markdown: `# Requisiti\n\n- Prima citazione [fonte:${a.sourceId}] [fonte:${b.sourceId}]\n- Seconda citazione [fonte:${a.sourceId}] [fonte:${c.sourceId}]\n`,
      createdAt: '2030-01-01T10:08:00Z', status: 'Proposed', approvalStatus: 'NotApproved', hasNewNotes: false, hasNewRevision: false,
      label: 'Bozza AI — da revisionare', sourceMap: map,
    };
    const run = fake.setRun(P, generation['id'] as string, { status: 'Completed', completedAt: '2030-01-01T10:08:00Z', draftId: 'd-1' });
    await fake.send(page, 'processing.state', run);
    await expect(page.getByTestId('draft')).toBeVisible();
    await expect(page.getByTestId('draft-label')).toContainText('Bozza AI — da revisionare');
    const chips = page.getByTestId('markdown').getByTestId('source-chip');
    await expect(chips).toHaveCount(4);
    await expect(page.getByTestId('source-chip-unknown')).toHaveCount(0);

    const openFirstChip = async () => {
      await activate(page, chips.nth(0));
      const focused = page.getByTestId('focused-source');
      await expect(focused).toHaveCount(2);
      await expect(page.getByTestId('focused-group-label')).toHaveText(['Citazione 1 di 2', 'Citazione 2 di 2']);
      await expect(page.getByTestId('source-notice')).toContainText('appartiene a 2 citazioni distinte');
      for (let g = 0; g < 2; g++) {
        const parts = focused.nth(g).getByTestId('source-part');
        await expect(parts).toHaveCount(2);                                   // ciascuna citazione ha le proprie due parti
        await expect(parts.nth(0).getByTestId('part-quote')).toContainText(groups[g][0].quote.slice(0, 20));
        await expect(parts.nth(1).getByTestId('part-quote')).toContainText(groups[g][1].quote.slice(0, 20));
      }
    };
    await openFirstChip();
    // Una fonte che appartiene a una sola citazione mostra solo il suo gruppo, senza etichetta «x di y».
    await activate(page, chips.nth(1));
    await expect(page.getByTestId('focused-source')).toHaveCount(1);
    await expect(page.getByTestId('focused-group-label')).toHaveCount(0);
    await page.screenshot({ path: `${screens}/T092-fake-${info.project.name}-fonti-multiblocco.png`, fullPage: true });

    // Nuova versione del documento dopo la bozza: la mappa storica continua ad aprire le stesse parti originali.
    const document = fake.project().documents[0];
    const v2 = { id: 'v-new', version: 2, originalName: document.versions[0].originalName, mediaType: 'text/markdown', bytes: Buffer.from('# v2\n'), uploadedAt: '2030-01-01T10:20:00Z' };
    document.versions.push(v2);
    parsing.onUpload(v2);
    fake.sourceChanges = true;
    const before = commands().length;
    await page.reload();
    await expect(page.getByTestId('draft')).toBeVisible();
    await openFirstChip();
    expect(commands().length, 'riapertura e riletture non creano comandi').toBe(before);
    await noHorizontalScroll(page);
    expect(consoleErrors).toEqual([]);
  });
});

// ============================================================================================================== errori, limiti e disconnessione

test.describe('[E2E fake] documenti oltre contesto: errori, limiti e disconnessione', () => {
  test('analisi con copertura incompleta o contesto oltre il limite: nessuna anteprima, conferma o bozza; nuova chiave al retry', async ({ page }) => {
    fake.addPm(P, 'Analizza tutto.');
    await openProject(page);
    await prepareDocuments(page);
    await activate(page, page.getByTestId('analyze'));
    await expect(page.getByTestId('run-state')).toBeVisible();
    const first = fake.project().runs[0]['id'] as string;
    await fake.send(page, 'processing.state', fake.setRun(P, first, { status: 'Failed', completedAt: '2030-01-01T10:06:00Z', error: { code: 'coverage_incomplete', message: null } }));
    await expect(page.getByTestId('run-error')).toContainText('La copertura dei documenti non è completa');
    for (const id of ['preview', 'confirm', 'generate', 'draft']) await expect(page.getByTestId(id)).toHaveCount(0);

    await activate(page, page.getByTestId('analyze'));
    await expect.poll(() => fake.project().runs.length).toBe(2);
    const second = fake.project().runs[1]['id'] as string;
    await fake.send(page, 'processing.state', fake.setRun(P, second, {
      status: 'Failed', completedAt: '2030-01-01T10:09:00Z',
      error: { code: 'context_overflow', message: '1200 token oltre il budget di input di 24576 token' },
    }));
    await expect(page.getByTestId('run-error')).toContainText('Contesto misurato: 1200 token');
    await expect(page.getByTestId('run-error')).toContainText('24576');
    for (const id of ['preview', 'confirm', 'generate', 'draft']) await expect(page.getByTestId(id)).toHaveCount(0);
    expect(fake.project().analysisId).toBeNull();
    const keys = fake.callsTo('POST', /\/analyses$/).map((c) => c.key);
    expect(keys[1]).not.toBe(keys[0]);
    await noHorizontalScroll(page);
    expect(consoleErrors).toEqual([]);
  });

  test('disconnessione durante un run multipasso: il risultato perso dallo stream si riconcilia dai GET con una sola connessione e nessun comando', async ({ page }) => {
    fake.addPm(P, 'Analizza tutto.');
    await openProject(page);
    await prepareDocuments(page);
    await activate(page, page.getByTestId('analyze'));
    await expect(page.getByTestId('run-state')).toBeVisible();
    const runId = fake.project().runs[0]['id'] as string;
    fake.project().usage = usageOf({ kind: 'analysis', refId: runId, metrics: metricsOf({ phase: 'analysis', state: 'running', completion: null }) });
    setExecution(fake, multipassExecution({ stage: 'reduce', completed: 4, planned: 5, elapsedMs: 700_000 }));
    await fake.send(page, 'processing.state', fake.setRun(P, runId, { status: 'Running', startedAt: '2030-01-01T10:00:05Z' }));
    await ensureUsagePanel(page);
    await expect(page.getByTestId('execution-units')).toHaveText('Unità: 4 di 5 pianificate finora');
    const commandsBefore = commands().length;

    // Lo stream cade (e la prima riconnessione e rifiutata); intanto il server completa il run senza che il browser riceva l'evento.
    await fake.refuse(page, 503);
    await fake.drop(page);
    completeAnalysisSilently(runId);
    setExecution(fake, multipassExecution({ stage: 'completed', completed: 5, planned: 5, elapsedMs: 760_000 }));

    await expect(page.getByTestId('preview')).toBeVisible({ timeout: 40_000 });
    await expect(page.getByTestId('run-state')).toHaveCount(0);
    await expect.poll(async () => (await fake.connections(page)).filter((c) => c.open).length, { timeout: 40_000 }).toBe(1);
    expect(commands().length, 'la riconnessione non emette comandi').toBe(commandsBefore);
    await noHorizontalScroll(page);
    expect(consoleErrors).toEqual([]);
  });
});
