import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { api, realStack, stack } from './support/real-stack/client';
import { FakeLifecycle } from './support/fake-lifecycle';
import { FlowStack } from './support/fake-document-flow';
import { FakeParsing } from './support/fake-parsing';
import { metricsOf, usageOf } from './support/context-usage';
import {
  createProject, diagnose, docItem, fixtureBytes, login, openDocumentsTab, openProject, showVersions, unique, uploadDocument, uploadVersion, waitReady, workCounts,
} from './support/real-stack/ui';

// T103 (E2E-OFFLINE) - versioni, rielaborazione, promozione, sessioni concorrenti, chiusura/riapertura del browser, riavvio del backend ed errori/retry,
// su DUE livelli distinti e mai confusi (si discoverano separatamente: la variabile E2E_STACK decide quale dei due describe esiste):
//   [E2E fake]  fake-stack nel browser (nessun backend/PostgreSQL/parser): contratto HTTP letto dall'interfaccia, `npx playwright test document-version-recovery`.
//   [E2E reale + LLM finto]  backend REALE + PostgreSQL REALE (database isolato) + parser REALE; finte solo le risposte del modello (support/fake-llm.ts):
//                            `npx playwright test -c playwright.real.config.ts document-version-recovery`. Nessun DGX in nessuno dei due.
// Risultati in specs/002-project-documents/validation/I7-deterministic.md.

const screens = 'test-results/screens';
const hashOf = (b: Buffer) => createHash('sha256').update(b).digest('hex');
const md1 = fixtureBytes('md-basic.md');
const md2 = Buffer.concat([md1, Buffer.from('\n## Aggiornamento\n\n- La consultazione degli ordini resta disponibile per ventiquattro mesi.\n', 'utf8')]);

if (realStack) {
  test.describe.configure({ mode: 'serial' });

  test.describe('[E2E reale + LLM finto] versioni, rielaborazione, promozione e recupero', () => {
    // Ogni prova parte da un database senza progetti e da un modello finto azzerato: le asserzioni globali sulle tabelle sono cosi riferite alla sola prova.
    test.beforeEach(async () => {
      await stack.resetData();
      await stack.resetLlm();
    });

    test('v1/v2, fissa, escludi, rielabora e promuovi: stato persistito in PostgreSQL, originali e hash intatti, nessun lavoro da sola lettura', async ({ page }) => {
      const name = `T103 versioni ${unique()}`;
      await login(page);
      const projectId = await createProject(page, name);
      await openDocumentsTab(page);

      // --- v1 e v2: parser reale, estrazione col modello finto, entrambe Pronto con originale e hash.
      await uploadDocument(page, 'md-basic.md', md1);
      await waitReady(page, 'md-basic.md');
      await uploadVersion(page, 'md-basic.md', md2);
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-version')).toHaveCount(2, { timeout: 30_000 });
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-version-status')).toHaveText(['Pronto', 'Pronto'], { timeout: 90_000 });
      const versions = await stack.sql<{ Version: number; Sha256: string; Id: string; DocumentId: string }>(`SELECT "Id", "DocumentId", "Version", "Sha256" FROM "DocumentVersions" ORDER BY "Version"`);
      expect(versions.map((v) => v.Sha256)).toEqual([hashOf(md1), hashOf(md2)]);
      const [v1, v2] = versions;
      const states = await stack.sql<{ DocumentVersionId: string; Revision: number; CurrentPreparationId: string | null }>(`SELECT "DocumentVersionId", "Revision", "CurrentPreparationId" FROM "EvidenceDocumentStates"`);
      expect(states).toHaveLength(2);
      expect(states.every((s) => s.CurrentPreparationId !== null)).toBe(true);
      console.log('[T103 real] stati iniziali', JSON.stringify(states));
      await page.screenshot({ path: `${screens}/T103-real-versioni.png`, fullPage: true });

      const row = (n: number) => docItem(page, 'md-basic.md').getByTestId('docs-version').filter({ hasText: `v${n} ·` });
      const selection = () => stack.sql<{ DocumentId: string; Included: boolean; Mode: string | number; PinnedVersionId: string | null }>(`SELECT "DocumentId", "Included", "Mode", "PinnedVersionId" FROM "ProjectSourceSelectionItems"`);

      // --- Fissa v1: persistito (Mode/PinnedVersionId), badge "Fissata"; poi "ultima versione pronta" lo toglie.
      await row(1).getByTestId('docs-pin').click();
      await expect(row(1).getByTestId('docs-pinned')).toBeVisible();
      await expect.poll(async () => (await selection()).map((i) => i.PinnedVersionId)).toEqual([v1.Id]);
      console.log('[T103 real] selezione dopo pin', JSON.stringify(await selection()));
      await docItem(page, 'md-basic.md').getByTestId('docs-latest').click();
      await expect(row(1).getByTestId('docs-pinned')).toHaveCount(0);
      await expect.poll(async () => (await selection()).map((i) => i.PinnedVersionId)).toEqual([null]);

      // --- Escludi: persiste dopo un ricaricamento della pagina; poi si reincludere.
      const include = docItem(page, 'md-basic.md').getByTestId('docs-include');
      await include.uncheck();
      await expect.poll(async () => (await selection()).map((i) => i.Included)).toEqual([false]);
      await page.reload();
      await openDocumentsTab(page);
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-include')).not.toBeChecked();
      await docItem(page, 'md-basic.md').getByTestId('docs-include').check();
      await expect.poll(async () => (await selection()).map((i) => i.Included)).toEqual([true]);

      // --- Rielabora v1: nuova preparazione CANDIDATA della stessa versione; il bundle corrente non cambia finche non la promuovi.
      await showVersions(page, 'md-basic.md');
      const before = await stack.sql<{ Revision: number; CurrentPreparationId: string }>(`SELECT "Revision", "CurrentPreparationId" FROM "EvidenceDocumentStates" WHERE "DocumentVersionId" = '${v1.Id}'`);
      await row(1).getByTestId('docs-reprocess').click();
      // Finche la generazione 2 e in lavorazione il bundle corrente della v1 resta quello in uso; la candidata compare (pronta, non corrente) solo a fine preparazione.
      await expect(row(1).getByTestId('docs-candidate')).toContainText('generazione 2', { timeout: 90_000 }).catch(async (e) => {
        console.log('[T103 real] preparazioni', JSON.stringify(await stack.sql(`SELECT "Generation", "Status", "ErrorCode" FROM "DocumentPreparations" WHERE "DocumentVersionId" = '${v1.Id}' ORDER BY 1`)));
        console.log('[T103 real] vista versione', JSON.stringify((await api('GET', `/api/projects/${projectId}/documents/${v1.DocumentId}`)).body).slice(0, 3000));
        throw e;
      });
      await expect(row(1).getByTestId('docs-promote')).toBeVisible({ timeout: 90_000 });
      await expect(row(1).getByTestId('docs-candidate-note')).toContainText('non è usata come fonte');
      const mid = await stack.sql<{ Revision: number; CurrentPreparationId: string }>(`SELECT "Revision", "CurrentPreparationId" FROM "EvidenceDocumentStates" WHERE "DocumentVersionId" = '${v1.Id}'`);
      expect(mid, 'la candidata non cambia il bundle corrente').toEqual(before);
      await row(1).getByTestId('docs-promote').click();
      await expect(row(1).getByTestId('docs-candidate')).toHaveCount(0, { timeout: 30_000 });
      const after = await stack.sql<{ Revision: number; CurrentPreparationId: string }>(`SELECT "Revision", "CurrentPreparationId" FROM "EvidenceDocumentStates" WHERE "DocumentVersionId" = '${v1.Id}'`);
      expect(after[0].Revision).toBe(before[0].Revision + 1);
      expect(after[0].CurrentPreparationId).not.toBe(before[0].CurrentPreparationId);
      const promotions = await stack.sql<{ FromPreparationId: string; ToPreparationId: string }>(`SELECT "FromPreparationId", "ToPreparationId" FROM "DocumentPromotions" WHERE "DocumentVersionId" = '${v1.Id}'`);
      expect(promotions.some((p) => p.FromPreparationId === before[0].CurrentPreparationId && p.ToPreparationId === after[0].CurrentPreparationId)).toBe(true);
      console.log('[T103 real] promozioni', JSON.stringify(promotions));

      // --- Originali e hash intatti dopo tutto il ciclo.
      const hashes = await stack.sql<{ Version: number; Sha256: string }>(`SELECT "Version", "Sha256" FROM "DocumentVersions" ORDER BY "Version"`);
      expect(hashes.map((h) => h.Sha256)).toEqual([hashOf(md1), hashOf(md2)]);
      for (const [n, bytes] of [[1, md1], [2, md2]] as const) {
        const [download] = await Promise.all([page.waitForEvent('download'), row(n).getByTestId('docs-download').click()]);
        expect(hashOf(await (await import('node:fs/promises')).readFile((await download.path())!))).toBe(hashOf(bytes));
      }

      // --- Lettura/riconnessione non creano lavoro: ricarica, offline/online (riconnessione SSE), rilettura del contenuto.
      const counts = await workCounts();
      const llmBefore = (await stack.llm()).requests.length;
      await page.reload();
      await openDocumentsTab(page);
      await showVersions(page, 'md-basic.md');
      await page.context().setOffline(true);
      await page.waitForTimeout(1_500);
      await page.context().setOffline(false);
      await page.waitForTimeout(3_000);
      await row(1).getByTestId('docs-read').click();
      await page.waitForTimeout(1_500);
      expect(await workCounts(), 'nessun job creato da lettura o riconnessione').toEqual(counts);
      expect((await stack.llm()).requests.length, 'nessuna richiesta al modello da lettura o riconnessione').toBe(llmBefore);
      expect((await stack.llm()).unexpected).toEqual([]);
    });

    /** Impronta di tutto cio che dopo una conferma/bozza NON deve mai cambiare per effetto di versioni, rielaborazioni o promozioni. */
    const frozen = () =>
      stack.sql<{ k: string; id: string; h: string }>(`
        SELECT 'revisione' k, "Id"::text id, md5("ContentJson"::text || "ValidationState"::text || coalesce("ConfirmedAt"::text, '') || "SchemaVersion") h FROM "AnalysisRevisions"
        UNION ALL SELECT 'bozza', "Id"::text, md5("Markdown" || "AnalysisRevisionId"::text) FROM "Drafts"
        UNION ALL SELECT 'snapshot', "Id"::text, md5("Hash" || "SelectionRevision"::text) FROM "SourceSnapshots"
        UNION ALL SELECT 'snapshot-voce', "Id"::text, md5(coalesce("DocumentVersionId"::text, '') || coalesce("PreparationId"::text, '') || coalesce("EvidenceVersionId"::text, '')) FROM "SourceSnapshotItems"
        UNION ALL SELECT 'run', "Id"::text, md5("Status"::text || coalesce("SourceSnapshotId"::text, '') || coalesce("ResultRevisionId"::text, '') || coalesce("DraftId"::text, '')) FROM "ProcessingRuns"
        UNION ALL SELECT 'messaggio', "Id"::text, md5("Body") FROM "ConversationMessages"
        UNION ALL SELECT 'originale', "Id"::text, "Sha256" FROM "DocumentVersions"
        ORDER BY 1, 2`);

    test('conferma, bozza, snapshot, citazioni e originali restano IDENTICI dopo nuova versione, rielaborazione e promozione; nessun lavoro AI da queste azioni', async ({ page }) => {
      const name = `T103 identita ${unique()}`;
      await login(page);
      const projectId = await createProject(page, name);
      await openDocumentsTab(page);
      await uploadDocument(page, 'md-basic.md', md1);
      await waitReady(page, 'md-basic.md');
      await uploadDocument(page, 'txt-negations.txt');
      await waitReady(page, 'txt-negations.txt');

      // --- chat con fonti, Analizza (comando esplicito), conferma e Genera: tutto sul modello finto.
      await page.getByTestId('tab-work').click();
      const box = page.getByLabel("Messaggio per l'assistente");
      await box.fill('Che cosa prevede la consultazione degli ordini?');
      await box.press('Control+Enter');
      await expect(page.locator('article[data-role="assistant"]')).toHaveCount(1, { timeout: 90_000 });
      await expect(page.locator('article[data-role="assistant"]').getByTestId('chat-source').first()).toBeVisible();
      await page.getByTestId('analyze').click();
      await expect(page.getByTestId('run-state')).toBeVisible();
      await expect.poll(async () => (await page.getByTestId('preview').count()) + (await page.getByTestId('run-error').count()), { timeout: 120_000 }).toBeGreaterThan(0);
      if ((await page.getByTestId('run-error').count()) > 0) await diagnose('analisi');
      await expect(page.getByTestId('run-error'), 'analisi senza errori').toHaveCount(0);
      await page.getByTestId('confirm').click();
      await expect(page.getByTestId('confirmed-badge')).toBeVisible({ timeout: 30_000 });
      await page.getByTestId('generate').click();
      await expect.poll(async () => (await page.getByTestId('draft').count()) + (await page.getByTestId('generation-error').count()), { timeout: 120_000 }).toBeGreaterThan(0);
      if ((await page.getByTestId('generation-error').count()) > 0) await diagnose('generazione');
      await expect(page.getByTestId('generation-error'), 'generazione senza errori').toHaveCount(0);
      const markdownBefore = await page.getByTestId('markdown').innerText();
      expect(await page.getByTestId('markdown').getByTestId('source-chip').count()).toBeGreaterThan(0);

      // --- stato di riferimento: impronte, citazioni risolte dall'API (testo esatto dei passaggi) e conteggi del lavoro.
      const revision = (await stack.sql<{ ContentJson: unknown }>(`SELECT "ContentJson" FROM "AnalysisRevisions" ORDER BY "Version" DESC LIMIT 1`))[0];
      const sources = ((typeof revision.ContentJson === 'string' ? JSON.parse(revision.ContentJson) : revision.ContentJson) as { sources: { kind: string; documentId: string; preparationId: string; blockId: string; start: number; end: number; quote: string }[] }).sources.filter((x) => x.kind === 'DocumentPassage');
      expect(sources.length).toBeGreaterThan(0);
      const passages = async () => Promise.all(sources.map(async (x) => {
        const response = await api<{ quote: { text: string } }>('GET', `/api/projects/${projectId}/documents/${x.documentId}/versions/1/preparations/${x.preparationId}/passages/${x.blockId}?start=${x.start}&end=${x.end}`);
        return { status: response.status, text: response.body?.quote?.text };
      }));
      const passagesBefore = await passages();
      expect(passagesBefore.every((p) => p.status === 200 && p.text !== undefined)).toBe(true);
      expect(passagesBefore.map((p) => p.text)).toEqual(sources.map((x) => x.quote));
      const before = await frozen();
      expect(before.filter((r) => r.k === 'bozza')).toHaveLength(1);
      const countsBefore = await workCounts();
      const aiBefore = (await stack.llm()).requests.filter((r) => r.graph !== 'extract');

      // --- nuova versione v2 (md), rielaborazione della v1 e promozione: solo estrazione documentale, nessun'altra chiamata AI.
      await openDocumentsTab(page);
      await uploadVersion(page, 'md-basic.md', md2);
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-version-status')).toHaveText(['Pronto', 'Pronto'], { timeout: 90_000 });
      const row = (n: number) => docItem(page, 'md-basic.md').getByTestId('docs-version').filter({ hasText: `v${n} ·` });
      await row(1).getByTestId('docs-reprocess').click();
      await expect(row(1).getByTestId('docs-promote')).toBeVisible({ timeout: 120_000 });
      await row(1).getByTestId('docs-promote').click();
      await expect(row(1).getByTestId('docs-candidate')).toHaveCount(0, { timeout: 30_000 });

      // --- tutto cio che era stato accettato e' identico, byte per byte; le citazioni si aprono con lo stesso testo.
      const frozenAfter = await frozen();
      expect(frozenAfter.filter((r) => r.k !== 'originale'), 'revisione/bozza/snapshot/run/messaggi identici').toEqual(before.filter((r) => r.k !== 'originale'));
      expect(frozenAfter.filter((r) => r.k === 'originale').filter((r) => before.some((b) => b.id === r.id)), 'gli originali gia caricati hanno lo stesso hash').toEqual(before.filter((r) => r.k === 'originale'));
      expect(frozenAfter.filter((r) => r.k === 'originale'), 'solo la v2 si aggiunge').toHaveLength(before.filter((r) => r.k === 'originale').length + 1);
      expect(await passages(), 'citazioni identiche (passaggi della preparazione storica)').toEqual(passagesBefore);
      const after = await workCounts();
      expect(after.runs, 'nessun nuovo run di analisi/generazione').toBe(countsBefore.runs);
      expect(after.turns, 'nessun nuovo turno di chat').toBe(countsBefore.turns);
      expect((await stack.llm()).requests.filter((r) => r.graph !== 'extract'), 'nessuna chiamata AI non documentale dopo la bozza').toEqual(aiBefore);
      expect((await stack.llm()).unexpected).toEqual([]);

      // --- dopo un ricaricamento la bozza e le sue fonti sono ancora consultabili con le stesse parti originali.
      await openProject(page, projectId, name);
      await expect(page.getByTestId('draft')).toBeVisible({ timeout: 30_000 });
      expect(await page.getByTestId('markdown').innerText()).toBe(markdownBefore);
      await page.getByTestId('markdown').getByTestId('source-chip').first().click();
      await expect(page.getByTestId('focused-source').first()).toBeVisible();
      await expect(page.getByTestId('source-chip-unknown')).toHaveCount(0);
      await page.screenshot({ path: `${screens}/T103-real-identita.png`, fullPage: true });
    });

    const newSession = async (browser: Browser, storageState?: Awaited<ReturnType<BrowserContext['storageState']>>) =>
      browser.newContext({ baseURL: process.env['E2E_BASE_URL'], viewport: { width: 1440, height: 900 }, storageState });
    const versionRow = (page: Page, n: number) => docItem(page, 'md-basic.md').getByTestId('docs-version').filter({ hasText: `v${n} ·` });
    const refreshList = async (page: Page) => {
      await page.getByTestId('docs-refresh').click();
      await page.waitForTimeout(500);
    };

    test('due sessioni PM concorrenti: rielaborazione e promozione simultanee producono UNA sola candidata e UNA sola promozione, e le due viste convergono', async ({ browser }) => {
      const name = `T103 concorrenza ${unique()}`;
      const a = await newSession(browser);
      const b = await newSession(browser);
      try {
        const pa = await a.newPage();
        const pb = await b.newPage();
        await login(pa);
        const projectId = await createProject(pa, name);
        await openDocumentsTab(pa);
        await uploadDocument(pa, 'md-basic.md', md1);
        await waitReady(pa, 'md-basic.md');
        await login(pb);
        await openProject(pb, projectId, name);
        await openDocumentsTab(pb);
        await showVersions(pb, 'md-basic.md');
        await expect(versionRow(pb, 1).getByTestId('docs-version-status')).toHaveText('Pronto');

        // A carica la v2: B la vede senza alcuna azione propria oltre alla lettura (Aggiorna); nessun job creato da B.
        await uploadVersion(pa, 'md-basic.md', md2);
        await expect(versionRow(pa, 2).getByTestId('docs-version-status')).toHaveText('Pronto', { timeout: 90_000 });
        const jobsBeforeB = await workCounts();
        await refreshList(pb);
        await showVersions(pb, 'md-basic.md');
        await expect(versionRow(pb, 2).getByTestId('docs-version-status')).toHaveText('Pronto', { timeout: 30_000 });
        expect(await workCounts(), 'la lettura di B non crea lavoro').toEqual(jobsBeforeB);

        // Rielaborazione SIMULTANEA della v1 da entrambe le sessioni (chiavi di idempotenza diverse): una sola nuova generazione.
        await Promise.all([versionRow(pa, 1).getByTestId('docs-reprocess').click(), versionRow(pb, 1).getByTestId('docs-reprocess').click()]);
        await expect.poll(async () => (await stack.sql<{ n: number }>(`SELECT count(*)::int n FROM "DocumentPreparations" p JOIN "DocumentVersions" v ON v."Id" = p."DocumentVersionId" WHERE v."Version" = 1`))[0].n, { timeout: 30_000 }).toBeGreaterThanOrEqual(2);
        await expect(versionRow(pa, 1).getByTestId('docs-promote')).toBeVisible({ timeout: 120_000 });
        await refreshList(pb);
        await showVersions(pb, 'md-basic.md');
        await expect(versionRow(pb, 1).getByTestId('docs-promote')).toBeVisible({ timeout: 30_000 });
        const generations = await stack.sql<{ Generation: number }>(`SELECT p."Generation" FROM "DocumentPreparations" p JOIN "DocumentVersions" v ON v."Id" = p."DocumentVersionId" WHERE v."Version" = 1 ORDER BY 1`);
        expect(generations.map((g) => g.Generation), 'una sola candidata: generazioni 1 e 2').toEqual([1, 2]);

        // Promozione SIMULTANEA: CAS sulla bundleRevision, un solo vincitore (l'altra sessione riceve conflitto o replay, mai una seconda promozione).
        const promotionsBefore = (await stack.sql<{ n: number }>(`SELECT count(*)::int n FROM "DocumentPromotions"`))[0].n;
        const revisionBefore = (await stack.sql<{ Revision: number }>(`SELECT s."Revision" FROM "EvidenceDocumentStates" s JOIN "DocumentVersions" v ON v."Id" = s."DocumentVersionId" WHERE v."Version" = 1`))[0].Revision;
        await Promise.all([versionRow(pa, 1).getByTestId('docs-promote').click(), versionRow(pb, 1).getByTestId('docs-promote').click()]);
        await expect.poll(async () => (await stack.sql<{ n: number }>(`SELECT count(*)::int n FROM "DocumentPromotions"`))[0].n, { timeout: 30_000 }).toBe(promotionsBefore + 1);
        await pa.waitForTimeout(1_500);
        expect((await stack.sql<{ n: number }>(`SELECT count(*)::int n FROM "DocumentPromotions"`))[0].n, 'esattamente una promozione').toBe(promotionsBefore + 1);
        const revisionAfter = (await stack.sql<{ Revision: number }>(`SELECT s."Revision" FROM "EvidenceDocumentStates" s JOIN "DocumentVersions" v ON v."Id" = s."DocumentVersionId" WHERE v."Version" = 1`))[0].Revision;
        expect(revisionAfter).toBe(revisionBefore + 1);
        const outcomes = await Promise.all([pa, pb].map(async (p) => ({ conflict: await versionRow(p, 1).getByTestId('docs-promote-error').count(), candidate: await versionRow(p, 1).getByTestId('docs-candidate').count() })));
        console.log('[T103 real] esiti promozione simultanea (conflitti/candidate residue per sessione)', JSON.stringify(outcomes));

        // Le due viste convergono dopo la lettura: nessuna candidata, stesso stato per le due versioni.
        for (const p of [pa, pb]) {
          await refreshList(p);
          await showVersions(p, 'md-basic.md');
          await expect(versionRow(p, 1).getByTestId('docs-candidate')).toHaveCount(0, { timeout: 30_000 });
        }
        const view = (p: Page) => docItem(p, 'md-basic.md').getByTestId('docs-version-status').allInnerTexts();
        expect(await view(pa)).toEqual(await view(pb));
        expect((await stack.llm()).unexpected).toEqual([]);
      } finally {
        await a.close();
        await b.close();
      }
    });

    test('chiusura e riapertura del browser su 3 sessioni con riavvio del backend (anche a meta rielaborazione): stato persistito, ripresa senza duplicati, nessun job da sola lettura', async ({ browser }) => {
      test.setTimeout(420_000);
      const name = `T103 riavvii ${unique()}`;

      // ---- Sessione 1: login, progetto, v1 Pronto, pin della v1; il browser viene chiuso conservando solo i cookie (come un browser riaperto).
      let ctx = await newSession(browser);
      let page = await ctx.newPage();
      await login(page);
      const projectId = await createProject(page, name);
      await openDocumentsTab(page);
      await uploadDocument(page, 'md-basic.md', md1);
      await waitReady(page, 'md-basic.md');
      await showVersions(page, 'md-basic.md');
      await versionRow(page, 1).getByTestId('docs-pin').click();
      await expect(versionRow(page, 1).getByTestId('docs-pinned')).toBeVisible();
      let cookies = await ctx.storageState();
      await ctx.close();
      const afterS1 = await workCounts();

      // ---- Riavvio 1. Sessione 2: riapertura SENZA nuovo login (refresh dal cookie persistito), lettura pura: nessun job, nessuna chiamata al modello.
      const llmMark = (await stack.llm()).requests.length;
      await stack.restartBackend();
      ctx = await newSession(browser, cookies);
      page = await ctx.newPage();
      await openProject(page, projectId, name);
      await openDocumentsTab(page);
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-status')).toHaveText('Pronto', { timeout: 30_000 });
      await showVersions(page, 'md-basic.md');
      await expect(versionRow(page, 1).getByTestId('docs-pinned'), 'il pin sopravvive al riavvio').toBeVisible();
      await page.reload();
      await openDocumentsTab(page);
      await page.waitForTimeout(2_000);
      expect(await workCounts(), 'riapertura + riavvio + rilettura: nessun job').toEqual(afterS1);
      expect((await stack.llm()).requests.length, 'nessuna chiamata al modello da sola lettura').toBe(llmMark);

      // ---- Sessione 2 continua: v2 (esclusa dalla risoluzione perche la v1 e fissata), poi rielaborazione della v1 con la prima chiamata di estrazione LENTA,
      //      e il backend viene riavviato MENTRE la rielaborazione e in corso.
      await uploadVersion(page, 'md-basic.md', md2);
      await expect(versionRow(page, 2).getByTestId('docs-version-status')).toHaveText('Pronto', { timeout: 90_000 });
      await stack.fault({ graph: 'extract', times: 1, kind: 'delay', delayMs: 10_000 });
      await versionRow(page, 1).getByTestId('docs-reprocess').click();
      await expect.poll(async () => (await stack.llm()).requests.filter((r) => r.outcome === 'delay').length, { timeout: 60_000 }).toBe(1);
      await stack.restartBackend();
      cookies = await ctx.storageState();
      await ctx.close();

      // ---- Sessione 3: browser riaperto dopo il secondo riavvio. L'estrazione interrotta riprende da sola (lease scaduto): una sola candidata, nessun duplicato.
      ctx = await newSession(browser, cookies);
      page = await ctx.newPage();
      await openProject(page, projectId, name);
      await openDocumentsTab(page);
      await showVersions(page, 'md-basic.md');
      await expect(versionRow(page, 1).getByTestId('docs-promote')).toBeVisible({ timeout: 180_000 });
      const preparations = await stack.sql<{ Generation: number; Status: string }>(`SELECT p."Generation", p."Status"::text "Status" FROM "DocumentPreparations" p JOIN "DocumentVersions" v ON v."Id" = p."DocumentVersionId" WHERE v."Version" = 1 ORDER BY 1`);
      expect(preparations, 'una sola candidata dopo il riavvio a meta lavoro').toEqual([{ Generation: 1, Status: 'Ready' }, { Generation: 2, Status: 'Ready' }]);
      const extractionJobs = await stack.sql<{ Status: string }>(`SELECT j."Status"::text "Status" FROM "EvidenceExtractionJobs" j JOIN "DocumentPreparations" p ON p."Id" = j."PreparationId" WHERE p."Generation" = 2`);
      expect(extractionJobs.map((j) => j.Status), 'un solo job di estrazione per la candidata, concluso').toEqual(['Completed']);
      await versionRow(page, 1).getByTestId('docs-promote').click();
      await expect(versionRow(page, 1).getByTestId('docs-candidate')).toHaveCount(0, { timeout: 30_000 });

      // ---- Persistenza finale: pin, versioni, promozioni e hash degli originali; riavvio 3 e lettura pura senza lavoro.
      const settled = await workCounts();
      await stack.restartBackend();
      await page.reload();
      await openDocumentsTab(page);
      await showVersions(page, 'md-basic.md');
      await expect(versionRow(page, 1).getByTestId('docs-pinned')).toBeVisible({ timeout: 30_000 });
      await page.waitForTimeout(2_000);
      expect(await workCounts(), 'dopo il terzo riavvio e la rilettura nessun lavoro nuovo').toEqual(settled);
      const selection = await stack.sql<{ Mode: string; PinnedVersionId: string | null; Included: boolean }>(`SELECT "Mode"::text "Mode", "PinnedVersionId", "Included" FROM "ProjectSourceSelectionItems"`);
      expect(selection).toHaveLength(1);
      expect(selection[0].Mode).toBe('Pinned');
      expect(selection[0].Included).toBe(true);
      const hashes = await stack.sql<{ Sha256: string }>(`SELECT "Sha256" FROM "DocumentVersions" ORDER BY "Version"`);
      expect(hashes.map((h) => h.Sha256)).toEqual([hashOf(md1), hashOf(md2)]);
      expect((await stack.sql<{ n: number }>(`SELECT count(*)::int n FROM "DocumentPromotions"`))[0].n).toBe(3);   // v1 e v2 iniziali + la promozione della candidata
      expect((await stack.llm()).unexpected).toEqual([]);
      await ctx.close();
    });

    test('errori, retry, readiness e ultimo successo: parser fallito con retry unico, guasti del modello senza falsi Pronto, runtime non pronto dichiarato, ultimo successo conservato', async ({ page }) => {
      test.setTimeout(420_000);
      const name = `T103 errori ${unique()}`;
      await login(page);
      const projectId = await createProject(page, name);
      await openDocumentsTab(page);
      const usage = async () => (await api<Record<string, unknown>>('GET', `/api/projects/${projectId}/context-usage`)).body;
      /** L'ultimo successo misurato (refId, istante e metriche); `staleReason` cambia legittimamente con i messaggi piu recenti e non ne fa parte. */
      const lastOf = async () => {
        const last = ((await usage()) as { last?: { kind: string; refId: string; at: string; metrics: unknown } | null }).last;
        return last ? JSON.stringify({ kind: last.kind, refId: last.refId, at: last.at, metrics: last.metrics }) : null;
      };

      // ---- (a) Parser reale: PDF corrotto => Failed con motivo; mai un falso Pronto; un solo retry per clic; la lettura non ritenta.
      await uploadDocument(page, 'pdf-corrupt.pdf');
      await expect(docItem(page, 'pdf-corrupt.pdf').getByTestId('docs-status')).not.toHaveText('In coda', { timeout: 90_000 });
      await expect.poll(async () => (await docItem(page, 'pdf-corrupt.pdf').getByTestId('docs-status').innerText()).trim(), { timeout: 90_000 }).not.toMatch(/^(In coda|In lavorazione|Lettura in corso|Estrazione in corso)$/);
      console.log('[T103 real] stato PDF corrotto:', (await docItem(page, 'pdf-corrupt.pdf').getByTestId('docs-status').innerText()).trim());
      await expect(docItem(page, 'pdf-corrupt.pdf').getByTestId('docs-status')).not.toHaveText('Pronto');
      await showVersions(page, 'pdf-corrupt.pdf');
      await expect(docItem(page, 'pdf-corrupt.pdf').getByTestId('docs-issue')).toBeVisible();
      const failedJobs = async () => (await stack.sql<{ n: number }>(`SELECT count(*)::int n FROM "DocumentJobs" WHERE "Kind"::text = 'Parse'`))[0].n;
      const jobsBefore = await failedJobs();
      await page.reload();
      await openDocumentsTab(page);
      await page.waitForTimeout(2_000);
      expect(await failedJobs(), 'ricaricare non ritenta il parsing').toBe(jobsBefore);
      await showVersions(page, 'pdf-corrupt.pdf');
      await docItem(page, 'pdf-corrupt.pdf').getByTestId('docs-retry').click();
      await expect(docItem(page, 'pdf-corrupt.pdf').getByTestId('docs-retry-link')).toBeVisible({ timeout: 30_000 });
      await expect.poll(failedJobs, { timeout: 30_000 }).toBe(jobsBefore + 1);
      await page.waitForTimeout(3_000);
      expect(await failedJobs(), 'un solo nuovo job per clic').toBe(jobsBefore + 1);

      // ---- (b) Guasto TRANSITORIO del modello durante l'estrazione: il dispatcher ritenta e il documento diventa Pronto; la telemetria distingue il tentativo fallito.
      await stack.fault({ graph: 'extract', times: 1, kind: 503 });
      await uploadDocument(page, 'md-basic.md', md1);
      await waitReady(page, 'md-basic.md', 120_000);
      const attempts = await stack.sql<{ State: string; IsFinal: boolean; Attempt: number }>(`SELECT "State"::text "State", "IsFinal", "Attempt" FROM "InferenceCalls" WHERE "DocumentJobId" IS NOT NULL OR "EvidenceExtractionJobId" IS NOT NULL ORDER BY "StartedAt"`);
      console.log('[T103 real] tentativi di inferenza', JSON.stringify(attempts));
      expect((await stack.llm()).requests.filter((r) => r.outcome === 'error:503').length).toBe(1);
      // Primo successo di chat sul documento Pronto: da qui in poi `context-usage.last` e l'ULTIMO SUCCESSO e non cambia per effetto dei guasti.
      const ask = async (text: string) => {
        await page.getByTestId('tab-work').click();
        const box = page.getByLabel("Messaggio per l'assistente");
        await box.fill(text);
        await box.press('Control+Enter');
      };
      await ask('Che cosa prevede la consultazione degli ordini?');
      await expect(page.locator('article[data-role="assistant"]')).toHaveCount(1, { timeout: 90_000 });
      const lastSuccess = await lastOf();
      expect(lastSuccess, 'dopo un successo di chat esiste un ultimo successo misurato').not.toBeNull();
      console.log('[T103 real] context-usage.last', lastSuccess!.slice(0, 200));
      await openDocumentsTab(page);

      // ---- (c) Guasto PERSISTENTE: oltre i tentativi il documento NON diventa Pronto (nessun falso Pronto) e l'ultimo successo registrato resta quello di prima.
      await stack.fault({ graph: 'extract', times: 50, kind: 503 });
      await uploadDocument(page, 'txt-negations.txt');
      await expect.poll(async () => (await docItem(page, 'txt-negations.txt').getByTestId('docs-status').innerText()).trim(), { timeout: 180_000, intervals: [2_000] }).toMatch(/Non riuscit|Fallit|Incomplet|Errore/i);
      console.log('[T103 real] stato dopo guasto persistente:', (await docItem(page, 'txt-negations.txt').getByTestId('docs-status').innerText()).trim());
      await expect(docItem(page, 'txt-negations.txt').getByTestId('docs-status')).not.toHaveText('Pronto');
      expect(await lastOf(), 'l ultimo successo non cambia per effetto dei guasti documentali').toBe(lastSuccess);

      // ---- (d) Readiness: runtime non raggiungibile => il monitor lo dichiara; al ritorno in linea e l'unica porta per ripartire e un comando esplicito del PM.
      await stack.clearFaults();
      await stack.runtime({ mode: 'down' });
      await ask('Quali ordini sono esclusi?');
      await expect(page.locator('.turn-error')).toBeVisible({ timeout: 200_000 });
      expect((await stack.sql<{ Status: string }>(`SELECT "Status"::text "Status" FROM "ChatTurns" ORDER BY "RequestedAt"`)).map((t) => t.Status), 'il turno con il runtime spento e Failed, il precedente resta Completed').toEqual(['Completed', 'Failed']);
      expect(await lastOf(), 'un turno fallito non sostituisce l ultimo successo').toBe(lastSuccess);
      await openDocumentsTab(page);
      await page.waitForTimeout(6_500);                      // oltre Ai:RuntimeCheckSeconds=5
      await page.reload();
      await expect(page.getByTestId('usage-runtime')).toBeVisible({ timeout: 30_000 });
      await expect(page.getByTestId('usage-runtime')).not.toContainText('in linea', { timeout: 30_000 });
      console.log('[T103 real] Server AI (down):', (await page.getByTestId('usage-runtime').innerText()).replace(/\s+/g, ' '));
      await stack.runtime({ mode: 'normal' });
      await page.waitForTimeout(6_500);
      await page.reload();
      await expect(page.getByTestId('usage-runtime')).toContainText('in linea', { timeout: 30_000 });
      // Il comando esplicito del PM (nuovo messaggio) riparte e produce un NUOVO ultimo successo.
      await ask('Chi puo consultare gli ordini?');
      await expect(page.locator('article[data-role="assistant"]')).toHaveCount(2, { timeout: 90_000 });
      expect(await lastOf(), 'il nuovo successo aggiorna l ultimo successo').not.toBe(lastSuccess);
      expect((await stack.llm()).unexpected).toEqual([]);
    });
  });
}

// ============================================================================================================== livello FAKE-STACK (browser, API simulate)

if (!realStack) {
  test.describe('[E2E fake] versioni, rielaborazione, promozione e recupero', () => {
    let fake: FlowStack;
    let parsing: FakeParsing;
    let lifecycle: FakeLifecycle;
    let consoleErrors: string[];
    const P = 'p-1';

    const track = (page: Page) => {
      page.on('pageerror', (e) => consoleErrors.push(`pageerror: ${e.message}`));
      page.on('console', (m) => {
        if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) consoleErrors.push(`console: ${m.text()}`);
      });
    };

    test.beforeEach(async ({ page }) => {
      fake = new FlowStack();
      parsing = new FakeParsing();
      lifecycle = new FakeLifecycle(parsing);
      fake.parsing = parsing;
      fake.lifecycle = lifecycle;
      consoleErrors = [];
      await fake.install(page);
      track(page);
    });

    /** Scritture inviate dal browser, escluse autenticazione, upload, selezione delle fonti e i due comandi del ciclo (rielabora/promuovi) che le prove contano a parte. */
    const stray = () => fake.calls.filter((c) => c.method !== 'GET' && !c.path.startsWith('/api/auth/') && !/\/(documents|source-selection)(\/|$)/.test(c.path) && !/document-jobs/.test(c.path));

    async function fakeLogin(page: Page) {
      await page.goto('/');
      await expect(page).toHaveURL(/\/login$/);
      await page.getByLabel('Nome utente').fill('pm');
      await page.getByLabel('Password').fill('non-un-segreto');
      await page.getByRole('button', { name: 'Accedi' }).click();
      await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible();
    }

    async function fakeOpen(page: Page, withLogin = true) {
      if (withLogin) await fakeLogin(page);
      await page.goto(`/projects/${P}`);
      await expect(page.getByRole('heading', { level: 1, name: 'Aurora' })).toBeVisible();
      await openDocumentsTab(page);
    }

    const rowOf = (page: Page, n: number) => docItem(page, 'md-basic.md').getByTestId('docs-version').filter({ hasText: `v${n} ·` });
    const refresh = async (page: Page) => {
      await page.getByTestId('docs-refresh').click();
      await page.waitForTimeout(250);
    };
    /** Scritture (non GET) del browser, escluso il refresh della sessione che accompagna ogni ricaricamento. */
    const writes = () => fake.calls.filter((c) => c.method !== 'GET' && !c.path.startsWith('/api/auth/'));
    const documentBytes = () => fake.project().documents[0].versions.map((v) => hashOf(v.bytes));

    test('v1/v2, fissa, escludi, rielabora e promuovi: stato letto dal server a ogni riapertura, candidata distinta dalla corrente, originali con lo stesso hash', async ({ page }) => {
      await fakeOpen(page);
      await uploadDocument(page, 'md-basic.md', md1);
      await waitReady(page, 'md-basic.md');
      await uploadVersion(page, 'md-basic.md', md2);
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-version-status')).toHaveText(['Pronto', 'Pronto']);
      expect(documentBytes()).toEqual([hashOf(md1), hashOf(md2)]);
      expect(lifecycle.promotions.map((x) => x.revision), 'ogni versione file ha il suo primo bundle corrente, nessuna candidata').toEqual([1, 1]);
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-candidate')).toHaveCount(0);

      // --- Fissa v1 / ultima pronta / escludi: la preferenza e dello stato del server (sopravvive al ricaricamento).
      await rowOf(page, 1).getByTestId('docs-pin').click();
      await expect(rowOf(page, 1).getByTestId('docs-pinned')).toBeVisible();
      expect([...fake.project().selection.items.values()].map((i) => i.mode)).toEqual(['Pinned']);
      await docItem(page, 'md-basic.md').getByTestId('docs-latest').click();
      await expect(rowOf(page, 1).getByTestId('docs-pinned')).toHaveCount(0);
      await docItem(page, 'md-basic.md').getByTestId('docs-include').uncheck();
      await expect.poll(() => [...fake.project().selection.items.values()].map((i) => i.included)).toEqual([false]);
      await page.reload();
      await openDocumentsTab(page);
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-include')).not.toBeChecked();
      await docItem(page, 'md-basic.md').getByTestId('docs-include').check();
      await expect.poll(() => [...fake.project().selection.items.values()].map((i) => i.included)).toEqual([true]);

      // --- Rielabora v1: la nuova generazione e in lavorazione ("In coda"), il bundle corrente non cambia; a fine lavoro compare la CANDIDATA, pronta ma non corrente.
      parsing.delayPolls = 2;
      await showVersions(page, 'md-basic.md');
      await rowOf(page, 1).getByTestId('docs-reprocess').click();
      await expect(rowOf(page, 1).getByTestId('docs-version-status')).toHaveText('In coda');
      expect(lifecycle.reprocessJobsCreated).toBe(1);
      await expect(rowOf(page, 1).getByTestId('docs-candidate')).toHaveCount(0);
      await refresh(page);
      await expect(rowOf(page, 1).getByTestId('docs-candidate')).toContainText('generazione 2', { timeout: 15_000 });
      await expect(rowOf(page, 1).getByTestId('docs-candidate-note')).toContainText('non è usata come fonte');
      expect(lifecycle.promotions.map((x) => x.revision), 'la candidata non e stata promossa da sola').toEqual([1, 1]);
      await page.screenshot({ path: `${screens}/T103-fake-candidata.png`, fullPage: true });
      await rowOf(page, 1).getByTestId('docs-promote').click();
      await expect(rowOf(page, 1).getByTestId('docs-candidate')).toHaveCount(0);
      expect(lifecycle.promotions.map((x) => x.revision), 'una sola promozione, con la revisione attesa del client').toEqual([1, 1, 2]);

      // --- Originali: scarico identico per entrambe le versioni; lettura/riconnessione non emettono comandi.
      for (const [n, bytes] of [[1, md1], [2, md2]] as const) {
        const [download] = await Promise.all([page.waitForEvent('download'), rowOf(page, n).getByTestId('docs-download').click()]);
        expect(hashOf(readFileSync((await download.path())!))).toBe(hashOf(bytes));
      }
      const before = fake.calls.length;
      await page.reload();
      await openDocumentsTab(page);
      await showVersions(page, 'md-basic.md');
      await fake.drop(page);
      await page.waitForTimeout(2_500);
      expect(fake.calls.slice(before).filter((c) => c.method !== 'GET' && !c.path.startsWith('/api/auth/')), 'ricarica e riconnessione: solo letture').toEqual([]);
      expect(lifecycle.reprocessJobsCreated).toBe(1);
      expect(stray()).toEqual([]);
      expect(consoleErrors).toEqual([]);
    });

    test('due sessioni PM: promozione simultanea = una sola promozione; promozione con vista scaduta = conflitto esplicito e vista riletta', async ({ page, browser }) => {
      const other = await browser.newContext({ baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:4200', viewport: { width: 1440, height: 900 } });
      const pageB = await other.newPage();
      await fake.install(pageB);
      track(pageB);
      try {
        await fakeOpen(page);
        await uploadDocument(page, 'md-basic.md', md1);
        await waitReady(page, 'md-basic.md');
        await fakeOpen(pageB, false);
        await showVersions(pageB, 'md-basic.md');

        const reprocessAndWait = async () => {
          parsing.delayPolls = 1;
          await rowOf(page, 1).getByTestId('docs-reprocess').click();
          await refresh(page);
          await expect(rowOf(page, 1).getByTestId('docs-promote')).toBeVisible({ timeout: 15_000 });
        };
        await showVersions(page, 'md-basic.md');
        await reprocessAndWait();                                    // gen 2
        await refresh(pageB);
        await showVersions(pageB, 'md-basic.md');
        await expect(rowOf(pageB, 1).getByTestId('docs-promote')).toBeVisible();

        // Promozione simultanea della stessa candidata: una promozione reale, l'altra e un replay (200): nessun errore, nessuna seconda promozione.
        await Promise.all([rowOf(page, 1).getByTestId('docs-promote').click(), rowOf(pageB, 1).getByTestId('docs-promote').click()]);
        await expect(rowOf(page, 1).getByTestId('docs-candidate')).toHaveCount(0);
        await expect(rowOf(pageB, 1).getByTestId('docs-candidate')).toHaveCount(0);
        expect(lifecycle.promotions.filter((x) => x.revision === 2), 'una sola promozione alla revisione 2').toHaveLength(1);

        // Nuova rielaborazione (g3) e promozione da A (revisione 3); B la vede come candidata con la bundleRevision di allora.
        await reprocessAndWait();                                    // gen 3
        await refresh(pageB);
        await showVersions(pageB, 'md-basic.md');
        await expect(rowOf(pageB, 1).getByTestId('docs-promote')).toBeVisible();   // B vede la g3 con bundleRevision 2
        await rowOf(page, 1).getByTestId('docs-promote').click();                   // A promuove g3 -> revisione 3
        await expect(rowOf(page, 1).getByTestId('docs-candidate')).toHaveCount(0);
        // La g3 e ora la corrente: un clic di B sarebbe un replay. Per il conflitto A promuove ancora una g4 (revisione 4) e B agisce con la vista scaduta della g3.
        await reprocessAndWait();                                    // gen 4
        await rowOf(page, 1).getByTestId('docs-promote').click();                   // A promuove g4 -> revisione 4
        await expect(rowOf(page, 1).getByTestId('docs-candidate')).toHaveCount(0);
        await rowOf(pageB, 1).getByTestId('docs-promote').click();                  // B: promuove la g3 con expectedRevision=2 ma il bundle e a 4
        await expect(rowOf(pageB, 1).getByTestId('docs-promote-error')).toContainText('Il bundle corrente è cambiato', { timeout: 10_000 });
        expect(lifecycle.promotions.map((x) => x.revision).filter((r) => r >= 2), 'nessuna promozione da B scaduta').toEqual([2, 3, 4]);
        // La vista di B e stata riletta dal server: la g3 e storica (non piu candidata), nessun comando ripetuto da solo.
        await expect(rowOf(pageB, 1).getByTestId('docs-candidate')).toHaveCount(0);
        const promotesB = fake.calls.filter((c) => c.method === 'POST' && /\/promote$/.test(c.path)).length;
        await pageB.waitForTimeout(1_000);
        expect(fake.calls.filter((c) => c.method === 'POST' && /\/promote$/.test(c.path)).length, 'nessuna ripetizione automatica').toBe(promotesB);
        expect(consoleErrors).toEqual([]);
      } finally {
        await other.close();
      }
    });

    test('browser chiuso e riaperto su 3 sessioni con riavvio del server (anche a meta rielaborazione): stato dal server, una sola candidata, nessun comando da sola lettura', async ({ browser }) => {
      const baseURL = process.env['E2E_BASE_URL'] ?? 'http://localhost:4200';
      const open = async () => {
        const ctx = await browser.newContext({ baseURL, viewport: { width: 1440, height: 900 } });
        const page = await ctx.newPage();
        await fake.install(page);
        track(page);
        return { ctx, page };
      };
      const restart = async () => {
        fake.down = true;
        await new Promise((r) => setTimeout(r, 1_200));
        fake.down = false;
      };

      // ---- Sessione 1: login, v1 Pronto, fissata; il browser viene chiuso.
      let { ctx, page } = await open();
      await fakeOpen(page);
      await uploadDocument(page, 'md-basic.md', md1);
      await waitReady(page, 'md-basic.md');
      await showVersions(page, 'md-basic.md');
      await rowOf(page, 1).getByTestId('docs-pin').click();
      await expect(rowOf(page, 1).getByTestId('docs-pinned')).toBeVisible();
      await ctx.close();
      await restart();
      const writesAfterS1 = writes().length;

      // ---- Sessione 2: riapertura (la sessione di refresh e del server): lettura pura; poi v2 e rielaborazione, con riavvio MENTRE la nuova generazione e in coda.
      ({ ctx, page } = await open());
      await fakeOpen(page, false);
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-status')).toHaveText('Pronto');
      await showVersions(page, 'md-basic.md');
      await expect(rowOf(page, 1).getByTestId('docs-pinned'), 'il pin e dello stato del server').toBeVisible();
      expect(writes().length, 'la riapertura non emette comandi').toBe(writesAfterS1);
      await uploadVersion(page, 'md-basic.md', md2);
      await expect(rowOf(page, 2).getByTestId('docs-version-status')).toHaveText('Pronto');
      parsing.delayPolls = 3;
      await rowOf(page, 1).getByTestId('docs-reprocess').click();
      await expect(rowOf(page, 1).getByTestId('docs-version-status')).toHaveText('In coda');
      fake.down = true;                                              // il server si riavvia a meta lavoro: la UI non inventa esiti ne ripete il comando
      await page.waitForTimeout(1_500);
      await refresh(page);
      fake.down = false;
      expect(lifecycle.reprocessJobsCreated, 'nessun secondo job di rielaborazione per il riavvio').toBe(1);
      await ctx.close();

      // ---- Sessione 3: browser riaperto; il lavoro in coda prosegue lato server (tick delle letture); una sola candidata; promozione.
      ({ ctx, page } = await open());
      await fakeOpen(page, false);
      await showVersions(page, 'md-basic.md');
      for (let i = 0; i < 4 && (await rowOf(page, 1).getByTestId('docs-promote').count()) === 0; i++) await refresh(page);
      await expect(rowOf(page, 1).getByTestId('docs-promote')).toBeVisible();
      expect(lifecycle.reprocessJobsCreated).toBe(1);
      expect(parsing.gens.get(fake.project().documents[0].versions[0].id)!.map((g) => g.generation), 'generazioni 1 e 2, nessun duplicato').toEqual([1, 2]);
      await rowOf(page, 1).getByTestId('docs-promote').click();
      await expect(rowOf(page, 1).getByTestId('docs-candidate')).toHaveCount(0);
      expect(lifecycle.promotions.map((x) => x.revision)).toEqual([1, 1, 2]);
      await restart();
      await page.reload();
      await openDocumentsTab(page);
      await showVersions(page, 'md-basic.md');
      await expect(rowOf(page, 1).getByTestId('docs-pinned')).toBeVisible();
      expect(documentBytes()).toEqual([hashOf(md1), hashOf(md2)]);
      expect(stray()).toEqual([]);
      expect(consoleErrors).toEqual([]);
      await ctx.close();
    });

    test('errori, retry, readiness e ultimo successo: retry unico del parsing, nessun falso Pronto, runtime non raggiungibile dichiarato, ultimo successo conservato', async ({ page }) => {
      fake.project().usage = usageOf({ kind: 'chat', refId: 'm-2', metrics: metricsOf({ phase: 'chat', calls: 1 }) });
      parsing.outcomes = ['Failed'];
      await fakeOpen(page);
      await uploadDocument(page, 'md-basic.md', md1);
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-status')).toHaveText('Preparazione non riuscita');
      await showVersions(page, 'md-basic.md');
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-issue')).toBeVisible();
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-status')).not.toHaveText('Pronto');
      await page.reload();
      await openDocumentsTab(page);
      await page.waitForTimeout(1_000);
      expect(parsing.retryJobsCreated, 'ricaricare non ritenta').toBe(0);

      // --- Retry: un solo nuovo job per clic; esito a Ready solo quando lo dice il server.
      parsing.delayPolls = 2;
      await showVersions(page, 'md-basic.md');
      await docItem(page, 'md-basic.md').getByTestId('docs-retry').click();
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-retry-link')).toBeVisible();
      expect(parsing.retryJobsCreated).toBe(1);
      await expect(docItem(page, 'md-basic.md').getByTestId('docs-status')).not.toHaveText('Pronto');
      await refresh(page);
      await waitReady(page, 'md-basic.md');
      expect(parsing.retryJobsCreated, 'un solo nuovo job').toBe(1);

      // --- Readiness e ultimo successo: il runtime non raggiungibile e dichiarato; l'ultimo successo misurato resta quello di prima; al ritorno in linea torna Online.
      await page.getByTestId('tab-work').click();
      if (page.viewportSize()!.width < 1200) await page.getByTestId('usage-toggle').click();
      await expect(page.getByTestId('usage-runtime')).toContainText('in linea');
      const headline = await page.getByTestId('usage-headline').innerText();
      expect(headline).toMatch(/\d/);
      const usage = fake.project().usage as Record<string, any>;
      usage['runtime'] = { ...usage['runtime'], status: 'offline', contextWindowTokens: { value: null, source: 'unavailable' } };
      await page.reload();
      await expect(page.getByTestId('usage-runtime')).not.toContainText('in linea');
      await expect(page.getByTestId('usage-runtime')).toContainText('non raggiungibile');
      expect(await page.getByTestId('usage-headline').innerText(), 'l ultimo successo misurato resta quello di prima').toBe(headline);
      usage['runtime'] = { ...usage['runtime'], status: 'online', contextWindowTokens: { value: 32768, source: 'measured' } };
      await page.reload();
      await expect(page.getByTestId('usage-runtime')).toContainText('in linea');
      expect(stray()).toEqual([]);
      expect(consoleErrors).toEqual([]);
    });
  });
}
