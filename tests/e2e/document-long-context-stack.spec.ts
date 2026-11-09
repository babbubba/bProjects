import { expect, test, type Page } from '@playwright/test';
import { api, stack } from './support/real-stack/client';
import { asFile, createProject, diagnose, docItem, login, openDocumentsTab, unique, uploadDocument, waitReady, workCounts } from './support/real-stack/ui';

// T092, livello [E2E reale + LLM finto] (playwright.real.config.ts): documenti oltre contesto sul backend REALE + PostgreSQL REALE (database isolato) + parser REALE; sono
// finte soltanto le risposte del modello (support/fake-llm.ts). La finestra del backend e piccola e DICHIARATA (Ai__ContextWindowTokens=4000, come i test in-process
// T084/T089/T078): il corpus la supera e il percorso multipasso e quello vero del server (scansione di tutti i gruppi, riduzione, riconciliazione gerarchica, bozza per unita).
// Il livello fake-stack del browser e document-long-context.spec.ts (non gira qui). Risultati distinti in validation/I6-deterministic.md. Nessun DGX.

const screens = 'test-results/screens';
const ZONES = ['ZONAALFA', 'ZONABETA', 'ZONAGAMMA'] as const;
const PARAGRAPHS = 22;
const FILLER = 'il cliente puo avere un modulo con ogni dato per la zona e il suo caso';

/** Documento di una zona: paragrafi numerati e unici, parole corte (< 6 lettere) tranne il nome della zona, cosi la chiave della domanda e solo la zona. */
function zoneDocument(zone: string): Buffer {
  const paragraphs = Array.from({ length: PARAGRAPHS }, (_, i) => `${zone} dice che al punto ${i + 1} ${FILLER} ${i + 1} e ${FILLER} ${zone}.`);
  return Buffer.from(paragraphs.join('\n\n') + '\n', 'utf8');
}

const names = ZONES.map((z) => `${z.toLowerCase()}.txt`);

async function seedProject(page: Page, label: string) {
  await login(page);
  const projectId = await createProject(page, `T092 ${label} ${unique()}`);
  await openDocumentsTab(page);
  for (const [i, zone] of ZONES.entries()) {
    await page.getByTestId('docs-file').setInputFiles(asFile(names[i], zoneDocument(zone)));
    await page.getByTestId('docs-upload').click();
    await expect(docItem(page, names[i])).toHaveCount(1);
  }
  for (const name of names) await waitReady(page, name, 180_000);
  return projectId;
}

test.describe.configure({ mode: 'serial' });

test.describe('[E2E reale + LLM finto] documenti oltre contesto', () => {
  test.beforeAll(async () => {
    await stack.restartBackend({ Ai__ContextWindowTokens: '4000', Ai__MaxOutputTokens: '1500', Ai__SafetyMarginTokens: '20' });
  });

  test.afterAll(async () => {
    await stack.restartBackend({});
  });

  test.beforeEach(async () => {
    await stack.resetData();
    await stack.resetLlm();
  });

  /** Legge periodicamente il pannello «Esecuzione» finche `until` non e vera: restituisce le unita osservate (testo esatto) e il regime mostrato. */
  async function watchProgress(page: Page, until: () => Promise<boolean>, timeoutMs = 240_000) {
    const units: string[] = [];
    let regime = '';
    let percent = false;
    const end = Date.now() + timeoutMs;
    while (Date.now() < end && !(await until())) {
      const u = page.getByTestId('execution-units');
      if (await u.count()) {
        const t = (await u.innerText()).trim();
        if (units.at(-1) !== t) units.push(t);
      }
      const r = page.getByTestId('execution-regime');
      if (await r.count()) regime = (await r.innerText()).trim();
      const panel = page.getByTestId('usage-execution');
      if ((await panel.count()) && /%|completat[oa] al/i.test(await panel.innerText())) percent = true;
      await page.waitForTimeout(250);
    }
    return { units, regime, percent };
  }

  /**
   * Cio che l'interfaccia mostra del lavoro multipasso e solo cio che il server riporta: mai percentuali ne «completato al»; il regime e quello deciso dal server (Esteso, mai
   * riclassificato dal tempo). Le unita pianificate/completate sono dichiarate dal server solo per le preparazioni documentali (InferenceMetrics): per chat e analisi il
   * contratto reale le lascia null e il pannello deve dire «indisponibili» (LIMITE NOTO, registrato in I6-deterministic.md: il fake-stack le simula).
   */
  async function expectTruthfulExecutionPanel(page: Page, progress: { units: string[]; regime: string; percent: boolean }, projectId: string) {
    expect(progress.percent, 'nessuna percentuale o «completato al» nel pannello Esecuzione durante il lavoro').toBe(false);
    for (const u of progress.units) expect(u, 'unita mostrate in corso: solo testo del contratto').toMatch(/^Unità: (indisponibili|\d+ di \d+ pianificate finora|\d+ completate \(pianificazione non disponibile\)|completate indisponibili su \d+ pianificate finora)$/);
    const usage = (await api<{ last: { execution: Record<string, unknown> | null } | null }>('GET', `/api/projects/${projectId}/context-usage`)).body;
    const execution = usage.last?.execution ?? null;
    console.log('[T092 real] execution (API):', JSON.stringify(execution));
    expect(execution, 'il server riporta la telemetria di esecuzione del root completato').not.toBeNull();
    expect(execution!['regime'], 'regime deciso dal server').toBe('Extended');
    expect(execution!['initialRegime']).toBe('Extended');
    await page.getByTestId('tab-work').click();
    if (page.viewportSize()!.width < 1200 && (await page.getByTestId('usage-execution').count()) === 0) await page.getByTestId('usage-toggle').click();
    await expect(page.getByTestId('execution-regime')).toContainText('Regime: Esteso');
    const text = (await page.getByTestId('usage-execution').innerText()).replace(/\s+/g, ' ');
    console.log('[T092 real] pannello Esecuzione:', text);
    expect(text).not.toMatch(/%|completat[oa] al/i);
    expect(await page.getByTestId('execution-units').innerText()).toMatch(/^Unità: /);
  }

  test('chat oltre contesto: scansione di TUTTE le evidenze, riduzione gerarchica, pannello senza percentuali e con il regime del server, tre fonti lontane apribili', async ({ page }) => {
    test.setTimeout(600_000);
    const projectId = await seedProject(page, 'chat');
    await stack.llmConfig({ noteWords: 800 });                                       // note grandi: la riduzione gerarchica e necessaria
    await stack.fault({ graph: 'doc-chat-scan', times: 40, kind: 'delay', delayMs: 1_200 });
    const documentEvidences = (await stack.sql<{ n: number }>(`SELECT count(*)::int n FROM "Evidences" WHERE "OriginKind"::text = 'Document'`))[0].n;
    expect(documentEvidences, 'il corpus supera la finestra: molte evidenze').toBeGreaterThan(30);
    const writesBefore: string[] = [];
    page.on('request', (r) => {
      if (r.method() !== 'GET' && r.url().includes('/api/') && !r.url().includes('/api/auth/')) writesBefore.push(`${r.method()} ${new URL(r.url()).pathname}`);
    });

    await page.getByTestId('tab-work').click();
    const box = page.getByLabel("Messaggio per l'assistente");
    await box.fill('Cosa prevedono ZONAALFA ZONABETA ZONAGAMMA?');
    await box.press('Control+Enter');
    const assistant = page.locator('article[data-role="assistant"]');
    const progress = await watchProgress(page, async () => (await assistant.count()) + (await page.locator('.turn-error').count()) > 0);
    console.log('[T092 real] progressi chat:', JSON.stringify(progress));
    await expect(page.locator('.turn-error'), 'la chat oltre contesto non fallisce').toHaveCount(0);
    await expect(assistant).toHaveCount(1);
    await expectTruthfulExecutionPanel(page, progress, projectId);

    // --- Il server ha letto TUTTE le evidenze: ogni evidenza compare in un gruppo di scansione, e l'insieme dei gruppi e esattamente il corpus.
    const llm = await stack.llm();
    expect(llm.unexpected).toEqual([]);
    const scans = llm.requests.filter((r) => r.graph === 'doc-chat-scan');
    const reduces = llm.requests.filter((r) => r.graph === 'doc-chat-reduce');
    const finals = llm.requests.filter((r) => r.graph === 'doc-chat');
    const seen = scans.flatMap((r) => [...r.text.matchAll(/<<<EVIDENZA[^\n]*>>>\n([\s\S]*?)<<<FINE EVIDENZA>>>/g)].map((m) => m[1]));
    expect(scans.length, 'piu gruppi di scansione').toBeGreaterThan(2);
    expect(seen.length, 'ogni evidenza dello snapshot e in esattamente un gruppo').toBe(documentEvidences);
    expect(new Set(seen).size).toBe(documentEvidences);
    expect(reduces.length, 'riduzione gerarchica delle note').toBeGreaterThan(0);
    expect(finals).toHaveLength(1);
    const turn = (await stack.sql<{ Status: string; Regime: string; InitialRegime: string; CallCount: number }>(`SELECT "Status"::text, "Regime"::text, "InitialRegime"::text, "CallCount" FROM "ChatTurns"`))[0];
    expect(turn.Status).toBe('Completed');
    expect(turn.Regime, 'regime deciso dal server e mai riclassificato dal tempo').toBe(turn.InitialRegime);
    expect(turn.CallCount).toBe(scans.length + reduces.length + finals.length);

    // --- Tre fonti di zone lontane (documenti diversi), apribili sul proprio passaggio originale.
    const chips = assistant.getByTestId('chat-source');
    await expect(chips).toHaveCount(3);
    for (let i = 0; i < 3; i++) {
      await chips.nth(i).click();
      await expect(assistant.getByTestId('part-quote').first()).toContainText(/ZONA(ALFA|BETA|GAMMA)/);
    }
    expect(await page.getByTestId('source-chip-unknown').count()).toBe(0);
    const quotes = new Set<string>();
    for (let i = 0; i < 3; i++) {
      await chips.nth(i).click();
      quotes.add(/ZONA(ALFA|BETA|GAMMA)/.exec(await assistant.getByTestId('part-quote').first().innerText())![0]);
    }
    expect([...quotes].sort(), 'una fonte per ciascuna zona').toEqual([...ZONES].sort());
    expect(writesBefore.filter((w) => !/\/messages$/.test(w)), 'la chat invia solo il messaggio').toEqual([]);
    await page.screenshot({ path: `${screens}/T092-real-chat.png`, fullPage: true });
    void projectId;
  });

  test('analisi multipasso, copertura, conferma e bozza per unita: PM recupera il risultato completo con tutte le fonti apribili', async ({ page }) => {
    test.setTimeout(900_000);
    const projectId = await seedProject(page, 'analisi');
    await stack.fault({ graph: 'doc-reconcile', times: 80, kind: 'delay', delayMs: 700 });
    const documentEvidences = (await stack.sql<{ n: number }>(`SELECT count(*)::int n FROM "Evidences" WHERE "OriginKind"::text = 'Document'`))[0].n;
    const writes: string[] = [];
    page.on('request', (r) => {
      if (r.method() !== 'GET' && r.url().includes('/api/') && !r.url().includes('/api/auth/')) writes.push(`${r.method()} ${new URL(r.url()).pathname}`);
    });
    await page.getByTestId('tab-work').click();
    await page.getByTestId('analyze').click();
    await expect(page.getByTestId('run-state')).toBeVisible();
    const progress = await watchProgress(page, async () => (await page.getByTestId('preview').count()) + (await page.getByTestId('run-error').count()) > 0, 420_000);
    console.log('[T092 real] progressi analisi:', JSON.stringify(progress));
    if ((await page.getByTestId('run-error').count()) > 0) await diagnose('analisi oltre contesto');
    await expect(page.getByTestId('run-error'), 'l analisi oltre contesto non fallisce').toHaveCount(0);
    await expect(page.getByTestId('preview')).toBeVisible();
    await expectTruthfulExecutionPanel(page, progress, projectId);

    const run = (await stack.sql<{ Status: string; Regime: string; InitialRegime: string; WorkflowDefinitionVersion: string }>(`SELECT "Status"::text, "Regime"::text, "InitialRegime"::text, "WorkflowDefinitionVersion" FROM "ProcessingRuns"`))[0];
    expect(run.Status).toBe('Completed');
    expect(run.Regime).toBe(run.InitialRegime);
    expect(run.Regime).toBe('Extended');
    const reconcileCalls = (await stack.llm()).requests.filter((r) => r.graph === 'doc-reconcile').length;
    expect(reconcileCalls, 'riconciliazione gerarchica: piu batch').toBeGreaterThan(2);
    expect((await stack.llm()).requests.filter((r) => r.graph === 'doc-analysis')).toHaveLength(0);   // oltre contesto: nessuna chiamata single-call

    // --- Risultato completo: una voce per evidenza, ciascuna con le fonti apribili sul passaggio originale.
    const items = page.locator('[data-testid=item]');
    await expect(items).toHaveCount(documentEvidences);
    await items.first().getByTestId('item-sources').click();
    await expect(items.first().getByTestId('passage-open').first()).toBeVisible();

    // --- Copertura: 100% per ID senza certificazione del significato.
    await page.getByTestId('tab-evidence').click();
    await expect(page.getByTestId('verify-coverage')).toBeEnabled();
    await page.getByTestId('verify-coverage').click();
    await expect(page.getByTestId('report')).toBeVisible({ timeout: 60_000 });
    await expect(page.getByTestId('id-coverage')).toContainText(`${documentEvidences} di ${documentEvidences} evidenze attese collegate a un elemento`);
    await expect(page.getByTestId('id-coverage-note')).toContainText('nessuna copertura, anche al 100%, certifica il significato');
    expect(await page.getByTestId('report').innerText()).not.toMatch(/\b(fedelt[àa] (verificata|certificata)|risultato completo|verifica completata|copertura completa)\b/i);

    // --- Conferma e Genera: bozza per unita editoriali, fonti apribili.
    await page.getByTestId('tab-work').click();
    await page.getByTestId('confirm').click();
    await expect(page.getByTestId('confirmed-badge')).toBeVisible({ timeout: 30_000 });
    await page.getByTestId('generate').click();
    await expect(page.getByTestId('generation-state')).toBeVisible();
    await expect.poll(async () => (await page.getByTestId('draft').count()) + (await page.getByTestId('generation-error').count()), { timeout: 420_000 }).toBeGreaterThan(0);
    if ((await page.getByTestId('generation-error').count()) > 0) await diagnose('bozza oltre contesto');
    await expect(page.getByTestId('generation-error')).toHaveCount(0);
    await expect(page.getByTestId('draft')).toBeVisible();
    const draftCalls = (await stack.llm()).requests.filter((r) => r.graph === 'doc-draft');
    console.log('[T092 real] chiamate bozza:', draftCalls.length);
    const chips = page.getByTestId('markdown').getByTestId('source-chip');
    expect(await chips.count(), 'la bozza cita le fonti').toBeGreaterThan(0);
    await expect(page.getByTestId('source-chip-unknown')).toHaveCount(0);
    await chips.first().click();
    await expect(page.getByTestId('focused-source').first()).toBeVisible();
    expect((await stack.llm()).unexpected).toEqual([]);
    expect(writes.filter((w) => /\/analyses$/.test(w))).toHaveLength(1);
    expect(writes.filter((w) => /document-generations$/.test(w))).toHaveLength(1);
    await page.screenshot({ path: `${screens}/T092-real-bozza.png`, fullPage: true });
    void projectId;
  });

  test('capacita ridotta del runtime: il run fallisce con errore esplicito del server (nessuna anteprima, conferma, Genera o bozza); ripristinata la capacita, una nuova richiesta riesce e il fallito resta nello storico', async ({ page }) => {
    test.setTimeout(600_000);
    const projectId = await seedProject(page, 'limite');
    const documentEvidences = (await stack.sql<{ n: number }>(`SELECT count(*)::int n FROM "Evidences" WHERE "OriginKind"::text = 'Document'`))[0].n;
    // Capacita del runtime ridotta DOPO la preparazione: il budget di input (700 - 200 - 20) non basta nemmeno per un batch minimo di riconciliazione.
    await stack.restartBackend({ Ai__ContextWindowTokens: '700', Ai__MaxOutputTokens: '200', Ai__SafetyMarginTokens: '20' });
    await page.reload();
    await expect(page.getByTestId('analyze')).toBeVisible({ timeout: 30_000 });
    await page.getByTestId('analyze').click();
    await expect(page.getByTestId('run-error')).toBeVisible({ timeout: 120_000 });
    const message = (await page.getByTestId('run-error').innerText()).replace(/\s+/g, ' ');
    console.log('[T092 real] errore del run:', message);
    for (const id of ['preview', 'confirm', 'generate', 'draft']) await expect(page.getByTestId(id)).toHaveCount(0);
    const failed = await stack.sql<{ Status: string; ErrorCode: string | null }>(`SELECT "Status"::text, "ErrorCode" FROM "ProcessingRuns" ORDER BY "RequestedAt"`);
    console.log('[T092 real] run falliti:', JSON.stringify(failed));
    expect(failed).toHaveLength(1);
    expect(failed[0].Status).toBe('Failed');
    expect(failed[0].ErrorCode, 'codice di errore esplicito del server').toBeTruthy();
    expect(await stack.sql(`SELECT 1 FROM "AnalysisRevisions"`), 'nessuna revisione da un run fallito').toHaveLength(0);
    expect(await stack.sql(`SELECT 1 FROM "Drafts"`)).toHaveLength(0);
    expect((await stack.llm()).unexpected).toEqual([]);

    // --- Ripristino della capacita: nuova richiesta (nuova chiave), completata; il run fallito non viene riscritto.
    await stack.restartBackend({ Ai__ContextWindowTokens: '4000', Ai__MaxOutputTokens: '1500', Ai__SafetyMarginTokens: '20' });
    await page.reload();
    await expect(page.getByTestId('analyze')).toBeVisible({ timeout: 30_000 });
    await page.getByTestId('analyze').click();
    await expect(page.getByTestId('preview')).toBeVisible({ timeout: 300_000 });
    await expect(page.locator('[data-testid=item]')).toHaveCount(documentEvidences);
    const runs = await stack.sql<{ Status: string; ErrorCode: string | null }>(`SELECT "Status"::text, "ErrorCode" FROM "ProcessingRuns" ORDER BY "RequestedAt"`);
    expect(runs.map((r) => r.Status)).toEqual(['Failed', 'Completed']);
    expect(runs[0].ErrorCode).toBe(failed[0].ErrorCode);
    void projectId;
  });

  test('disconnessione durante il run multipasso: il risultato si riconcilia dai GET, un solo comando Analizza, nessun lavoro duplicato', async ({ page }) => {
    test.setTimeout(600_000);
    const projectId = await seedProject(page, 'disconnessione');
    await stack.fault({ graph: 'doc-reconcile', times: 80, kind: 'delay', delayMs: 700 });
    const writes: string[] = [];
    page.on('request', (r) => {
      if (r.method() !== 'GET' && r.url().includes('/api/') && !r.url().includes('/api/auth/')) writes.push(`${r.method()} ${new URL(r.url()).pathname}`);
    });
    await page.getByTestId('tab-work').click();
    await page.getByTestId('analyze').click();
    await expect(page.getByTestId('run-state')).toBeVisible();
    await expect.poll(async () => (await stack.llm()).requests.filter((r) => r.graph === 'doc-reconcile').length, { timeout: 60_000 }).toBeGreaterThan(2);
    // Il browser perde la rete a meta run: l'evento di completamento puo andare perso; alla riconnessione l'interfaccia rilegge lo stato dal server.
    await page.context().setOffline(true);
    await expect.poll(async () => (await stack.sql<{ Status: string }>(`SELECT "Status"::text "Status" FROM "ProcessingRuns"`))[0].Status, { timeout: 120_000 }).toBe('Completed');
    await page.waitForTimeout(1_500);
    await page.context().setOffline(false);
    await expect(page.getByTestId('preview')).toBeVisible({ timeout: 90_000 });
    await expect(page.getByTestId('run-state')).toHaveCount(0);
    await expect(page.getByTestId('run-error')).toHaveCount(0);
    expect(writes.filter((w) => /\/analyses$/.test(w)), 'un solo comando Analizza, nessuna ripetizione alla riconnessione').toHaveLength(1);
    expect(writes.filter((w) => !/\/analyses$/.test(w)), 'nessun altro comando').toEqual([]);
    expect((await stack.sql<{ n: number }>(`SELECT count(*)::int n FROM "ProcessingRuns"`))[0].n).toBe(1);
    expect((await stack.llm()).unexpected).toEqual([]);
    void projectId;
  });
});
