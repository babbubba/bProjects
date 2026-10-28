import { execSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { SCREENSHOT_DIR, parseCount, readComposition, readMetricsRow, readPanel } from './support/context-usage';

// Metriche di inferenza e monitor del contesto su stack REALE (Angular + API + PostgreSQL + DGX reale).
// Ogni valore mostrato dalla UI e' confrontato con la risposta GET /messages, /context-usage, /processing-runs, /analyses e /draft
// dello stesso stack e, se disponibile, con la riga salvata in PostgreSQL (tabella InferenceCalls).
// Variabili: E2E_BASE_URL, E2E_USER, E2E_PASSWORD, E2E_MODEL_MODE=real (senza, la suite risulta ignorata, mai superata) e, facoltative,
//   E2E_DB_COMMAND            psql -At sul DB dello STACK DI PROVA (SQL da stdin) per il confronto con InferenceCalls;
//   E2E_APP_RESTART_COMMAND   riavvia il solo servizio app conservando il volume (es. docker compose ... restart app);
//   E2E_DGX_ENDPOINT / E2E_DGX_API_KEY / E2E_DGX_MODEL   chiamata diretta equivalente al DGX (solo per il confronto a tre vie);
//   E2E_API_KEY_NEEDLE        valore della chiave API da cercare (e non trovare) in DOM e risposte: mai stampato.
// Lo stack deve avere AI_COMPACTION_TRIGGER_TOKENS basso (es. 3000) perche' la compattazione si attivi con messaggi realistici.
const user = process.env['E2E_USER'];
const password = process.env['E2E_PASSWORD'];
const mode = process.env['E2E_MODEL_MODE'];
const dbCommand = process.env['E2E_DB_COMMAND'];
const restartCommand = process.env['E2E_APP_RESTART_COMMAND'];
const needle = process.env['E2E_API_KEY_NEEDLE'];

test.skip(!user || !password, 'E2E_USER ed E2E_PASSWORD non impostate: stack di prova non disponibile.');
test.skip(mode !== 'real', 'E2E_MODEL_MODE=real non impostata: serve uno stack con DGX Spark raggiungibile.');

type Json = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any
const BUDGET = 180_000;
const unique = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
const log = (label: string, value: unknown) => console.log(`[real] ${label}: ${typeof value === 'string' ? value : JSON.stringify(value)}`);

const PARAGRAPHS = [
  "Il portale clienti Aurora serve a gestire i ticket di assistenza: il cliente accede con email e password, apre un ticket indicando oggetto, descrizione e priorita', allega fino a cinque file da dieci megabyte ciascuno e segue lo stato della richiesta fino alla chiusura.",
  "Gli agenti dell'assistenza lavorano da una coda condivisa ordinata per priorita' e anzianita': possono prendere in carico un ticket, assegnarlo a un collega, aggiungere note interne invisibili al cliente e chiuderlo con una causale scelta da un elenco concordato con la direzione.",
  "Le notifiche arrivano solo via email: una al cliente a ogni cambio di stato, una all'agente quando riceve un'assegnazione. Le notifiche via SMS sono escluse dal primo rilascio e non devono essere progettate ora, nemmeno come predisposizione tecnica.",
  "Il portale non gestisce la fatturazione e non mostra importi: la direzione ha deciso che resta fuori perimetro e che sara' valutata in un progetto separato dopo il primo trimestre di esercizio, quando avremo dati reali sull'utilizzo.",
  "Non conosciamo ancora il numero di clienti che useranno il portale: potrebbero essere alcune centinaia oppure alcune migliaia, lo scopriremo dopo il primo trimestre. Per questo il dimensionamento resta un punto aperto da non inventare.",
  "I tempi di risposta attesi dagli agenti sono quattro ore lavorative per le richieste ad alta priorita' e due giorni lavorativi per le altre, ma e' un obiettivo interno del servizio e non un impegno contrattuale verso i clienti.",
  "L'integrazione con il gestionale esistente e' desiderata ma non confermata: se fosse possibile, il portale dovrebbe leggere l'anagrafica dei clienti dal gestionale invece di duplicarla. Va chiarito con l'ufficio informatico prima di decidere.",
  "L'accesso dei clienti deve essere sicuro e tracciato: ogni accesso e ogni modifica a un ticket vengono registrati con data, ora e utente. La conservazione dei log e' di dodici mesi, salvo diversa indicazione del responsabile della protezione dei dati.",
  "Il responsabile del progetto e' la signora Bianchi del reparto assistenza; il referente tecnico e' il signor Verdi dell'ufficio informatico. Le decisioni sul perimetro passano dalla direzione, che si riunisce una volta al mese.",
  "Il primo rilascio deve essere pronto entro la fine dell'anno, ma la data non e' vincolante se emergono problemi di sicurezza: la qualita' e la protezione dei dati dei clienti hanno la precedenza sulla scadenza.",
];

/** Messaggio PM lungo e realistico (cinque paragrafi, circa 400 token), diverso a ogni indice. */
const pmMessage = (i: number) =>
  `${[0, 1, 2, 3, 4].map((k) => PARAGRAPHS[(i * 3 + k) % PARAGRAPHS.length]).join(" ")} Rispondi in due frasi, senza fare domande.`;

async function login(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill(user!);
  await page.getByLabel('Password').fill(password!);
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible({ timeout: 20_000 });
}

async function openSidebar(page: Page) {
  if (page.viewportSize()!.width >= 992) return;
  const toggle = page.locator('.shell-toggle');
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

const projectIdOf = (page: Page) => /\/projects\/([0-9a-f-]{36})/.exec(page.url())![1];

class Api {
  private constructor(
    private readonly request: APIRequestContext,
    private readonly token: string,
  ) {}
  static async login(request: APIRequestContext): Promise<Api> {
    const response = await request.post('/api/auth/login', { data: { userName: user, password } });
    expect(response.ok(), 'login API').toBe(true);
    return new Api(request, (await response.json()).accessToken as string);
  }
  async get(path: string): Promise<Json> {
    const response = await this.request.get(path, { headers: { Authorization: `Bearer ${this.token}` } });
    expect(response.ok(), `GET ${path} → ${response.status()}`).toBe(true);
    return (await response.json()) as Json;
  }
  messages = async (p: string) => ((await this.get(`/api/projects/${p}/messages?limit=100`)).items as Json[]);
  usage = (p: string) => this.get(`/api/projects/${p}/context-usage`);
}

/** Esegue SQL sul DB dello stack di prova (psql -At) e restituisce lo stdout. */
const sql = (statement: string) => execSync(dbCommand!, { input: statement, encoding: 'utf8' }).trim();

const fmt = (n: number) => new Intl.NumberFormat('it-IT', { useGrouping: true }).format(n);
const value = (token: Json | undefined) => (token?.['value'] ?? null) as number | null;

/** La riga metriche mostrata deve coincidere con le metriche dell'API (CTX, Max output, Generati, troncamento). */
async function expectRowMatches(scope: ReturnType<Page['locator']>, metrics: Json | null, label: string) {
  const row = await readMetricsRow(scope);
  if (!metrics) {
    expect(row.unavailable, `${label}: storico senza telemetria`).toBe(true);
    return row;
  }
  expect(row.running, `${label}: non deve essere in corso`).toBe(false);
  expect(row.ctx, `${label}: CTX`).toBe(value(metrics['contextWindowTokens']));
  expect(row.max, `${label}: Max output`).toBe(metrics['maxOutputTokens'].value);
  expect(row.gen, `${label}: Generati`).toBe(value(metrics['completionTokens']));
  expect(row.truncated, `${label}: troncata`).toBe(metrics['finishReason'] === 'length');
  return row;
}

/** Il pannello deve coincidere con GET /context-usage (titolo, barra, legenda). */
async function expectPanelMatches(page: Page, usage: Json, label: string) {
  const last = usage['last'] as Json;
  await expect
    .poll(async () => (await readPanel(page)).prompt, { timeout: 20_000, message: `${label}: prompt del pannello` })
    .toBe(value(last['metrics'].promptTokens));
  const panel = await readPanel(page);
  expect(panel.ctx, `${label}: CTX`).toBe(value(last['metrics'].contextWindowTokens));
  expect(panel.percent, `${label}: percentuale`).toBe(last['usedPercent']);
  const legend = async (id: string) => parseCount((await page.locator(`[data-testid=usage-legend] [data-segment=${id}] .usage-count`).textContent()) ?? '');
  expect(await legend('prompt'), `${label}: legenda prompt`).toBe(value(last['metrics'].promptTokens));
  expect(await legend('reserved'), `${label}: output riservato`).toBe(last['outputReservedTokens']);
  expect(await legend('margin'), `${label}: margine`).toBe(last['safetyMarginTokens']);
  expect(await legend('remaining'), `${label}: residuo`).toBe(value(last['remainingTokens']));
  return panel;
}

async function expandPanel(page: Page) {
  const toggle = page.getByTestId('usage-toggle');
  if (await toggle.isVisible()) {
    if ((await toggle.getAttribute('aria-expanded')) === 'false') await toggle.click();
  }
  const composition = page.getByTestId('composition-toggle');
  if ((await composition.getAttribute('aria-expanded')) === 'false') await composition.click();
  await expect(page.getByTestId('composition')).toBeVisible();
}

/** Voci di composizione mostrate = voci dell'API; voci + quota non attribuita = totale. */
async function expectCompositionMatches(page: Page, usage: Json, label: string) {
  const composition = usage['composition'] as Json;
  const shown = await readComposition(page);
  let sum = 0;
  for (const item of composition['items'] as Json[]) {
    expect(shown[item['key']]?.tokens, `${label}: ${item['key']}`).toBe(value(item['tokens']));
    sum += value(item['tokens']) ?? 0;
  }
  expect(shown['unattributed'].tokens, `${label}: quota non attribuita`).toBe(value(composition['unattributedTokens']));
  expect(shown['total'].tokens, `${label}: totale`).toBe(value(composition['promptTokens']));
  expect(sum + (shown['unattributed'].tokens ?? 0), `${label}: voci + non attribuita = totale`).toBe(shown['total'].tokens);
}

type Session = { name: string; projectId: string; rows: Record<string, string> };
/** Progetto della chat lunga con compattazione e progetto breve per analisi e documento (la conversazione lunga supera la capacita' di output dell'analisi). */
const session: Session = { name: '', projectId: '', rows: {} };
const docSession: Session = { name: '', projectId: '', rows: {} };
const bodies: string[] = [];

test.describe.configure({ mode: 'serial' });

test.describe('sessione reale: chat, compattazione, analisi, documento, riapertura', () => {
  test.beforeEach(({ page }, info) => {
    test.skip(info.project.name !== 'desktop-1440', 'Le richieste AI reali sono eseguite una sola volta (desktop); il mobile e\' verificato con dati reali dalla prova di chat.');
    page.on('response', async (r) => {
      if (/\/api\//.test(r.url()) && !/\/events$/.test(r.url())) bodies.push(await r.text().catch(() => ''));
    });
  });

  test('chat: metriche sotto ogni risposta, monitor aggiornato, streaming provvisorio e compattazione reale', async ({ page, request, browser }) => {
    test.setTimeout(12 * 60_000);
    const api = await Api.login(request);
    session.name = `Contesto reale ${unique()}`;
    await login(page);
    await createProject(page, session.name);
    session.projectId = projectIdOf(page);
    const p = session.projectId;
    await expect(page.getByTestId('usage-empty')).toBeVisible(); // nessuna richiesta AI: nessun numero inventato
    await expect(page.getByTestId('usage-headline')).toHaveCount(0);
    await page.evaluate(() => ((window as unknown as { __alive: boolean }).__alive = true));

    let compacted: Json | null = null;
    const history: { exchange: number; prompt: number; completion: number; ctx: number; percent: number }[] = [];
    let failures = 0;
    for (let attempt = 0; attempt < 14 && !compacted; attempt++) {
      const i = history.length; // scambi riusciti finora
      await page.evaluate(() => {
        const w = window as unknown as { __provisional?: string };
        w.__provisional = undefined;
        new MutationObserver(() => {
          const row = document.querySelector('article[data-role="assistant-streaming"] [data-testid=inference-metrics]');
          if (row && row.querySelector('[data-testid=metrics-running]') && !w.__provisional) w.__provisional = (row.textContent ?? '').replace(/\s+/g, ' ');
        }).observe(document.body, { childList: true, subtree: true, characterData: true });
      });
      const started = Date.now();
      await page.getByLabel("Messaggio per l'assistente").fill(pmMessage(attempt));
      await page.getByRole('button', { name: 'Invia', exact: true }).click();
      // Esito del turno: risposta oppure errore (la sintesi della compattazione puo' superare il proprio limite di output).
      await expect
        .poll(
          async () =>
            (await page.locator('article[data-role="assistant"]').count()) === i + 1
              ? 'ok'
              : (await page.locator('.turn-error').count()) > failures
                ? 'failed'
                : 'wait',
          { timeout: BUDGET, intervals: [400] },
        )
        .not.toBe('wait');
      if ((await page.locator('article[data-role="assistant"]').count()) !== i + 1) {
        failures++;
        const userMessages = (await api.messages(p)).filter((m) => m['role'] === 'user');
        const turn = await api.get('/api/projects/' + p + '/chat-turns/' + userMessages.at(-1)!['chatTurnId']);
        log('turno fallito (osservato)', { tentativo: attempt + 1, scambiRiusciti: i, status: turn['status'], codice: turn['error']?.code, messaggio: turn['error']?.message });
        expect(turn['status']).toBe('Failed');
        expect(turn['error']?.code, 'unico guasto atteso: compaction_failed').toBe('compaction_failed');
        const text = ((await page.locator('.turn-error').last().textContent()) ?? '').replace(/\s+/g, ' ');
        expect(text).toContain('Non è stato possibile condensare la conversazione');
        expect(text).not.toMatch(/[0-9]{3,}/);
        // Nessuna risposta e nessuna metrica attribuite al turno fallito; il monitor resta sull'ultimo scambio riuscito.
        expect(await page.locator('article[data-role="assistant"]').count()).toBe(i);
        const usageNow = await api.usage(p);
        const known = (await api.messages(p)).filter((m) => m['role'] === 'assistant');
        expect(usageNow['last'].refId).toBe(known.at(-1)!['id']);
        await expectPanelMatches(page, usageNow, 'dopo turno con compattazione fallita');
        await page.locator('.turn-error').last().screenshot({ path: SCREENSHOT_DIR + '/real-turn-error-compaction-failed-desktop-1440.png' });
        continue;
      }
      log(`scambio ${i + 1} durata`, `${Date.now() - started} ms`);

      // Riga provvisoria osservata durante lo streaming: configurazione nota e «generazione in corso…», mai i token generati.
      const provisional = await page.evaluate(() => (window as unknown as { __provisional?: string }).__provisional);
      expect(provisional, `scambio ${i + 1}: riga provvisoria non osservata`).toBeTruthy();
      expect(provisional).toContain('generazione in corso');
      expect(provisional).toMatch(/CTX 32\.768/);
      expect(provisional).not.toContain('Generati');
      await expect(page.locator('article[data-role="assistant-streaming"]')).toHaveCount(0);
      await expect(page.locator('article[data-role="assistant"] [data-testid=metrics-running]')).toHaveCount(0);
      if (i === 0) log('riga provvisoria (esempio)', provisional);

      // Riga definitiva = GET /messages.
      const messages = await api.messages(p);
      const assistants = messages.filter((m) => m['role'] === 'assistant');
      expect(assistants).toHaveLength(i + 1);
      const metrics = assistants[i]['metrics'] as Json;
      expect(metrics, `scambio ${i + 1}: metriche assenti nella risposta dell'API`).toBeTruthy();
      expect(metrics['state']).toBe('completed');
      expect(metrics['promptTokens'].source).toBe('measured');
      expect(metrics['completionTokens'].source).toBe('measured');
      expect(metrics['finishReason']).toBe('stop');
      const row = await expectRowMatches(page.locator('article[data-role="assistant"]').nth(i), metrics, `scambio ${i + 1}`);
      expect(await page.locator('article[data-role="user"] app-inference-metrics').count()).toBe(0);

      // Pannello = GET /context-usage, aggiornato senza ricaricare la pagina.
      const usage = await api.usage(p);
      expect(usage['last'].refId).toBe(assistants[i]['id']);
      const panel = await expectPanelMatches(page, usage, `scambio ${i + 1}`);
      history.push({
        exchange: i + 1,
        prompt: value(metrics['promptTokens'])!,
        completion: value(metrics['completionTokens'])!,
        ctx: value(metrics['contextWindowTokens'])!,
        percent: panel.percent!,
      });

      // Confronto con PostgreSQL (InferenceCalls) per la chiamata finale del turno.
      if (dbCommand) {
        const [dbPrompt, dbCompletion, dbCtx, dbMax, dbFinish] = sql(
          `select "PromptTokens","CompletionTokens","ContextWindowTokens","MaxOutputTokensRequested","FinishReason" from "InferenceCalls" where "ChatTurnId"='${assistants[i]['chatTurnId']}' and "IsFinal"`,
        ).split('|');
        expect(Number(dbPrompt), `scambio ${i + 1}: PromptTokens in PostgreSQL`).toBe(value(metrics['promptTokens']));
        expect(Number(dbCompletion), `scambio ${i + 1}: CompletionTokens in PostgreSQL`).toBe(value(metrics['completionTokens']));
        expect(Number(dbCtx), `scambio ${i + 1}: ContextWindowTokens in PostgreSQL`).toBe(value(metrics['contextWindowTokens']));
        expect(Number(dbMax), `scambio ${i + 1}: MaxOutputTokensRequested in PostgreSQL`).toBe(metrics['maxOutputTokens'].value);
        expect(dbFinish).toBe(metrics['finishReason']);
        if (i === 0 || usage['compaction']) {
          log(`confronto a tre vie, scambio ${i + 1}`, {
            postgres: { prompt: Number(dbPrompt), completion: Number(dbCompletion), ctx: Number(dbCtx), max: Number(dbMax), finish: dbFinish },
            api: { prompt: value(metrics['promptTokens']), completion: value(metrics['completionTokens']), ctx: value(metrics['contextWindowTokens']), max: metrics['maxOutputTokens'].value },
            ui: { rigaMetriche: row.visible, pannello: panel.headline },
          });
        }
      }

      if (i === 0 && process.env['E2E_DGX_ENDPOINT']) {
        // Chiamata diretta equivalente (system + primo messaggio, max_tokens=1): il prompt_tokens del DGX deve coincidere.
        const system = readFileSync('../../backend/src/bProjects.App/AI/Prompts/conversation.md', 'utf8');
        const direct = await fetch(`${process.env['E2E_DGX_ENDPOINT']}chat/completions`, {
          method: 'POST',
          headers: { 'content-type': 'application/json', authorization: `Bearer ${process.env['E2E_DGX_API_KEY']}` },
          body: JSON.stringify({
            model: process.env['E2E_DGX_MODEL'],
            messages: [{ role: 'system', content: system }, { role: 'user', content: pmMessage(0) }],
            max_tokens: 1,
            temperature: 0.2,
            chat_template_kwargs: { enable_thinking: false },
          }),
        });
        const dgx = ((await direct.json()) as Json)['usage'] as Json;
        log('DGX diretto (system + primo messaggio)', { prompt_tokens: dgx['prompt_tokens'], api_prompt: value(metrics['promptTokens']) });
        expect(dgx['prompt_tokens'], 'usage del DGX = prompt misurato dall\'API').toBe(value(metrics['promptTokens']));
      }

      // Ha il server compattato? (solo la prima volta che succede)
      compacted = (usage['compaction'] as Json | null) ?? null;
      if (compacted) {
        log('compattazione (API)', usage['compaction']);
        log('composizione (API)', usage['composition']);
        await expandPanel(page);
        const text = ((await page.getByTestId('usage-compaction').textContent()) ?? '').replace(/\s+/g, ' ');
        expect(text).toContain(`${compacted['messagesSummarized']} messaggi riassunti`);
        for (const token of [compacted['tokensBefore'], compacted['tokensAfter'], compacted['tokensSaved']]) {
          if (value(token as Json) !== null) expect(text, 'valore di compattazione nel pannello').toContain(fmt(value(token as Json)!));
        }
        await expectCompositionMatches(page, usage, 'dopo la compattazione');
        const keys = (usage['composition'].items as Json[]).map((x) => x['key']);
        expect(keys, 'riassunto derivato nella composizione').toContain('derived_summary');
        // La chiamata finale dichiara la compattazione e le chiamate di sintesi.
        expect(metrics['calls']).toBeGreaterThanOrEqual(2);
        expect(metrics['compaction'], 'metriche della compattazione sul messaggio').toBeTruthy();
        if (dbCommand) {
          const rows = sql(
            `select "Phase","PromptTokens","CompletionTokens","FinishReason","IsFinal" from "InferenceCalls" where "ChatTurnId"='${assistants[i]['chatTurnId']}' order by "StartedAt"`,
          ).split('\n');
          log('InferenceCalls del turno con compattazione', rows);
          const summary = rows.map((r) => r.split('|')).find((r) => r[0] === 'Compaction');
          expect(summary, 'chiamata di sintesi salvata').toBeTruthy();
          expect(Number(summary![1])).toBe(value(metrics['compaction'].summaryPromptTokens));
          expect(Number(summary![2])).toBe(value(metrics['compaction'].summaryCompletionTokens));
        }
        await page.screenshot({ path: `${SCREENSHOT_DIR}/real-compaction-desktop-1440.png` });
      }
    }
    expect(compacted, 'la compattazione reale deve attivarsi (AI_COMPACTION_TRIGGER_TOKENS basso)').toBeTruthy();
    expect(await page.evaluate(() => (window as unknown as { __alive?: boolean }).__alive), 'il monitor si e\' aggiornato senza ricaricare la pagina').toBe(true);
    log('andamento di prompt/ctx', history);
    expect(history.map((h) => h.prompt)[1], 'il prompt cresce con la conversazione').toBeGreaterThan(history[0].prompt);

    // Pannello sticky reale: dopo lo scroll di centinaia di px resta nel viewport e non copre la chat.
    // La colonna sticky e' l'host: il contenuto interno scorre dentro il suo max-height.
    const panel = page.locator('app-context-usage-panel');
    for (const y of [400, 900, 1600]) {
      await page.evaluate((top) => window.scrollTo(0, top), y);
      await page.waitForTimeout(100);
      const box = (await panel.boundingBox())!;
      expect(box.y).toBeGreaterThanOrEqual(-1);
      expect(box.y + box.height).toBeLessThanOrEqual(900 + 1);
    }
    expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(300);
    await page.getByLabel("Messaggio per l'assistente").scrollIntoViewIfNeeded();
    await page.getByLabel("Messaggio per l'assistente").click();
    await page.screenshot({ path: `${SCREENSHOT_DIR}/real-sticky-desktop-1440.png` });
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: `${SCREENSHOT_DIR}/real-chat-desktop-1440.png` });
    await page.locator('article[data-role="assistant"]').last().screenshot({ path: `${SCREENSHOT_DIR}/real-metrics-row-desktop-1440.png` });
    await page.getByTestId('context-usage-panel').screenshot({ path: `${SCREENSHOT_DIR}/real-panel-desktop-1440.png` });

    // Riapertura: reload e nuova sessione mostrano le stesse metriche storiche dell'API.
    const reloaded = page.waitForResponse((r) => /\/messages(\?|$)/.test(r.url()) && r.request().method() === 'GET');
    await page.reload();
    const fromApi = ((await (await reloaded).json()).items as Json[]).filter((m) => m['role'] === 'assistant');
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(fromApi.length);
    for (const [i, m] of fromApi.entries()) await expectRowMatches(page.locator('article[data-role="assistant"]').nth(i), m['metrics'], `dopo reload, risposta ${i + 1}`);
    await expectPanelMatches(page, await api.usage(p), 'dopo reload');
    session.rows['chat'] = (await page.locator('article[data-role="assistant"]').last().locator('[data-testid=inference-metrics]').first().textContent())!.replace(/\s+/g, ' ');
    session.rows['panel'] = (await readPanel(page)).headline;

    // Mobile (390px) con gli stessi dati reali: nessun overflow, pannello compatto espandibile da tastiera, valori uguali all'API.
    const mobile = await browser.newContext({ baseURL: test.info().project.use.baseURL, viewport: { width: 390, height: 844 } });
    try {
      const m = await mobile.newPage();
      await login(m);
      await reopen(m, session.name);
      await expectPanelMatches(m, await api.usage(p), 'mobile');
      const overflow = await m.evaluate(() => [document.documentElement.scrollWidth, document.documentElement.clientWidth]);
      expect(overflow[0]).toBeLessThanOrEqual(overflow[1]);
      const toggle = m.getByTestId('usage-toggle');
      await toggle.focus();
      await m.keyboard.press('Enter');
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      await expect(m.getByTestId('usage-details')).toBeVisible();
      await m.screenshot({ path: `${SCREENSHOT_DIR}/real-mobile-390.png` });
      await m.locator('article[data-role="assistant"]').last().screenshot({ path: `${SCREENSHOT_DIR}/real-metrics-row-mobile-390.png` });
      const after = await m.evaluate(() => [document.documentElement.scrollWidth, document.documentElement.clientWidth]);
      expect(after[0]).toBeLessThanOrEqual(after[1]);
    } finally {
      await mobile.close();
    }
  });

  test('analisi sulla conversazione lunga: esito reale osservato, mai metriche attribuite a un run fallito', async ({ page, request }) => {
    test.setTimeout(8 * 60_000);
    const api = await Api.login(request);
    await login(page);
    await reopen(page, session.name);
    const p = session.projectId;
    const usageBefore = await api.usage(p);

    await page.getByTestId('analyze').click();
    const outcome = await Promise.race([
      page.getByTestId('preview').waitFor({ state: 'visible', timeout: BUDGET }).then(() => 'completed' as const),
      page.getByTestId('run-error').waitFor({ state: 'visible', timeout: BUDGET }).then(() => 'failed' as const),
    ]);
    const project = await api.get('/api/projects/' + p);
    const run = await api.get('/api/projects/' + p + '/processing-runs/' + project['latestAnalysisRunId']);
    log('analisi lunga', {
      outcome,
      status: run['status'],
      errorCode: run['error']?.code,
      errorMessage: run['error']?.message,
      metrics: run['metrics'] && { finish: run['metrics'].finishReason, completion: value(run['metrics'].completionTokens), prompt: value(run['metrics'].promptTokens) },
    });
    if (outcome === 'failed') {
      // Un run fallito non ha metriche attribuite, ne' nella UI ne' nel monitor; il messaggio e' comprensibile e senza dettagli interni.
      expect(run['status']).toBe('Failed');
      await expect(page.locator('app-analysis-preview [data-testid=run-metrics]')).toHaveCount(0);
      const text = ((await page.getByTestId('run-error').textContent()) ?? '').replace(/\s+/g, ' ');
      expect(text).toContain('Analisi non riuscita.');
      expect(text).not.toMatch(/Exception|AnalysisContent|\$\./);
      const usage = await api.usage(p);
      expect(usage['last'].kind, 'il monitor non attribuisce la chiamata fallita').toBe('chat');
      expect(usage['last'].refId).toBe(usageBefore['last'].refId);
      await expectPanelMatches(page, usage, 'dopo analisi fallita');
    } else {
      expect(run['status']).toBe('Completed');
      await expectRowMatches(page.locator('app-analysis-preview [data-testid=run-metrics]'), run['metrics'], 'analisi lunga (run)');
    }
    await page.screenshot({ path: SCREENSHOT_DIR + '/real-analysis-long-' + outcome + '-desktop-1440.png' });
  });

  test('analisi e documento su conversazione breve: metriche del run, poi dopo il reload da AnalysisView e DraftView', async ({ page, request }) => {
    test.setTimeout(10 * 60_000);
    const api = await Api.login(request);
    await login(page);
    // Cambio progetto (navigazione nella stessa pagina): il nuovo progetto non eredita numeri, righe o messaggi del precedente.
    await reopen(page, session.name);
    const longUsage = await api.usage(session.projectId);
    await expectPanelMatches(page, longUsage, 'progetto lungo prima del cambio');
    const longPrompt = (await readPanel(page)).prompt;
    docSession.name = 'Analisi reale ' + unique();
    await createProject(page, docSession.name);
    docSession.projectId = projectIdOf(page);
    const p = docSession.projectId;
    await expect(page.getByTestId('usage-empty')).toBeVisible();
    await expect(page.getByTestId('usage-headline')).toHaveCount(0);
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(0);
    expect(await page.getByTestId('context-usage-panel').textContent()).not.toContain(fmt(longPrompt!));
    await openSidebar(page);
    await page.getByRole('navigation', { name: 'Elenco progetti' }).getByRole('link', { name: session.name }).first().click();
    await expect(page.getByRole('heading', { level: 1, name: session.name })).toBeVisible();
    await expectPanelMatches(page, longUsage, 'progetto lungo dopo il ritorno');
    await openSidebar(page);
    await page.getByRole('navigation', { name: 'Elenco progetti' }).getByRole('link', { name: docSession.name }).first().click();
    await expect(page.getByRole('heading', { level: 1, name: docSession.name })).toBeVisible();
    await expect(page.getByTestId('usage-empty')).toBeVisible();
    for (const [i, index] of [0, 1].entries()) {
      await page.getByLabel("Messaggio per l'assistente").fill(PARAGRAPHS[index] + ' Rispondi in una frase.');
      await page.getByRole('button', { name: 'Invia', exact: true }).click();
      await expect(page.locator('article[data-role="assistant"]')).toHaveCount(i + 1, { timeout: BUDGET });
    }
    docSession.rows['chat'] = ((await page.locator('article[data-role="assistant"]').last().locator('[data-testid=inference-metrics]').first().textContent()) ?? '').replace(/\s+/g, ' ');

    await page.getByTestId('analyze').click();
    await expect(page.getByTestId('run-error')).toHaveCount(0);
    await expect(page.getByTestId('preview')).toBeVisible({ timeout: BUDGET });
    await expect(page.getByTestId('run-error')).toHaveCount(0);
    const project = await api.get(`/api/projects/${p}`);
    const run = await api.get(`/api/projects/${p}/processing-runs/${project['latestAnalysisRunId']}`);
    expect(run['status']).toBe('Completed');
    const analysisRow = page.locator('app-analysis-preview [data-testid=run-metrics]');
    await expectRowMatches(analysisRow, run['metrics'], 'analisi (run)');
    const usage = await api.usage(p);
    expect(usage['last'].kind).toBe('analysis');
    await expectPanelMatches(page, usage, 'dopo analisi');
    log('analisi', { prompt: value(run['metrics'].promptTokens), completion: value(run['metrics'].completionTokens), finish: run['metrics'].finishReason, calls: run['metrics'].calls });
    if (dbCommand) {
      const db = sql(`select "PromptTokens","CompletionTokens","FinishReason","Phase" from "InferenceCalls" where "ProcessingRunId"='${run['id']}' and "IsFinal"`).split('|');
      expect(Number(db[0])).toBe(value(run['metrics'].promptTokens));
      expect(Number(db[1])).toBe(value(run['metrics'].completionTokens));
      expect(db[2]).toBe(run['metrics'].finishReason);
    }

    // Dopo il reload nessun run e' noto alla UI: la riga viene da AnalysisView.metrics, gia' letta al ripristino.
    const analysisResponse = page.waitForResponse((r) => /\/analyses\/[0-9a-f-]{36}$/.test(r.url()) && r.request().method() === 'GET');
    await page.reload();
    const view = (await (await analysisResponse).json()) as Json;
    await expect(page.getByTestId('preview')).toBeVisible();
    expect(view['metrics'], 'AnalysisView.metrics').toBeTruthy();
    await expectRowMatches(page.locator('app-analysis-preview [data-testid=run-metrics]'), view['metrics'], 'analisi dopo reload');
    expect(value(view['metrics'].completionTokens)).toBe(value(run['metrics'].completionTokens));

    // Conferma e generazione del Markdown.
    await page.getByTestId('confirm').click();
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    await page.getByTestId('generate').click();
    await expect(page.getByTestId('generation-error')).toHaveCount(0);
    await expect(page.getByTestId('draft')).toBeVisible({ timeout: BUDGET });
    await expect(page.getByTestId('generation-error')).toHaveCount(0);
    const draft = await api.get(`/api/projects/${p}/draft`);
    const docRun = await api.get(`/api/projects/${p}/processing-runs/${draft['processingRunId']}`);
    expect(docRun['kind']).toBe('Document');
    await expectRowMatches(page.locator('app-document-view [data-testid=run-metrics]'), docRun['metrics'], 'documento (run)');
    expect(value(draft['metrics'].completionTokens)).toBe(value(docRun['metrics'].completionTokens));
    log('documento', { prompt: value(docRun['metrics'].promptTokens), completion: value(docRun['metrics'].completionTokens), finish: docRun['metrics'].finishReason });
    expect((await api.usage(p))['last'].kind).toBe('document');

    const draftResponse = page.waitForResponse((r) => /\/draft$/.test(r.url()) && r.request().method() === 'GET');
    await page.reload();
    const draftAfter = (await (await draftResponse).json()) as Json;
    await expect(page.getByTestId('draft')).toBeVisible();
    await expectRowMatches(page.locator('app-document-view [data-testid=run-metrics]'), draftAfter['metrics'], 'documento dopo reload');
    await page.getByTestId('context-usage-panel').screenshot({ path: `${SCREENSHOT_DIR}/real-panel-after-document-desktop-1440.png` });

    docSession.rows['analysis'] = ((await page.locator('app-analysis-preview [data-testid=run-metrics]').textContent()) ?? '').replace(/\s+/g, ' ').trim();
    docSession.rows['document'] = ((await page.locator('app-document-view [data-testid=run-metrics]').textContent()) ?? '').replace(/\s+/g, ' ').trim();
    docSession.rows['panel'] = (await readPanel(page)).headline;
  });

  test('riavvio del servizio app: le metriche restano identiche', async ({ page, request }) => {
    test.skip(!restartCommand, 'E2E_APP_RESTART_COMMAND non impostata.');
    test.setTimeout(5 * 60_000);
    const api = await Api.login(request);
    const before = await api.usage(docSession.projectId);
    const longBefore = await api.usage(session.projectId);
    execSync(restartCommand!, { stdio: 'inherit' });
    await expect
      .poll(async () => (await page.request.get('/health').catch(() => null))?.status() ?? 0, { timeout: 120_000 })
      .toBe(200);
    await login(page);
    await reopen(page, docSession.name);
    await expect(page.getByTestId('draft')).toBeVisible();
    const apiAfter = await Api.login(request);
    const after = await apiAfter.usage(docSession.projectId);
    expect(after['last'].metrics, 'ultima richiesta invariata dopo il riavvio').toEqual(before['last'].metrics);
    expect(after['composition']).toEqual(before['composition']);
    expect(after['compaction']).toEqual(before['compaction']);
    // La compattazione reale del progetto lungo e' persistita: stesso evento, stessa composizione dopo il riavvio.
    const longAfter = await apiAfter.usage(session.projectId);
    expect(longBefore['compaction'], 'compattazione presente').toBeTruthy();
    expect(longAfter['compaction']).toEqual(longBefore['compaction']);
    expect(longAfter['composition']).toEqual(longBefore['composition']);
    expect(longAfter['history']).toEqual(longBefore['history']);
    expect(((await page.locator('article[data-role="assistant"]').last().locator('[data-testid=inference-metrics]').first().textContent()) ?? '').replace(/\s+/g, ' ')).toBe(docSession.rows['chat']);
    expect(((await page.locator('app-analysis-preview [data-testid=run-metrics]').textContent()) ?? '').replace(/\s+/g, ' ').trim()).toBe(docSession.rows['analysis']);
    expect(((await page.locator('app-document-view [data-testid=run-metrics]').textContent()) ?? '').replace(/\s+/g, ' ').trim()).toBe(docSession.rows['document']);
    expect((await readPanel(page)).headline).toBe(docSession.rows['panel']);
  });

  test('riconnessione SSE reale: il servizio app si riavvia con la pagina aperta, il flusso si riapre da solo e il monitor segue il nuovo scambio', async ({ page, request }) => {
    test.skip(!restartCommand, 'E2E_APP_RESTART_COMMAND non impostata.');
    test.setTimeout(8 * 60_000);
    const api = await Api.login(request);
    await login(page);
    await reopen(page, docSession.name);
    const p = docSession.projectId;
    let eventsRequests = 0;
    page.on('request', (r) => {
      if (/\/api\/projects\/[0-9a-f-]{36}\/events$/.test(r.url())) eventsRequests++;
    });
    await page.evaluate(() => ((window as unknown as { __alive: boolean }).__alive = true));
    await page.reload();
    await expect(page.getByTestId('usage-headline')).toBeVisible();
    await expect.poll(() => eventsRequests, { timeout: 15_000 }).toBeGreaterThanOrEqual(1);
    const opened = eventsRequests;
    await page.evaluate(() => ((window as unknown as { __alive: boolean }).__alive = true));
    const before = (await api.messages(p)).filter((m) => m['role'] === 'assistant').length;

    // Il server riparte mentre la pagina resta aperta: lo stream cade e deve riaprirsi senza ricaricare.
    execSync(restartCommand!, { stdio: 'inherit' });
    await expect
      .poll(async () => (await page.request.get('/health').catch(() => null))?.status() ?? 0, { timeout: 120_000 })
      .toBe(200);
    await expect.poll(() => eventsRequests, { timeout: 90_000, message: 'nuova richiesta /events dopo la caduta' }).toBeGreaterThan(opened);
    expect(await page.evaluate(() => (window as unknown as { __alive?: boolean }).__alive), 'nessun reload').toBe(true);

    // Dopo la riconnessione il nuovo scambio arriva in streaming, con le sue metriche, e il monitor si aggiorna da solo.
    const apiAfter = await Api.login(request);
    await page.getByLabel("Messaggio per l'assistente").fill(PARAGRAPHS[2] + ' Conferma in una frase.');
    await page.getByRole('button', { name: 'Invia', exact: true }).click();
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(before + 1, { timeout: BUDGET });
    const assistants = (await apiAfter.messages(p)).filter((m) => m['role'] === 'assistant');
    expect(assistants).toHaveLength(before + 1);
    await expectRowMatches(page.locator('article[data-role="assistant"]').last(), assistants.at(-1)!['metrics'], 'dopo la riconnessione');
    const usage = await apiAfter.usage(p);
    expect(usage['last'].refId).toBe(assistants.at(-1)!['id']);
    await expectPanelMatches(page, usage, 'dopo la riconnessione');
    log('riconnessione', { richiesteEvents: eventsRequests, prompt: value(usage['last'].metrics.promptTokens) });
  });

  test('riservatezza: né ragionamento né parametri del template né chiave API in DOM e risposte dell\'API', async ({ page }) => {
    await login(page);
    await reopen(page, docSession.name);
    await expect(page.getByTestId('draft')).toBeVisible();
    await expandPanel(page);
    const html = await page.content();
    const corpus = bodies.join('\n');
    expect(bodies.length, 'risposte API raccolte').toBeGreaterThan(20);
    for (const text of ['reasoning_content', 'reasoningContent', 'chat_template_kwargs', '<think>']) {
      expect(html.includes(text), `DOM contiene «${text}»`).toBe(false);
      expect(corpus.includes(text), `risposte API contengono «${text}»`).toBe(false);
    }
    if (needle) {
      expect(html.includes(needle), 'il DOM contiene la chiave API').toBe(false);
      expect(corpus.includes(needle), 'le risposte API contengono la chiave API').toBe(false);
    }
    // Il campo reasoning dell'API e' solo la configurazione (richiesto/effettivo), mai contenuto.
    const reasoning = [...corpus.matchAll(/"reasoning":\{[^}]*\}/g)].map((m) => m[0]);
    expect(reasoning.length).toBeGreaterThan(0);
    for (const r of reasoning) expect(r).toMatch(/^"reasoning":\{"requested":(true|false),"effective":"(on|off|unknown)"\}$/);
  });
});
