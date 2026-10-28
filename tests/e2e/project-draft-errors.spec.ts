import { expect, test, type Page } from '@playwright/test';
import { FakeStack } from './support/fake-stack';

// T050: errore, retry e recovery di chat, SSE, anteprima e generazione. Le prove girano contro un'API finta installata nel
// browser (support/fake-stack.ts): servono guasti deterministici (POST con esito incerto, stream caduto, run Failed,
// PUT rifiutato) che uno stack reale con DGX non sa provocare in modo ripetibile. Nessuna variabile d'ambiente e'
// necessaria: l'app e' servita dal dev server Angular avviato da Playwright (o da E2E_BASE_URL).
//
// Sono prove d'errore: non dimostrano che il percorso riuscito funzioni (lo fanno project-draft-success.spec.ts e
// project-chat.spec.ts su stack reale, ignorate senza stack) e non contano come prova del modello.

const P = 'p-1';
const TOKENS =
  'Contesto di 31000 token oltre il budget di input di 24000 token, non riducibile con la compattazione nativa. Gli originali non sono stati modificati.';

async function login(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill('pm');
  await page.getByLabel('Password').fill('non-un-segreto');
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible();
}

async function openSidebar(page: Page) {
  if (page.viewportSize()!.width >= 992) return;
  const toggle = page.locator('.shell-toggle');
  if ((await toggle.getAttribute('aria-expanded')) === 'false') await toggle.click();
}

/** Login e apertura del progetto (ricaricamento completo: la sessione riparte dal refresh). */
async function openProject(page: Page, name = 'Aurora', id = P) {
  await login(page);
  await page.goto(`/projects/${id}`);
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
}

async function connected(page: Page, fake: FakeStack, project = P) {
  await expect.poll(async () => (await fake.connections(page)).filter((c) => c.open && c.project === project).length).toBe(1);
}

async function send(page: Page, text: string) {
  await page.getByLabel("Messaggio per l'assistente").fill(text);
  await page.getByRole('button', { name: 'Invia', exact: true }).click();
}

const goalField = (page: Page) => page.locator('[data-section=goals] [data-testid=item-text]').first();
const userMessages = (page: Page) => page.locator('article[data-role="user"]');

/** Nessun comando AI (Analizza/Genera) e nessuna scrittura oltre a quelle che la prova ha chiesto. */
const writesOf = (fake: FakeStack) => fake.calls.filter((c) => c.method !== 'GET' && !c.path.startsWith('/api/auth/'));

let fake: FakeStack;

test.beforeEach(async ({ page }) => {
  fake = new FakeStack();
  await fake.install(page);
});

test.describe('POST con esito incerto riusa la stessa chiave', () => {
  for (const fault of ['drop-after-save', 'drop-before-save'] as const) {
    test(`messaggio PM: connessione interrotta (${fault}), il testo resta e il reinvio non duplica`, async ({ page }) => {
      fake.fail('postMessage', fault);
      await openProject(page);

      const text = 'Voglio un portale clienti chiamato Aurora';
      await send(page, text);

      // Nessuna conferma di salvataggio inventata: l'esito e' dichiarato incerto e il testo e' conservato.
      const error = page.locator('.send-error');
      await expect(error).toBeVisible();
      await expect(error).toContainText('Non sappiamo se il messaggio sia stato salvato');
      await expect(page.getByLabel("Messaggio per l'assistente")).toHaveValue(text);
      await expect(page.locator('.saved-badge')).toHaveCount(0);

      await error.getByRole('button', { name: 'Verifica e riprova' }).click();
      await expect(page.locator('.saved-badge')).toHaveCount(1);
      await expect(userMessages(page)).toHaveCount(1);
      await expect(error).toBeHidden();

      const posts = fake.callsTo('POST', /\/messages$/);
      expect(posts).toHaveLength(2);
      const bodies = posts.map((c) => c.body as { clientMessageId: string; text: string });
      expect(bodies[1]).toEqual(bodies[0]);
      expect(fake.project().messages.filter((m) => m['role'] === 'user')).toHaveLength(1);
    });
  }

  test('Analizza: 503 dopo la creazione del run, il nuovo comando riusa la Idempotency-Key e non crea un secondo run', async ({ page }) => {
    fake.addPm(P, 'Voglio un portale clienti');
    fake.fail('analyze', 'after-save:503');
    await openProject(page);

    await page.getByTestId('analyze').click();
    await expect(page.getByTestId('command-error')).toContainText('Non sappiamo se la richiesta di analisi sia stata ricevuta');
    await expect(page.getByTestId('run-state')).toHaveCount(0);
    await expect(page.getByTestId('preview')).toHaveCount(0);

    await page.getByTestId('analyze').click();
    await expect(page.getByTestId('run-state')).toBeVisible();
    await expect(page.getByTestId('command-error')).toHaveCount(0);

    const posts = fake.callsTo('POST', /\/analyses$/);
    expect(posts).toHaveLength(2);
    expect(posts[0].key).toBeTruthy();
    expect(posts[1].key).toBe(posts[0].key);
    expect(fake.project().runs).toHaveLength(1);
  });

  test('Genera: connessione interrotta dopo la creazione del run, Verifica e riprova riusa la chiave', async ({ page }) => {
    fake.seedAnalysis();
    fake.fail('generate', 'drop-after-save');
    await openProject(page);

    await page.getByTestId('confirm').click();
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    expect(fake.count('POST', /document-generations$/), 'la conferma non genera').toBe(0);

    await page.getByTestId('generate').click();
    await expect(page.getByTestId('generate-error')).toContainText('Non sappiamo se la richiesta di generazione sia stata ricevuta');
    await expect(page.getByTestId('draft')).toHaveCount(0);

    await page.getByTestId('generate-retry').click();
    await expect(page.getByTestId('generation-state')).toBeVisible();
    const posts = fake.callsTo('POST', /document-generations$/);
    expect(posts).toHaveLength(2);
    expect(posts[1].key).toBe(posts[0].key);
    expect(fake.project().runs.filter((r) => r['kind'] === 'Document')).toHaveLength(1);
  });

  test('dopo un run Failed, che resta alla riapertura, un nuovo comando usa una chiave nuova', async ({ page }) => {
    fake.addPm(P, 'Voglio un portale clienti');
    await openProject(page);
    await connected(page, fake);

    await page.getByTestId('analyze').click();
    await expect(page.getByTestId('run-state')).toBeVisible();
    const first = fake.callsTo('POST', /\/analyses$/)[0].key;

    const failed = fake.setRun(P, 'r-1', { status: 'Failed', completedAt: '2030-01-01T10:05:00Z', error: { code: 'attempts_exhausted', message: null } });
    await fake.send(page, 'processing.state', failed);
    await expect(page.getByTestId('run-error')).toContainText("I tentativi disponibili per l'analisi sono esauriti");

    // Nessuna chiave in localStorage: l'errore si ritrova dal server.
    await page.reload();
    await expect(page.getByTestId('run-error')).toContainText('Analisi non riuscita');
    await expect(page.getByTestId('preview')).toHaveCount(0);
    await expect(page.getByTestId('analyze')).toBeEnabled();

    await page.getByTestId('analyze').click();
    await expect(page.getByTestId('run-state')).toBeVisible();
    const posts = fake.callsTo('POST', /\/analyses$/);
    expect(posts).toHaveLength(2);
    expect(posts[1].key).not.toBe(first);
    expect(fake.project().runs).toHaveLength(2);
  });
});

test.describe('SSE: delta, riconnessione, riconciliazione e polling', () => {
  test('delta duplicati, fuori ordine e di un vecchio tentativo non alterano il testo provvisorio', async ({ page }) => {
    const { turn } = fake.addPm(P, 'Ciao');
    fake.setTurn(P, turn['id'] as string, { status: 'Running', attemptCount: 1 });
    await openProject(page);
    await connected(page, fake);
    await expect(page.locator('.turn-badge')).toHaveText('AI al lavoro');

    const delta = (attemptCount: number, deltaIndex: number, text: string) =>
      fake.send(page, 'chat.delta', { chatTurnId: turn['id'], attemptCount, deltaIndex, text });
    const streaming = page.locator('article[data-role="assistant-streaming"] .msg-text');

    await delta(1, 0, 'Buongiorno ');
    await expect(streaming).toHaveText('Buongiorno');
    await delta(1, 0, 'DUPLICATO ');
    await delta(1, 2, 'SALTATO ');
    await delta(1, 1, 'a tutti');
    await expect(streaming).toHaveText('Buongiorno a tutti');

    // Un tentativo nuovo elimina il buffer del precedente; un ritardatario del vecchio tentativo e' ignorato.
    await delta(2, 0, 'Secondo tentativo');
    await expect(streaming).toHaveText('Secondo tentativo');
    await delta(1, 2, 'VECCHIO');
    await delta(2, 1, '!');
    await expect(streaming).toHaveText('Secondo tentativo!');
    await expect(page.getByText('VECCHIO')).toHaveCount(0);

    // Frammenti interrotti non sono messaggi salvati.
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(0);
    expect(writesOf(fake)).toHaveLength(0);
  });

  test('Completed sostituisce il buffer con il messaggio persistito, una sola volta', async ({ page }) => {
    const { turn } = fake.addPm(P, 'Ciao');
    fake.setTurn(P, turn['id'] as string, { status: 'Running', attemptCount: 1 });
    await openProject(page);
    await connected(page, fake);

    await fake.send(page, 'chat.delta', { chatTurnId: turn['id'], attemptCount: 1, deltaIndex: 0, text: 'bozza provvisoria' });
    await expect(page.locator('article[data-role="assistant-streaming"]')).toHaveCount(1);

    const done = fake.completeTurn(P, turn['id'] as string, 'Risposta autorevole salvata');
    await fake.send(page, 'chat.state', done.turn);
    await expect(page.locator('article[data-role="assistant-streaming"]')).toHaveCount(0);
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(1);
    await expect(page.locator('article[data-role="assistant"] .msg-text')).toHaveText('Risposta autorevole salvata');
    await expect(page.locator('.turn-badge')).toHaveText('Risposta ricevuta');

    // Stesso evento ripetuto: nessun duplicato.
    await fake.send(page, 'chat.state', done.turn);
    await page.waitForTimeout(500);
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(1);
  });

  test('caduta dello stream: buffer scartato, riconnessione, riconciliazione con i GET e nessun nuovo comando', async ({ page }) => {
    const { turn } = fake.addPm(P, 'Ciao');
    fake.setTurn(P, turn['id'] as string, { status: 'Running', attemptCount: 1 });
    await openProject(page);
    await connected(page, fake);
    await fake.send(page, 'chat.delta', { chatTurnId: turn['id'], attemptCount: 1, deltaIndex: 0, text: 'parziale' });
    await expect(page.locator('article[data-role="assistant-streaming"]')).toHaveCount(1);

    // La riconnessione e' rifiutata una volta: il turno si completa mentre lo stream e' assente.
    await fake.refuse(page, 503);
    await fake.drop(page);
    await expect(page.locator('article[data-role="assistant-streaming"]')).toHaveCount(0);
    const done = fake.completeTurn(P, turn['id'] as string, 'Risposta completata mentre eri disconnesso');

    await expect(page.locator('article[data-role="assistant"] .msg-text')).toHaveText(done.message['text'] as string, { timeout: 15_000 });
    await expect(page.locator('.turn-badge')).toHaveText('Risposta ricevuta');
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(1);

    // Ha riaperto lo stream (una sola connessione attiva) e non ha emesso alcun comando.
    await expect.poll(async () => (await fake.connections(page)).filter((c) => c.open).length).toBe(1);
    expect((await fake.connections(page)).every((c) => c.auth === 'Bearer tok-1')).toBe(true);
    expect(writesOf(fake)).toHaveLength(0);
  });

  test('con lo stream indisponibile il polling riconcilia il turno e mostra un errore che arriva solo dal GET', async ({ page }) => {
    const { turn } = fake.addPm(P, 'Ciao');
    fake.setTurn(P, turn['id'] as string, { status: 'Running', attemptCount: 1 });
    await openProject(page);
    await connected(page, fake);
    await fake.refuse(page, 503, 503, 503, 503, 503, 503);
    await fake.drop(page);
    await expect(page.locator('.turn-badge')).toHaveText('AI al lavoro');

    fake.failTurn(P, turn['id'] as string, 'context_overflow', TOKENS);
    await expect(page.locator('.turn-badge')).toHaveText('Risposta AI non riuscita', { timeout: 10_000 });
    const error = page.locator('.turn-error');
    await expect(error).toContainText('supera il contesto gestibile');
    expect(writesOf(fake)).toHaveLength(0);
  });

  test('cambio progetto e logout chiudono lo stream: una sola connessione per progetto aperto', async ({ page }) => {
    fake.addProject('p-2', 'Borealis');
    await openProject(page);
    await connected(page, fake);
    expect((await fake.connections(page)).filter((c) => c.open).map((c) => c.project)).toEqual([P]);

    await openSidebar(page);
    await page.getByRole('navigation', { name: 'Elenco progetti' }).getByRole('link', { name: /Borealis/ }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Borealis' })).toBeVisible();
    await expect.poll(async () => (await fake.connections(page)).filter((c) => c.open).map((c) => c.project)).toEqual(['p-2']);
    // Chat, anteprima e bozza condividono la stessa connessione: una sola per il progetto aperto.
    expect((await fake.connections(page)).filter((c) => c.project === 'p-2')).toHaveLength(1);

    await openSidebar(page);
    await page.getByRole('button', { name: 'Esci' }).click();
    await expect(page).toHaveURL(/\/login$/);
    await expect.poll(async () => (await fake.connections(page)).filter((c) => c.open).length).toBe(0);
    const total = (await fake.connections(page)).length;
    await page.waitForTimeout(2500);
    expect((await fake.connections(page)).length, 'nessuna riconnessione dopo il logout').toBe(total);
    expect(writesOf(fake)).toHaveLength(0);
  });

  test('i messaggi committati durante la disconnessione compaiono alla riconnessione, senza reinvii', async ({ page }) => {
    await openProject(page);
    await connected(page, fake);

    await fake.refuse(page, 503);
    await fake.drop(page);
    fake.addPm(P, 'Messaggio scritto da un\'altra sessione');
    await expect(userMessages(page).filter({ hasText: "scritto da un'altra sessione" })).toHaveCount(1, { timeout: 15_000 });
    await expect(userMessages(page)).toHaveCount(1);
    expect(writesOf(fake)).toHaveLength(0);
  });
});

test.describe('anteprima: conferma, salvataggio e Genera', () => {
  test('Genera non e\' mai offerto senza conferma e il server rifiuta comunque una conferma ritirata', async ({ page }) => {
    fake.seedAnalysis();
    await openProject(page);
    await expect(page.getByTestId('preview')).toBeVisible();
    await expect(page.getByTestId('validation-badge')).toBeVisible();
    await expect(page.getByTestId('generate')).toHaveCount(0);

    // Una modifica non salvata e una revisione nuova (non confermata) non offrono Genera.
    await goalField(page).fill('Obiettivo corretto dal PM');
    await expect(page.getByTestId('generate')).toHaveCount(0);
    await page.getByTestId('save').click();
    await expect(page.getByTestId('revision-info')).toContainText('modificata dal PM');
    await expect(page.getByTestId('generate')).toHaveCount(0);

    await page.getByTestId('confirm').click();
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    await expect(page.getByTestId('generate')).toBeEnabled();
    expect(fake.count('POST', /document-generations$/), 'confermare non genera').toBe(0);

    // Un'altra sessione ritira la conferma (nuova revisione non confermata): il server vieta la generazione.
    fake.project().revisions.at(-1)!['validationState'] = 'Unvalidated';
    await page.getByTestId('generate').click();
    await expect(page.getByTestId('generate-error')).toContainText('Nuova conferma richiesta');
    await expect(page.getByTestId('generation-state')).toHaveCount(0);
    await expect(page.getByTestId('draft')).toHaveCount(0);
    expect(fake.project().runs.filter((r) => r['kind'] === 'Document')).toHaveLength(0);
    expect(fake.callsTo('POST', /document-generations$/)).toHaveLength(1);
  });

  test('salvataggio PM fallito: il testo resta nel modulo, nessuna revisione dichiarata e il riprova riesce', async ({ page }) => {
    fake.seedAnalysis();
    fake.fail('savePreview', 500);
    await openProject(page);

    const corrected = 'Obiettivo corretto dal PM, da non perdere';
    await goalField(page).fill(corrected);
    await page.getByTestId('save').click();

    await expect(page.getByTestId('save-error')).toContainText('Il salvataggio non è riuscito');
    await expect(page.getByTestId('save-error')).toContainText('Le tue modifiche restano nel modulo');
    await expect(goalField(page)).toHaveValue(corrected);
    await expect(page.getByTestId('dirty-badge')).toBeVisible();
    await expect(page.getByTestId('revision-info')).toContainText("generata dall'AI");
    await expect(page.getByTestId('confirm')).toBeDisabled();
    await expect(page.getByTestId('generate')).toHaveCount(0);
    expect(fake.project().revisions).toHaveLength(1);

    await page.getByTestId('save').click();
    await expect(page.getByTestId('dirty-badge')).toHaveCount(0);
    await expect(page.getByTestId('revision-info')).toContainText('modificata dal PM');
    await expect(goalField(page)).toHaveValue(corrected);
    expect(fake.project().revisions).toHaveLength(2);
    expect(fake.callsTo('PUT', /\/preview$/)[1].ifMatch).toBe(fake.callsTo('PUT', /\/preview$/)[0].ifMatch);
  });

  test('salvataggio con esito incerto: il testo resta e il riprova non crea una seconda revisione ma viene riconciliato', async ({ page }) => {
    fake.seedAnalysis();
    fake.fail('savePreview', 'drop-after-save');
    await openProject(page);

    const corrected = 'Obiettivo salvato ma risposta persa';
    await goalField(page).fill(corrected);
    await page.getByTestId('save').click();
    await expect(page.getByTestId('save-error')).toContainText('non è riuscito');
    await expect(goalField(page)).toHaveValue(corrected);
    await expect(page.getByTestId('dirty-badge')).toBeVisible();

    // Il vecchio If-Match non crea un'altra revisione: 409 e riconciliazione, senza perdere il testo del PM.
    await page.getByTestId('save').click();
    await expect(page.getByTestId('conflict-banner')).toBeVisible();
    await expect(goalField(page)).toHaveValue(corrected);
    expect(fake.project().revisions).toHaveLength(2);
    await expect(page.getByTestId('generate')).toHaveCount(0);
  });

  test('conferma rifiutata: nessuna conferma dichiarata e nessuna generazione', async ({ page }) => {
    fake.seedAnalysis();
    fake.fail('confirm', 500);
    await openProject(page);

    await page.getByTestId('confirm').click();
    await expect(page.getByTestId('save-error')).toContainText('La conferma non è riuscita');
    await expect(page.getByTestId('confirmed-badge')).toBeHidden();
    await expect(page.getByTestId('validation-badge')).toBeVisible();
    await expect(page.getByTestId('generate')).toHaveCount(0);
    expect(fake.count('POST', /document-generations$/)).toBe(0);
  });
});

test.describe('la UI non inventa fasi o percentuali e non dichiara riuscito un errore', () => {
  test('stato del run: solo etichette osservate, nessuna percentuale, poi errore senza anteprima di successo', async ({ page }) => {
    fake.addPm(P, 'Voglio un portale clienti');
    await openProject(page);
    await connected(page, fake);

    await page.getByTestId('analyze').click();
    const state = page.getByTestId('run-state');
    await expect(state).toHaveText(/^\s*In coda\s*$/);

    // Senza lastObservedStage nessuna fase viene mostrata.
    await fake.send(page, 'processing.state', fake.setRun(P, 'r-1', { status: 'Running', attemptCount: 1 }));
    await expect(state).toHaveText(/^\s*Analisi in corso\s*$/);
    await fake.send(page, 'processing.state', fake.setRun(P, 'r-1', { lastObservedStage: 'Consolidating' }));
    await expect(state).toContainText('consolidamento');
    await fake.send(page, 'processing.state', fake.setRun(P, 'r-1', { attemptCount: 2 }));
    await expect(state).toContainText('tentativo 2 di 3');

    const body = async () => (await page.locator('body').innerText()).replace(/\s+/g, ' ');
    expect(await body()).not.toMatch(/\d\s*%/);
    await expect(page.getByRole('progressbar')).toHaveCount(0);
    await expect(page.locator('progress')).toHaveCount(0);

    await fake.send(
      page,
      'processing.state',
      fake.setRun(P, 'r-1', { status: 'Failed', completedAt: '2030-01-01T10:05:00Z', error: { code: 'invalid_agent_output', message: null } }),
    );
    await expect(page.getByTestId('run-error')).toContainText('Analisi non riuscita');
    await expect(page.getByTestId('run-error')).toContainText("non era un'analisi valida");
    await expect(page.getByTestId('run-state')).toHaveCount(0);
    await expect(page.getByTestId('preview')).toHaveCount(0);
    await expect(page.getByTestId('confirm')).toHaveCount(0);
    await expect(page.getByTestId('generate')).toHaveCount(0);
    await expect(page.getByTestId('draft')).toHaveCount(0);
    expect(await body()).not.toMatch(/\d\s*%/);
  });

  test('generazione fallita: errore esplicito, l\'ultima bozza non viene sostituita e nessun successo dichiarato', async ({ page }) => {
    fake.seedAnalysis();
    fake.project().revisions[0]['validationState'] = 'PmConfirmed';
    await openProject(page);
    await connected(page, fake);

    await page.getByTestId('generate').click();
    await expect(page.getByTestId('generation-state')).toBeVisible();
    await fake.send(
      page,
      'processing.state',
      fake.setRun(P, 'r-2', { status: 'Failed', completedAt: '2030-01-01T10:05:00Z', error: { code: 'deadline_exceeded', message: null } }),
    );
    await expect(page.getByTestId('generation-error')).toContainText('Generazione non riuscita');
    await expect(page.getByTestId('generation-error')).toContainText('Il tempo massimo per la generazione è scaduto');
    await expect(page.getByTestId('draft')).toHaveCount(0);
    await expect(page.getByTestId('draft-status')).toHaveCount(0);
    await expect(page.getByTestId('generation-state')).toHaveCount(0);
  });
});

test.describe('context_overflow non e\' mai un generico rifiuto del modello', () => {
  test('chat: spiegazione, valori misurati dai dati e azione praticabile', async ({ page }) => {
    const { turn } = fake.addPm(P, 'Messaggio molto lungo');
    fake.failTurn(P, turn['id'] as string, 'context_overflow', TOKENS);
    await openProject(page);

    const error = page.locator('.turn-error');
    await expect(error).toBeVisible();
    await expect(error).toContainText('supera il contesto gestibile dal modello');
    await expect(error).toContainText('31000 token');
    await expect(error).toContainText('24000 token');
    await expect(error).toContainText('messaggio più recente');
    await expect(error).toContainText('non verrà reinviato automaticamente');
    await expect(error).not.toContainText(/rifiutato la richiesta/);
    await expect(error).not.toContainText('%');
    await expect(page.locator('.saved-badge')).toHaveCount(1);
  });

  test('analisi: stessa spiegazione, nessuna anteprima di successo e Analizza ancora disponibile', async ({ page }) => {
    fake.addPm(P, 'Dialogo molto esteso');
    const run = fake.newRun(P, 'Analysis', 'k1');
    fake.setRun(P, run['id'] as string, { status: 'Failed', completedAt: '2030-01-01T10:05:00Z', error: { code: 'context_overflow', message: TOKENS } });
    await openProject(page);

    const error = page.getByTestId('run-error');
    await expect(error).toContainText('Analisi non riuscita');
    await expect(error).toContainText('supera il contesto gestibile dal modello');
    await expect(error).toContainText('31000 token');
    await expect(error).toContainText('24000 token');
    await expect(error).toContainText('messaggio più recente');
    await expect(error).not.toContainText(/rifiutato la richiesta/);
    await expect(error).not.toContainText('%');
    await expect(page.getByTestId('preview')).toHaveCount(0);
    await expect(page.getByTestId('analyze')).toBeEnabled();
  });

  test('senza valori nei dati non compaiono numeri né percentuali inventati', async ({ page }) => {
    fake.addPm(P, 'Dialogo molto esteso');
    const run = fake.newRun(P, 'Analysis', 'k1');
    fake.setRun(P, run['id'] as string, {
      status: 'Failed',
      completedAt: '2030-01-01T10:05:00Z',
      error: { code: 'context_overflow', message: 'Il modello interno ha rifiutato la richiesta.' },
    });
    await openProject(page);

    const error = page.getByTestId('run-error');
    await expect(error).toContainText('supera il contesto gestibile dal modello');
    await expect(error).toContainText('messaggio più recente');
    await expect(error).not.toContainText(/rifiutato la richiesta/);
    expect(((await error.innerText()) ?? '').replace('Analisi non riuscita.', '')).not.toMatch(/\d|%/);
  });
});
