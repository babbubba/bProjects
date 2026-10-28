import { execSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';

// T043: primo percorso completo con servizi reali (Angular + API + PostgreSQL + modello DGX):
// login -> creazione -> chat con streaming -> Analizza -> anteprima corretta e salvata -> conferma -> Genera -> bozza;
// riavvio dello stack (volume conservato) e tre sessioni conservano gli esiti.
//
// Variabili (senza E2E_MODEL_MODE=real la prova risulta ignorata, mai superata: richiede endpoint, modello e chiave DGX
// reali in infra/.env dello stack di prova, nessun'altra coda):
//   E2E_BASE_URL, E2E_USER, E2E_PASSWORD   stack di prova con account PM;
//   E2E_MODEL_MODE=real                    conferma che lo stack usa il modello reale;
//   E2E_RESTART_COMMAND                    riavvia lo stack conservando il volume PostgreSQL (obbligatoria per questa prova).
// Un errore o un timeout in qualunque fase fa fallire la prova: nessun fallimento e' trattato come successo.
const user = process.env['E2E_USER'];
const password = process.env['E2E_PASSWORD'];
const mode = process.env['E2E_MODEL_MODE'];
const restartCommand = process.env['E2E_RESTART_COMMAND'];
const BUDGET = 180_000;

test.skip(!user || !password, 'E2E_USER ed E2E_PASSWORD non impostate: stack di prova non disponibile.');

const unique = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

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

async function send(page: Page, text: string) {
  await page.getByLabel("Messaggio per l'assistente").fill(text);
  await page.getByRole('button', { name: 'Invia', exact: true }).click();
}

interface Snapshot {
  messages: number;
  replies: string[];
  goal: string;
  revisionInfo: string;
  markdown: string;
}

/** Stato osservabile dalla UI di un progetto riaperto: dialogo, anteprima confermata e bozza. */
async function observe(page: Page): Promise<Snapshot> {
  await expect(page.getByTestId('preview')).toBeVisible();
  await expect(page.getByTestId('draft')).toBeVisible();
  return {
    messages: await page.locator('article[data-role="user"]').count(),
    replies: await page.locator('article[data-role="assistant"] .msg-text').allTextContents(),
    goal: (await page.locator('[data-section=goals] [data-testid=item-text]').first().inputValue()).trim(),
    revisionInfo: ((await page.getByTestId('revision-info').textContent()) ?? '').replace(/\s+/g, ' ').trim().replace(/ · \d.*$/, ''),
    markdown: ((await page.getByTestId('markdown').textContent()) ?? '').trim(),
  };
}

test.describe('percorso completo con modello reale', () => {
  test.skip(mode !== 'real', 'E2E_MODEL_MODE=real non impostata: serve uno stack con DGX Spark raggiungibile e chiave configurata (AI_ENDPOINT/AI_MODEL_ID/AI_API_KEY in infra/.env).');

test('percorso completo con modello reale: chat, Analizza, correzione, conferma, Genera, riavvio e tre sessioni', async ({ page, browser, request }, info) => {
  test.skip(info.project.name !== 'desktop-1440', 'Prova eseguita una sola volta (desktop).');
  test.skip(!restartCommand, "E2E_RESTART_COMMAND non impostata: il riavvio dello stack e' parte essenziale della prova.");
  test.setTimeout(30 * 60_000);

  const name = `Percorso ${unique()}`;
  const messages = [
    'Voglio realizzare un portale clienti chiamato Aurora per gestire i ticket di assistenza: login con email, elenco ticket, allegati.',
    "Gli utenti sono i clienti finali e gli agenti dell'assistenza. Le notifiche via SMS sono escluse dal primo rilascio.",
    'Non so ancora quanti clienti useranno il portale; lo scopriremo dopo il primo trimestre. La fatturazione resta fuori perimetro.',
  ];
  const contextOptions = { baseURL: info.project.use.baseURL, viewport: { width: 1440, height: 900 } };

  // Le uniche scritture ammesse sono i comandi del PM: nessuna invocazione automatica di Analizza/Genera.
  const writes: string[] = [];
  page.on('request', (r) => {
    if (r.method() !== 'GET' && new URL(r.url()).pathname.startsWith('/api/')) {
      writes.push(`${r.method()} ${new URL(r.url()).pathname.replace(/[0-9a-f-]{36}/g, ':id')}`);
    }
  });
  const count = (re: RegExp) => writes.filter((w) => re.test(w)).length;

  const token = async () => {
    const response = await request.post('/api/auth/login', { data: { username: user, password } });
    expect(response.ok()).toBe(true);
    return ((await response.json()) as { accessToken: string }).accessToken;
  };
  const getJson = async <T>(path: string) => {
    const response = await request.get(path, { headers: { Authorization: `Bearer ${await token()}` } });
    expect(response.status(), path).toBe(200);
    return (await response.json()) as T;
  };

  // --- 1. Login, creazione, chat con streaming reale.
  await login(page);
  await createProject(page, name);
  const projectId = /\/projects\/([0-9a-f-]{36})/.exec(page.url())![1];

  const replies: string[] = [];
  for (const [i, text] of messages.entries()) {
    await page.evaluate(() => {
      (window as unknown as { __streamSeen: boolean }).__streamSeen = false;
      new MutationObserver(() => {
        if (document.querySelector('article[data-role="assistant-streaming"] .msg-text')?.textContent?.trim()) {
          (window as unknown as { __streamSeen: boolean }).__streamSeen = true;
        }
      }).observe(document.body, { childList: true, subtree: true, characterData: true });
    });
    const started = Date.now();
    await send(page, text);
    await expect(page.locator('article[data-role="user"] .saved-badge')).toHaveCount(i + 1, { timeout: 10_000 });
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(i + 1, { timeout: BUDGET });
    expect(Date.now() - started, `scambio ${i + 1} oltre 180s`).toBeLessThanOrEqual(BUDGET);
    expect(await page.evaluate(() => (window as unknown as { __streamSeen: boolean }).__streamSeen), `scambio ${i + 1}: streaming non osservato`).toBe(true);
    replies.push(((await page.locator('article[data-role="assistant"] .msg-text').last().textContent()) ?? '').trim());
  }
  // La chat non ha creato alcuna elaborazione formale.
  expect(count(/\/analyses|document-generations/)).toBe(0);
  await expect(page.getByTestId('preview')).toHaveCount(0);

  // --- 2. Analizza: un solo comando, anteprima entro 180s, nessun errore.
  const analyzeStart = Date.now();
  await page.getByTestId('analyze').click();
  await expect(page.getByTestId('run-error')).toHaveCount(0);
  await expect(page.getByTestId('preview')).toBeVisible({ timeout: BUDGET });
  expect(Date.now() - analyzeStart, 'analisi oltre 180s').toBeLessThanOrEqual(BUDGET);
  await expect(page.getByTestId('run-error')).toHaveCount(0);
  await expect(page.getByTestId('validation-badge')).toBeVisible();
  await expect(page.getByTestId('revision-info')).toContainText("generata dall'AI");
  await expect(page.getByTestId('generate')).toHaveCount(0); // niente Genera prima della conferma
  expect(count(/POST \/api\/projects\/:id\/analyses$/)).toBe(1);
  expect(count(/document-generations/)).toBe(0);

  // --- 3. Correzione e salvataggio (nuova revisione PM), poi conferma esplicita.
  const corrected = `Obiettivo corretto dal PM ${unique()}`;
  await page.locator('[data-section=goals] [data-testid=item-text]').first().fill(corrected);
  await page.getByTestId('save').click();
  await expect(page.getByTestId('revision-info')).toContainText('modificata dal PM');
  await expect(page.getByTestId('dirty-badge')).toHaveCount(0);
  await page.getByTestId('confirm').click();
  await expect(page.getByTestId('confirmed-badge')).toBeVisible();
  await expect(page.getByTestId('generate')).toBeEnabled();
  expect(count(/document-generations/), 'la conferma non deve generare').toBe(0);

  // --- 4. Genera: bozza entro 180s dal proprio comando.
  const generateStart = Date.now();
  await page.getByTestId('generate').click();
  await expect(page.getByTestId('generation-error')).toHaveCount(0);
  await expect(page.getByTestId('draft')).toBeVisible({ timeout: BUDGET });
  expect(Date.now() - generateStart, 'generazione oltre 180s').toBeLessThanOrEqual(BUDGET);
  await expect(page.getByTestId('generation-error')).toHaveCount(0);
  await expect(page.getByTestId('draft-status')).toHaveText('Proposta / non approvata');
  await expect(page.getByTestId('draft-label')).toContainText('Bozza AI');
  await expect(page.getByTestId('stale-banner')).toHaveCount(0);
  expect(count(/POST \/api\/projects\/:id\/analyses\/:id\/document-generations$/)).toBe(1);
  expect(count(/POST \/api\/projects\/:id\/analyses$/)).toBe(1);

  // --- 5. Esito via API: bozza legata alla revisione confermata (non alla v1 AI), Proposed/NotApproved.
  const draft = await getJson<{
    id: string; analysisId: string; analysisRevisionId: string; analysisVersion: number; status: string; approvalStatus: string; markdown: string; hasNewRevision: boolean;
  }>(`/api/projects/${projectId}/draft`);
  expect(draft.status).toBe('Proposed');
  expect(draft.approvalStatus).toBe('NotApproved');
  expect(draft.analysisVersion).toBeGreaterThanOrEqual(2);
  expect(draft.hasNewRevision).toBe(false);
  expect(draft.markdown.trim().length).toBeGreaterThan(0);
  const revision = await getJson<{ validationState: string; isCurrent: boolean; content: { goals: { text: string }[] } }>(
    `/api/projects/${projectId}/analyses/${draft.analysisId}/revisions/${draft.analysisVersion}`,
  );
  expect(revision.validationState).toBe('PmConfirmed');
  expect(revision.isCurrent).toBe(true);
  expect(revision.content.goals[0].text).toBe(corrected);

  const before = await observe(page);
  expect(before.messages).toBe(messages.length);
  expect(before.replies).toEqual(replies);
  expect(before.goal).toBe(corrected);

  // --- 6. Riapertura, riavvio dell'intero stack con volume conservato, e tre sessioni indipendenti.
  await reopen(page, name);
  expect(await observe(page)).toEqual(before);

  execSync(restartCommand!, { stdio: 'inherit', timeout: 8 * 60_000 });

  const contexts = [await browser.newContext(contextOptions), await browser.newContext(contextOptions), await browser.newContext(contextOptions)];
  try {
    for (const context of contexts) {
      const session = await context.newPage();
      await login(session);
      await reopen(session, name);
      expect(await observe(session)).toEqual(before);
      await expect(session.getByTestId('confirmed-badge')).toBeVisible();
      await expect(session.getByTestId('draft-status')).toHaveText('Proposta / non approvata');
      await expect(session.getByTestId('run-error')).toHaveCount(0);
      await expect(session.getByTestId('generation-error')).toHaveCount(0);
    }
  } finally {
    await Promise.all(contexts.map((c) => c.close()));
  }

  // Dopo il riavvio nulla e' stato rielaborato: stessa bozza, nessun run attivo.
  const after = await getJson<{ id: string }>(`/api/projects/${projectId}/draft`);
  const view = await getJson<{ activeProcessingRunId: string | null }>(`/api/projects/${projectId}`);
  expect(view.activeProcessingRunId).toBeNull();
  expect(after.id).toBe(draft.id);
});
});

test.describe('Analizza con modello offline (prova d\'errore, non e\' successo)', () => {
  test.skip(mode !== 'offline', 'E2E_MODEL_MODE=offline non impostata: serve uno stack con endpoint AI irraggiungibile.');

  test('il run termina Failed con errore comprensibile, nessuna anteprima mostrata come successo, nessun documento', async ({ page }, info) => {
    test.skip(info.project.name !== 'desktop-1440', 'Prova eseguita una sola volta (desktop).');
    test.setTimeout(6 * 60_000);

    const name = `Analisi offline ${unique()}`;
    await login(page);
    await createProject(page, name);
    await send(page, `Voglio un portale clienti ${unique()}`);
    await expect(page.locator('article[data-role="user"] .saved-badge')).toHaveCount(1, { timeout: 10_000 });

    await page.getByTestId('analyze').click();
    const error = page.getByTestId('run-error');
    await expect(error).toBeVisible({ timeout: BUDGET + 30_000 });
    await expect(error).toContainText('Analisi non riuscita');
    await expect(error).not.toContainText(/127\.0\.0\.1|HttpRequestException|Exception|at bProjects/);
    await expect(page.getByTestId('preview')).toHaveCount(0);
    await expect(page.getByTestId('confirm')).toHaveCount(0);
    await expect(page.getByTestId('generate')).toHaveCount(0);
    await expect(page.getByTestId('draft')).toHaveCount(0);
    await expect(page.getByTestId('run-state')).toHaveCount(0);

    // Il fallimento persiste alla riapertura e non diventa mai un risultato.
    await reopen(page, name);
    await expect(page.getByTestId('preview')).toHaveCount(0);
    await expect(page.getByTestId('draft')).toHaveCount(0);
    await expect(page.getByTestId('analyze')).toBeEnabled();
  });
});
