import { execSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';

// Checkpoint T036: revisione e conferma dell'anteprima, persistenti, su stack reale (Angular + API + PostgreSQL).
//
// IMPORTANTE - la revisione AI di partenza e' SEMINATA, non generata. Nell'ambiente di prova non c'e' alcuna chiave DGX,
// quindi l'analisi AI non puo' essere prodotta. Questa prova verifica la modifica, il salvataggio, la conferma e la
// persistenza dell'anteprima (non la qualita' dell'AI): per questo la revisione v1 (Origin=Ai, Unvalidated, JSON valido
// AnalysisContent 1.0.0, fonte = il vero messaggio PM scritto dalla UI) viene inserita direttamente nel PostgreSQL di
// uno stack di prova dedicato, con lo stesso effetto di AnalysisRunCompletion: ProcessingRun Completed + AnalysisRevision
// v1 + Project.CurrentAnalysisId. Tutto il resto (login, progetto, messaggio, edit, conflitti, conferma, riavvio) passa dalla UI e dall'API reali.
//
// Variabili (senza E2E_DB_COMMAND la prova risulta ignorata, mai superata):
//   E2E_BASE_URL, E2E_USER, E2E_PASSWORD   stack di prova con account PM (bootstrap-accounts);
//   E2E_DB_COMMAND       comando che esegue psql -At sul DB dello STACK DI PROVA leggendo l'SQL da stdin
//                        (es. docker exec -i <progetto>-db-1 psql -U bprojects -d bprojects -v ON_ERROR_STOP=1 -At);
//   E2E_RESTART_COMMAND  riavvia lo stack conservando il volume PostgreSQL (down senza -v, poi up --wait).
const user = process.env['E2E_USER'];
const password = process.env['E2E_PASSWORD'];
const dbCommand = process.env['E2E_DB_COMMAND'];
const restartCommand = process.env['E2E_RESTART_COMMAND'];

test.skip(!user || !password, 'E2E_USER ed E2E_PASSWORD non impostate: stack di prova non disponibile.');

const unique = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
const timeFormat = new Intl.DateTimeFormat('it-IT', { dateStyle: 'short', timeStyle: 'short' });

async function login(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill(user!);
  await page.getByLabel('Password').fill(password!);
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible();
}

async function openSidebar(page: Page) {
  if (page.viewportSize()!.width >= 992) return;
  const toggle = page.locator('.shell-toggle');
  await expect(toggle).toBeVisible();
  if ((await toggle.getAttribute('aria-expanded')) === 'false') await toggle.click();
}

async function createProject(page: Page, name: string) {
  await openSidebar(page);
  await page.getByRole('button', { name: 'Nuovo progetto' }).click();
  await page.getByLabel('Nome', { exact: true }).fill(name);
  await page.getByRole('button', { name: 'Crea progetto' }).click();
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
}

async function reopen(page: Page, name: string) {
  await page.goto('/');
  await openSidebar(page);
  await page.getByRole('navigation', { name: 'Elenco progetti' }).getByRole('link', { name }).first().click();
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
}

/** Esegue SQL sul DB dello stack di prova e restituisce lo stdout (psql -At). */
function sql(statement: string): string {
  return execSync(dbCommand!, { input: statement, encoding: 'utf8' }).trim();
}

/** Contenuto AnalysisContent 1.0.0 valido prodotto "come l'AI": Unvalidated, fonte Message utente reale, conflitti Open. */
function seededAiContent(ids: { analysisId: string; projectId: string; cutoff: number; messageId: string }) {
  const s1 = randomUUID();
  const item = (text: string, classification: 'Declared' | 'Inferred' | 'Assumed', refs: string[] = [s1]) => ({
    id: randomUUID(),
    text,
    classification,
    evidenceRefs: refs,
  });
  const conflict = (description: string, a: string, b: string) => ({
    id: randomUUID(),
    description,
    status: 'Open',
    alternatives: [item(a, 'Declared'), item(b, 'Declared')],
    resolution: null,
  });
  return {
    schemaVersion: '1.0.0',
    analysisId: ids.analysisId,
    projectId: ids.projectId,
    sourceCutoffSequence: ids.cutoff,
    validationState: 'Unvalidated',
    sources: [{ id: s1, kind: 'Message', messageId: ids.messageId, messageRole: 'user', revisionId: null }],
    goals: [item('Realizzare il portale clienti Aurora', 'Declared')],
    contextAndScope: {
      overview: [item('Portale per la gestione dei ticket di assistenza', 'Inferred')],
      included: [item('Login con email', 'Declared')],
      excluded: [item('Fatturazione nel portale', 'Declared')],
    },
    requirements: [item('Elenco ticket con filtri', 'Declared'), item('Allegati fino a 10 MB', 'Inferred')],
    operationalFlows: [item('Il cliente apre un ticket e un agente lo prende in carico', 'Inferred')],
    constraints: [item('Primo rilascio entro giugno', 'Declared')],
    dependencies: [item('Integrazione con il CRM aziendale', 'Declared')],
    assumptions: [item('Gli utenti attesi sono circa 500', 'Assumed', [])],
    openQuestions: [item('Quali SLA di risposta si applicano?', 'Assumed', [])],
    conflicts: [
      conflict('Hosting: cloud o on-premise?', 'Cloud pubblico', 'Server aziendali'),
      conflict('Notifiche: email o SMS?', 'Solo email', 'Email e SMS'),
    ],
  };
}

/** Inserisce la revisione AI v1 come farebbe AnalysisRunCompletion (vedi il commento in testa al file). */
function seedAiRevision(ids: { projectId: string; conversationId: string; pm: string; cutoff: number; messageId: string }) {
  const analysisId = randomUUID();
  const revisionId = randomUUID();
  const content = seededAiContent({ analysisId, projectId: ids.projectId, cutoff: ids.cutoff, messageId: ids.messageId });
  sql(`
BEGIN;
INSERT INTO "ProcessingRuns" ("Id","ProjectId","Kind","ConversationId","SourceCutoffSequence","RequestKey","RequestedBy","RequestedAt","Status","StartedAt","DeadlineAt","WorkflowDefinitionVersion","MafVersion","AttemptCount","MaxAttempts","FencingToken","UpdatedAt","LastObservedStage")
VALUES ('${analysisId}','${ids.projectId}','Analysis','${ids.conversationId}',${ids.cutoff},gen_random_uuid(),'${ids.pm}',now() - interval '2 minutes','Running',now() - interval '2 minutes',now() + interval '3 minutes','project-analysis-v2','1.26.0',1,3,1,now(),'ValidatePersistAnalysis');
INSERT INTO "AnalysisRevisions" ("Id","AnalysisId","Version","ParentRevisionId","Origin","CreatedBy","CreatedAt","SchemaVersion","ContentJson","ValidationState")
VALUES ('${revisionId}','${analysisId}',1,NULL,'Ai','${ids.pm}',now() - interval '1 minute','1.0.0',$json$${JSON.stringify(content)}$json$::jsonb,'Unvalidated');
UPDATE "ProcessingRuns" SET "Status"='Completed',"ResultRevisionId"='${revisionId}',"CompletedAt"=now(),"UpdatedAt"=now() WHERE "Id"='${analysisId}';
UPDATE "Projects" SET "CurrentAnalysisId"='${analysisId}' WHERE "Id"='${ids.projectId}';
COMMIT;`);
  return { analysisId, revisionId, content };
}

interface ApiRevision {
  version: number;
  origin: string;
  validationState: string;
  createdBy: string | null;
  createdAt: string;
  confirmedBy: string | null;
  isCurrent: boolean;
  content: {
    goals: { text: string }[];
    sources: { kind: string }[];
    conflicts: { status: string; resolution: { reason: string } | null }[];
  } & Record<string, unknown>;
}

test.describe('anteprima: revisione e conferma persistenti (revisione AI seminata)', () => {
  test.use({ actionTimeout: 15_000 }); // un controllo bloccato fallisce subito invece di attendere il timeout del test
  test.skip(!dbCommand, 'E2E_DB_COMMAND non impostata: serve il DB dello stack di prova per seminare la revisione AI.');

  test('edit completi, conflitto di revisione, salvataggio, conferma e riapertura dopo riavvio e giorni simulati', async (
    { page, browser, request },
    info,
  ) => {
    test.skip(info.project.name !== 'desktop-1440', 'Prova eseguita una sola volta (desktop).');
    test.skip(!restartCommand, 'E2E_RESTART_COMMAND non impostata: il riavvio dello stack e\' parte essenziale della prova.');
    test.setTimeout(10 * 60_000);

    const name = `Anteprima ${unique()}`;
    const original = `Voglio un portale clienti Aurora per i ticket di assistenza ${unique()}`;
    const contextOptions = { baseURL: info.project.use.baseURL, viewport: { width: 1440, height: 900 } };

    // Nessuna chiamata documentale ne' approvazione cliente: nessuna richiesta di scrittura fuori da chat/PUT/confirm.
    const writes: string[] = [];
    const watch = (p: Page) =>
      p.on('request', (r) => {
        if (r.method() !== 'GET' && new URL(r.url()).pathname.startsWith('/api/')) {
          writes.push(`${r.method()} ${new URL(r.url()).pathname}`);
        }
      });
    watch(page);

    const token = async () => {
      const response = await request.post('/api/auth/login', { data: { username: user, password } });
      expect(response.ok()).toBe(true);
      return ((await response.json()) as { accessToken: string }).accessToken;
    };
    const getJson = async <T>(path: string) => {
      const response = await request.get(path, { headers: { Authorization: `Bearer ${await token()}` } });
      return { status: response.status(), body: (await response.json().catch(() => null)) as T };
    };

    // --- 1. Progetto e messaggio PM reali dalla UI.
    await login(page);
    await createProject(page, name);
    await page.getByLabel("Messaggio per l'assistente").fill(original);
    await page.getByRole('button', { name: 'Invia', exact: true }).click();
    const message = page.locator('article[data-role="user"]').filter({ hasText: original });
    await expect(message.locator('.saved-badge')).toHaveText('Salvato', { timeout: 10_000 });

    // --- 2. Revisione AI seminata (vedi commento in testa) sul messaggio reale.
    const [projectId, conversationId, pm] = sql(
      `SELECT p."Id" || '|' || c."Id" || '|' || p."CreatedBy" FROM "Projects" p JOIN "Conversations" c ON c."ProjectId" = p."Id" WHERE p."Name" = '${name}';`,
    ).split('|');
    const [messageId, cutoff] = sql(
      `SELECT "Id" || '|' || "Sequence" FROM "ConversationMessages" WHERE "ConversationId" = '${conversationId}' AND "Role" = 'User' ORDER BY "Sequence" LIMIT 1;`,
    ).split('|');
    const seeded = seedAiRevision({ projectId, conversationId, pm, cutoff: Number(cutoff), messageId });
    const base = `/api/projects/${projectId}/analyses/${seeded.analysisId}`;

    // --- 3. Riapertura: l'anteprima AI appare non validata, senza Genera.
    await reopen(page, name);
    const preview = page.getByTestId('preview');
    await expect(preview).toBeVisible();
    await expect(page.getByTestId('validation-badge')).toBeVisible();
    await expect(page.getByTestId('revision-info')).toContainText("Versione 1");
    await expect(page.getByTestId('revision-info')).toContainText("generata dall'AI");
    await expect(page.getByTestId('generate')).toHaveCount(0);
    await expect(page.getByTestId('save')).toBeDisabled();
    await expect(page.getByTestId('confirm')).toBeEnabled();

    // --- 4. Un secondo PM (altra sessione) salva v2 mentre questa vista e' ancora ferma a v1.
    const other = await browser.newContext(contextOptions);
    try {
      const b = await other.newPage();
      watch(b);
      await login(b);
      await reopen(b, name);
      await b.locator('[data-section=goals] [data-testid=item-text]').first().fill('Obiettivo dell\'altra sessione');
      await b.getByTestId('save').click();
      await expect(b.getByTestId('revision-info')).toContainText('Versione 2');
    } finally {
      await other.close();
    }

    // --- 5. Edit completi su tutte le sezioni, conflitti risolti/rinviati; poi Salva con base obsoleta -> conflitto.
    const section = (key: string) => page.locator(`[data-section=${key}]`);
    const text = (key: string) => section(key).getByTestId('item-text');
    await text('goals').first().fill('Obiettivo rivisto dal PM');
    for (const [key, value] of [
      ['overview', 'Contesto aggiunto dal PM'],
      ['included', 'Notifiche email incluse'],
      ['excluded', 'Applicazione mobile esclusa'],
      ['requirements', 'Esportazione dei ticket in CSV'],
      ['flows', 'Chiusura ticket con conferma del cliente'],
      ['constraints', 'Conformita al GDPR'],
      ['dependencies', 'Integrazione con il sistema di posta'],
      ['assumptions', 'Il CRM espone API REST'],
      ['questions', 'Chi approva le modifiche ai ticket?'],
    ] as const) {
      const before = await text(key).count();
      await page.getByTestId(`add-${key}`).click();
      await expect(text(key)).toHaveCount(before + 1); // evita di riempire la voce precedente prima del rendering
      await text(key).last().fill(value);
    }
    await text('requirements').first().fill('Elenco ticket con filtri e ricerca');
    await text('dependencies').first().fill('Integrazione con il CRM (API REST)');
    await text('assumptions').first().fill('Gli utenti attesi sono circa 800');
    await text('questions').first().fill('Quali SLA di risposta si applicano ai clienti premium?');
    await text('excluded').first().fill('Fatturazione e pagamenti nel portale');

    const conflicts = page.getByTestId('conflict');
    // Conflitto 1: descrizione e alternativa corrette, poi risolto con motivazione.
    await conflicts.nth(0).getByTestId('conflict-description').fill('Hosting: cloud oppure on-premise?');
    await conflicts.nth(0).getByTestId('alt-text').nth(0).fill('Cloud pubblico (UE)');
    await expect(conflicts.nth(0).getByTestId('resolve-conflict')).toBeDisabled(); // senza scelta e motivazione
    await conflicts.nth(0).getByTestId('alt-choice').nth(0).check();
    await expect(conflicts.nth(0).getByTestId('resolve-conflict')).toBeDisabled(); // manca la motivazione
    await conflicts.nth(0).getByTestId('resolution-reason').fill('Deciso con il cliente: dati nel cloud UE');
    await conflicts.nth(0).getByTestId('resolve-conflict').click();
    await expect(conflicts.nth(0).getByTestId('conflict-status')).toHaveText('Risolto');
    // Conflitto 2: risolto e poi rinviato (resta Aperto, senza risoluzione).
    await conflicts.nth(1).getByTestId('alt-choice').nth(1).check();
    await conflicts.nth(1).getByTestId('resolution-reason').fill('Provvisorio');
    await conflicts.nth(1).getByTestId('resolve-conflict').click();
    await expect(conflicts.nth(1).getByTestId('conflict-status')).toHaveText('Risolto');
    await conflicts.nth(1).getByTestId('defer-conflict').click();
    await expect(conflicts.nth(1).getByTestId('conflict-status')).toHaveText('Aperto');

    await expect(page.getByTestId('dirty-badge')).toBeVisible();
    await expect(page.getByTestId('save')).toHaveClass(/btn-primary/);
    await expect(page.getByTestId('confirm')).toBeDisabled(); // prima si salva
    await page.getByTestId('save').click();

    // Conflitto di revisione: nessuna nuova revisione, nessuna sovrascrittura, le modifiche restano nel modulo.
    await expect(page.getByTestId('conflict-banner')).toBeVisible();
    await expect(text('goals').first()).toHaveValue('Obiettivo rivisto dal PM');
    await expect(text('assumptions').first()).toHaveValue('Gli utenti attesi sono circa 800');
    await expect(page.getByTestId('save')).toBeDisabled();
    expect((await getJson(`${base}/revisions/3`)).status).toBe(404);
    expect((await getJson<ApiRevision>(`${base}/revisions/2`)).body.content.goals[0].text).toBe("Obiettivo dell'altra sessione");

    // Il PM sceglie di mantenere le proprie modifiche sopra la v2 e salva: nasce la v3.
    await page.getByTestId('conflict-keep').click();
    await expect(page.getByTestId('conflict-banner')).toHaveCount(0);
    await expect(text('goals').first()).toHaveValue('Obiettivo rivisto dal PM');
    await page.getByTestId('save').click();
    await expect(page.getByTestId('revision-info')).toContainText('Versione 3');
    await expect(page.getByTestId('revision-info')).toContainText('modificata dal PM');
    await expect(page.getByTestId('revision-info')).toContainText(user!);
    await expect(page.getByTestId('dirty-badge')).toHaveCount(0);
    await expect(text('goals').first()).toHaveValue('Obiettivo rivisto dal PM');
    await expect(conflicts.nth(0).getByTestId('conflict-status')).toHaveText('Risolto');
    await expect(conflicts.nth(1).getByTestId('conflict-status')).toHaveText('Aperto');
    await expect(section('sources')).toContainText('revisione del PM');

    // --- 6. Conferma: Genera compare solo ora ed e' disponibile, ma NON viene premuto (nessuna generazione automatica).
    await expect(page.getByTestId('generate')).toHaveCount(0);
    await expect(page.getByTestId('confirm')).toHaveClass(/btn-primary/);
    await page.getByTestId('confirm').click();
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    await expect(page.getByTestId('revision-info')).toContainText(`confermata da ${user}`);
    await expect(page.getByTestId('generate')).toBeEnabled();
    await expect(page.getByTestId('confirm')).toBeDisabled();

    // --- 7. Un nuovo edit sulla revisione confermata crea una revisione non confermata: serve una nuova conferma.
    await text('goals').first().fill('Obiettivo rivisto dopo la conferma');
    await page.getByTestId('save').click();
    await expect(page.getByTestId('revision-info')).toContainText('Versione 4');
    await expect(page.getByTestId('validation-badge')).toBeVisible();
    await expect(page.getByTestId('generate')).toHaveCount(0);
    await page.getByTestId('confirm').click();
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    await expect(page.getByTestId('revision-info')).toContainText('Versione 4');

    // --- 8. Persistenza: ricarica, poi "giorni dopo" (date retrodatate di 4 giorni) e riavvio dello stack con il volume.
    await page.reload();
    await expect(page.getByTestId('revision-info')).toContainText('Versione 4');
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    await expect(text('goals').first()).toHaveValue('Obiettivo rivisto dopo la conferma');

    sql(`UPDATE "AnalysisRevisions" SET "CreatedAt" = "CreatedAt" - interval '4 days', "ConfirmedAt" = "ConfirmedAt" - interval '4 days' WHERE "AnalysisId" = '${seeded.analysisId}';`);
    execSync(restartCommand!, { stdio: 'inherit' });
    await expect
      .poll(async () => (await request.get('/health').catch(() => null))?.status() ?? 0, { timeout: 180_000 })
      .toBe(200);

    const later = await browser.newContext(contextOptions);
    try {
      const c = await later.newPage();
      watch(c);
      await login(c);
      await reopen(c, name);
      await expect(c.getByTestId('revision-info')).toContainText('Versione 4');
      await expect(c.getByTestId('confirmed-badge')).toBeVisible();
      await expect(c.getByTestId('generate')).toBeEnabled();
      await expect(c.locator('[data-section=goals] [data-testid=item-text]').first()).toHaveValue('Obiettivo rivisto dopo la conferma');
      await expect(c.locator('[data-section=assumptions] [data-testid=item-text]').first()).toHaveValue('Gli utenti attesi sono circa 800');
      await expect(c.locator('[data-section=conflicts] [data-testid=conflict-status]').first()).toHaveText('Risolto');
      await expect(c.locator('[data-section=conflicts] [data-testid=resolution-reason]').first()).toHaveValue('Deciso con il cliente: dati nel cloud UE');

      const current = (await getJson<ApiRevision>(`${base}/revisions/4`)).body;
      expect(Date.now() - new Date(current.createdAt).getTime()).toBeGreaterThan(3 * 24 * 3600_000);
      await expect(c.getByTestId('revision-info')).toContainText(timeFormat.format(new Date(current.createdAt)));
    } finally {
      await later.close();
    }

    // --- 9. Contenuti originali intatti, autori reali, storico completo (API e DB).
    const v1 = (await getJson<ApiRevision>(`${base}/revisions/1`)).body;
    expect(v1.origin).toBe('Ai');
    expect(v1.validationState).toBe('Unvalidated');
    expect(v1.confirmedBy).toBeNull();
    expect(v1.content).toEqual(seeded.content);
    expect(JSON.parse(sql(`SELECT "ContentJson"::text FROM "AnalysisRevisions" WHERE "AnalysisId" = '${seeded.analysisId}' AND "Version" = 1;`))).toEqual(seeded.content);

    const revisions = await Promise.all([1, 2, 3, 4].map(async (v) => (await getJson<ApiRevision>(`${base}/revisions/${v}`)).body));
    expect(revisions.map((r) => r.origin)).toEqual(['Ai', 'PmEdit', 'PmEdit', 'PmEdit']);
    expect(revisions.map((r) => r.validationState)).toEqual(['Unvalidated', 'Unvalidated', 'PmConfirmed', 'PmConfirmed']);
    expect(revisions.map((r) => r.isCurrent)).toEqual([false, false, false, true]);
    for (const r of revisions.slice(1)) expect(r.createdBy).toBe(user); // autore reale, mai l'AI
    expect(revisions[2].confirmedBy).toBe(user); // v3 confermata e poi congelata: l'edit successivo ha creato la v4
    expect(revisions[3].confirmedBy).toBe(user);
    expect(revisions[1].content.goals[0].text).toBe("Obiettivo dell'altra sessione");
    expect(revisions[2].content.goals[0].text).toBe('Obiettivo rivisto dal PM');
    expect(revisions[2].content.sources.filter((s) => s.kind === 'PmRevision')).toHaveLength(2);
    expect(revisions[3].content.goals[0].text).toBe('Obiettivo rivisto dopo la conferma');
    expect(revisions[2].content.conflicts.map((k) => k.status)).toEqual(['Resolved', 'Open']);
    expect(revisions[2].content.conflicts[1].resolution).toBeNull();

    // --- 10. Nessuna chiamata documentale, nessun nuovo run, nessuna bozza e nessuna approvazione cliente.
    expect(writes.filter((w) => !/\/api\/projects$|\/messages$|\/preview$|\/confirm$|\/auth\//.test(w))).toEqual([]);
    expect(writes.some((w) => /document|draft|generation|approv/i.test(w))).toBe(false);
    expect(sql(`SELECT count(*) FROM "ProcessingRuns" WHERE "ProjectId" = '${projectId}' AND "Kind" = 'Document';`)).toBe('0');
    expect(sql(`SELECT count(*) FROM "ProcessingRuns" WHERE "ProjectId" = '${projectId}';`)).toBe('1'); // solo l'analisi seminata
    expect(sql(`SELECT coalesce("LatestDraftId"::text, 'none') FROM "Projects" WHERE "Id" = '${projectId}';`)).toBe('none');
    // Nessuna entita' di approvazione cliente; la tabella Drafts esiste dal T040 ma questo progetto non ha bozze.
    expect(sql(`SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name ILIKE '%approv%';`)).toBe('0');
    expect(sql(`SELECT count(*) FROM "Drafts" WHERE "ProjectId" = '${projectId}';`)).toBe('0');
    expect(sql(`SELECT count(*) FROM "AnalysisRevisions" WHERE "AnalysisId" = '${seeded.analysisId}';`)).toBe('4');
  });
});
