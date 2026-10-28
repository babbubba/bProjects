import { expect, test, type Page } from '@playwright/test';
import { FakeStack } from './support/fake-stack';
import { SCREENSHOT_DIR, metricsOf, parseCount, readComposition, readMetricsRow, readPanel, tv, usageOf } from './support/context-usage';

// Metriche di inferenza e monitor del contesto (desktop-1440 e mobile-390).
// Stack: API finta (support/fake-stack.ts) con flusso SSE controllabile: nessuna dipendenza da DGX o PostgreSQL.
// I valori che la UI mostra sono confrontati con le risposte GET /messages e GET /context-usage intercettate dal browser.
// Le stesse verifiche su stack reale con DGX sono in context-monitor-real.spec.ts (E2E_MODEL_MODE=real).

type Json = Record<string, unknown>;
const P = 'p-1';
let fake: FakeStack;

test.beforeEach(async ({ page }) => {
  fake = new FakeStack();
  await fake.install(page);
});

const isDesktop = (page: Page) => page.viewportSize()!.width >= 1200;

async function login(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill('pm');
  await page.getByLabel('Password').fill('non-un-segreto');
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible();
}

async function openProject(page: Page, name = 'Aurora', id = P) {
  await login(page);
  await page.goto(`/projects/${id}`);
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
}

async function connected(page: Page, project = P) {
  await expect.poll(async () => (await fake.connections(page)).filter((c) => c.open && c.project === project).length).toBe(1);
}

async function openSidebar(page: Page) {
  if (isDesktop(page) || page.viewportSize()!.width >= 992) return;
  const toggle = page.locator('.shell-toggle');
  if ((await toggle.getAttribute('aria-expanded')) === 'false') await toggle.click();
}

const paragraph = (i: number) =>
  `Messaggio ${i}: il portale clienti Aurora deve consentire l'apertura dei ticket, l'allegato di documenti, la consultazione dello stato e la ricerca per numero; ` +
  `gli agenti dell'assistenza vedono la coda, assegnano e chiudono i ticket, mentre i clienti ricevono una notifica via email a ogni cambio di stato. `.repeat(2);

/** Scambi PM/assistente con metriche crescenti; il monitor riflette l'ultima richiesta. */
function seed(exchanges: number, id = P): { lastMetrics: Json; lastMessageId: string } {
  let lastMetrics = metricsOf();
  let lastMessageId = '';
  for (let i = 1; i <= exchanges; i++) {
    const { turn } = fake.addPm(id, paragraph(i));
    lastMetrics = metricsOf({ prompt: 700 + i * 410, completion: 90 + i * 23 });
    const done = fake.completeTurn(id, turn['id'] as string, `Risposta ${i}: ho annotato gli obiettivi, il perimetro e le esclusioni indicate.`, lastMetrics);
    lastMessageId = done.message['id'] as string;
  }
  fake.project(id).usage = usageOf({ metrics: lastMetrics, refId: lastMessageId, messageCount: exchanges * 2 });
  return { lastMetrics, lastMessageId };
}

const fig = (token: unknown) => ((token as Json)['value'] as number | null) ?? null;

test.describe('metriche sotto le risposte AI', () => {
  test('ogni risposta mostra CTX, Max output e Generati uguali ai dati di GET /messages; l\'utente non ha metriche', async ({ page }) => {
    seed(3);
    // Un messaggio storico senza telemetria e uno con risposta troncata dal limite di output.
    const { turn } = fake.addPm(P, 'Domanda storica');
    fake.completeTurn(P, turn['id'] as string, 'Risposta storica', null);
    const t2 = fake.addPm(P, 'Domanda lunga');
    fake.completeTurn(P, t2.turn['id'] as string, 'Risposta tagliata', metricsOf({ completion: 4096, finishReason: 'length', maxConfirmed: true }));

    const messagesResponse = page.waitForResponse((r) => /\/api\/projects\/p-1\/messages(\?|$)/.test(r.url()) && r.request().method() === 'GET');
    await openProject(page);
    const api = (await (await messagesResponse).json()).items as Json[];
    const assistants = api.filter((m) => m['role'] === 'assistant');
    expect(assistants).toHaveLength(5);

    const articles = page.locator('article[data-role="assistant"]');
    await expect(articles).toHaveCount(5);
    await expect(page.locator('article[data-role="user"] app-inference-metrics')).toHaveCount(0);
    for (const [i, message] of assistants.entries()) {
      const row = await readMetricsRow(articles.nth(i));
      const m = message['metrics'] as Json | null;
      if (!m) {
        expect(row.unavailable, `risposta ${i + 1}: storico senza telemetria`).toBe(true);
        expect(row.visible).toBe('Metriche non disponibili');
        continue;
      }
      expect(row.ctx, `risposta ${i + 1}: CTX`).toBe(fig(m['contextWindowTokens']));
      expect(row.max, `risposta ${i + 1}: Max output`).toBe((m['maxOutputTokens'] as Json)['value']);
      expect(row.gen, `risposta ${i + 1}: Generati`).toBe(fig(m['completionTokens']));
      expect(row.truncated, `risposta ${i + 1}: troncata`).toBe(m['finishReason'] === 'length');
    }
    // Un limite non confermato e' dichiarato come tale; quello confermato no.
    await expect(articles.nth(0).locator('[data-testid=metric-max]')).toContainText('(limite richiesto)');
    await expect(articles.nth(4).locator('[data-testid=metric-max]')).not.toContainText('limite richiesto');
    await expect(articles.nth(4).locator('[data-testid=metrics-truncated]')).toHaveText('Risposta troncata dal limite di output');
  });

  test('dopo il reload le metriche storiche sono identiche ai dati dell\'API', async ({ page }) => {
    seed(3);
    await openProject(page);
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(3);
    const before: string[] = [];
    for (let i = 0; i < 3; i++) before.push((await readMetricsRow(page.locator('article[data-role="assistant"]').nth(i))).visible);

    const messagesResponse = page.waitForResponse((r) => /\/api\/projects\/p-1\/messages(\?|$)/.test(r.url()) && r.request().method() === 'GET');
    await page.reload();
    const api = ((await (await messagesResponse).json()).items as Json[]).filter((m) => m['role'] === 'assistant');
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(3);
    for (const [i, message] of api.entries()) {
      const row = await readMetricsRow(page.locator('article[data-role="assistant"]').nth(i));
      const m = message['metrics'] as Json;
      expect(row.visible).toBe(before[i]);
      expect(row.gen).toBe(fig(m['completionTokens']));
      expect(row.ctx).toBe(fig(m['contextWindowTokens']));
    }
  });

  test('streaming: riga provvisoria «generazione in corso…» senza Generati, poi la riga definitiva nello stesso punto', async ({ page }) => {
    seed(1);
    const { turn } = fake.addPm(P, 'Seconda domanda');
    fake.setTurn(P, turn['id'] as string, { status: 'Running', attemptCount: 1 });
    await openProject(page);
    await connected(page);
    await expect(page.getByTestId('usage-headline')).toBeVisible();
    await fake.send(page, 'chat.delta', { chatTurnId: turn['id'], attemptCount: 1, deltaIndex: 0, text: 'Sto scrivendo' });

    const streaming = page.locator('article[data-role="assistant-streaming"]');
    await expect(streaming).toHaveCount(1);
    const provisional = await readMetricsRow(streaming);
    expect(provisional.running).toBe(true);
    expect(provisional.ctx).toBe(32768); // capacita' nota dal monitor
    expect(provisional.max).toBe(4096);
    expect(provisional.gen).toBeNull(); // nessun conteggio finche' non e' misurato
    await expect(streaming.locator('[data-testid=metric-max]')).toContainText('(limite richiesto)');
    expect(provisional.visible).toContain('generazione in corso…');

    const final = metricsOf({ prompt: 2100, completion: 456 });
    const done = fake.completeTurn(P, turn['id'] as string, 'Risposta definitiva', final);
    fake.project().usage = usageOf({ metrics: final, refId: done.message['id'] as string });
    await fake.send(page, 'chat.state', done.turn);
    await expect(streaming).toHaveCount(0);
    const definitive = await readMetricsRow(page.locator('article[data-role="assistant"]').last());
    expect(definitive.running).toBe(false);
    expect(definitive.gen).toBe(456);
    expect(definitive.visible).not.toContain('generazione in corso');
    await expect(page.locator('article[data-role="assistant"] [data-testid=metrics-running]')).toHaveCount(0);
  });
});

test.describe('monitor del contesto', () => {
  test('il pannello mostra i valori di GET /context-usage e si aggiorna con i nuovi messaggi senza refresh', async ({ page }) => {
    seed(1);
    const usageResponse = page.waitForResponse((r) => /\/context-usage$/.test(r.url()));
    await openProject(page);
    await connected(page);
    const first = (await (await usageResponse).json()) as Json;
    const last1 = first['last'] as Json;
    let panel = await readPanel(page);
    expect(panel.prompt).toBe(fig((last1['metrics'] as Json)['promptTokens']));
    expect(panel.ctx).toBe(32768);
    expect(panel.percent).toBe(last1['usedPercent']);

    await page.evaluate(() => ((window as unknown as { __alive: boolean }).__alive = true));
    const getsBefore = fake.count('GET', /\/context-usage$/);

    // Nuovo scambio dalla UI: il turno parte e termina; il pannello segue senza ricaricare la pagina.
    await page.getByLabel("Messaggio per l'assistente").fill('Nuova richiesta');
    await page.getByRole('button', { name: 'Invia', exact: true }).click();
    await expect(page.locator('article[data-role="user"]')).toHaveCount(2);
    const turn = fake.project().turns.at(-1)!;
    fake.setTurn(P, turn['id'] as string, { status: 'Running', attemptCount: 1 });
    await fake.send(page, 'chat.state', fake.turn(P, turn['id'] as string));
    const metrics = metricsOf({ prompt: 9100, completion: 612 });
    const done = fake.completeTurn(P, turn['id'] as string, 'Risposta nuova', metrics);
    fake.project().usage = usageOf({ metrics, refId: done.message['id'] as string, messageCount: 4 });
    await fake.send(page, 'chat.state', done.turn);

    await expect.poll(async () => (await readPanel(page)).prompt, { timeout: 10_000 }).toBe(9100);
    panel = await readPanel(page);
    expect(panel.percent).toBe(27.8);
    expect(panel.headline).toBe('Contesto: 9.100 / 32.768 token — 27,8%');
    expect(fake.count('GET', /\/context-usage$/)).toBeGreaterThan(getsBefore);
    expect(await page.evaluate(() => (window as unknown as { __alive?: boolean }).__alive)).toBe(true);
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(2);
  });

  test('ruoli e attributi ARIA: regione etichettata, barra con valori, aggiornamenti annunciati con garbo', async ({ page }) => {
    seed(2);
    await openProject(page);
    const region = page.getByRole('complementary', { name: 'Monitor del contesto AI' });
    await expect(region).toBeVisible();
    const bar = region.getByRole('progressbar', { name: 'Occupazione del contesto' });
    const panel = await readPanel(page);
    await expect(bar).toHaveAttribute('aria-valuemin', '0');
    await expect(bar).toHaveAttribute('aria-valuemax', '100');
    expect(Number(await bar.getAttribute('aria-valuenow'))).toBeCloseTo(panel.percent!, 0);
    expect(await bar.getAttribute('aria-valuetext')).toContain('token');
    await expect(region.locator('.usage-live')).toHaveAttribute('aria-live', 'polite');
    await expect(region.getByTestId('usage-notes')).toHaveAttribute('role', 'status');
    await expect(page.getByRole('group', { name: 'Metriche della richiesta AI' }).first()).toBeVisible();
    // Il testo per screen reader e' pieno e completo, quello visivo e' abbreviato (aria-hidden).
    await expect(page.locator('article[data-role="assistant"]').first().locator('[data-testid=metric-gen] .visually-hidden')).toContainText('Token generati:');
  });

  test('compattazione: il pannello mostra l\'ultimo evento e il riassunto derivato nella composizione, aggiornati senza refresh', async ({ page }) => {
    const { lastMetrics, lastMessageId } = seed(2);
    await openProject(page);
    await connected(page);
    if (!isDesktop(page)) await page.getByTestId('usage-toggle').click();
    await expect(page.getByTestId('usage-compaction-none')).toBeVisible();

    // Compattazione avvenuta nell'ultima richiesta: prima 4.200 → dopo 1.700, risparmiati 2.500; il riassunto entra nel prompt.
    const metrics = { ...lastMetrics, calls: 2, compaction: { summaryCalls: 1, summaryPromptTokens: tv(3900), summaryCompletionTokens: tv(480) } };
    const compaction = {
      at: '2030-01-01T10:04:00Z', tokensBefore: tv(4200), tokensAfter: tv(1700, 'calculated'), tokensSaved: tv(2500, 'calculated'), messagesSummarized: 6,
    };
    const items = [
      { key: 'system_instructions', label: 'Istruzioni di sistema', tokens: tv(406), messageCount: null },
      { key: 'derived_summary', label: 'Riassunto derivato', tokens: tv(520), messageCount: 6 },
      { key: 'user_messages', label: 'Messaggi del PM', tokens: tv(300), messageCount: 1 },
    ];
    fake.project().usage = usageOf({ metrics, refId: lastMessageId, compaction, items, messageCount: 12 });
    const { turn } = fake.addPm(P, 'Altro messaggio');
    await fake.send(page, 'chat.state', { ...fake.turn(P, turn['id'] as string), status: 'Running' });

    await expect(page.getByTestId('usage-compaction')).toBeVisible({ timeout: 10_000 });
    const text = ((await page.getByTestId('usage-compaction').textContent()) ?? '').replace(/\s+/g, ' ');
    expect(text).toContain('6 messaggi riassunti');
    for (const n of ['4.200', '1.700', '2.500']) expect(text).toContain(n);

    await page.getByTestId('composition-toggle').click();
    const rows = await readComposition(page);
    expect(rows['derived_summary']).toEqual({ tokens: 520, nature: 'misurato' });
    const unattributed = rows['unattributed'];
    expect(unattributed.nature).toBe('calcolato');
    // Coerenza interna: voci + quota non attribuita = totale (che e' il prompt della richiesta).
    const sum = Object.entries(rows).filter(([k]) => k !== 'total' && k !== 'unattributed').reduce((s, [, v]) => s + (v.tokens ?? 0), 0);
    expect(sum + (unattributed.tokens ?? 0)).toBe(rows['total'].tokens);
    expect(rows['total'].tokens).toBe((await readPanel(page)).prompt);
  });

  test('cambio progetto: nessuna contaminazione, nemmeno da una risposta in ritardo del progetto precedente', async ({ page }) => {
    seed(2);
    fake.addProject('p-2', 'Borealis');
    const p2 = seed(1, 'p-2');
    fake.project('p-2').usage = usageOf({ metrics: metricsOf({ prompt: 5000, completion: 77 }), refId: p2.lastMessageId });
    fake.addProject('p-3', 'Cassiopea');

    await openProject(page);
    await connected(page);
    expect((await readPanel(page)).prompt).toBe(1520);
    const aurora = (await page.locator('article[data-role="assistant"]').count());
    expect(aurora).toBe(2);

    // Una richiesta del monitor di Aurora e' in volo (lenta) quando si passa a Borealis.
    fake.project(P).usageDelayMs = 1_500;
    await fake.send(page, 'chat.state', { id: 't-x', status: 'Running' });
    await openSidebar(page);
    await page.getByRole('navigation', { name: 'Elenco progetti' }).getByRole('link', { name: /Borealis/ }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Borealis' })).toBeVisible();
    await expect.poll(async () => (await readPanel(page)).prompt).toBe(5000);
    await page.waitForTimeout(2_000); // la risposta tardiva di Aurora e' gia' arrivata
    const panel = await readPanel(page);
    expect(panel.prompt).toBe(5000);
    expect(panel.headline).not.toContain('1.520');
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(1);
    await expect(page.locator('article[data-role="assistant"] .msg-text')).toHaveText(/Risposta 1/);
    const row = await readMetricsRow(page.locator('article[data-role="assistant"]').first());
    expect(row.gen).toBe(113); // 90 + 1 * 23: i dati di Borealis, non quelli di Aurora

    // Un progetto senza richieste AI non eredita i numeri del precedente.
    await openSidebar(page);
    await page.getByRole('navigation', { name: 'Elenco progetti' }).getByRole('link', { name: /Cassiopea/ }).click();
    await expect(page.getByRole('heading', { level: 1, name: 'Cassiopea' })).toBeVisible();
    await expect(page.getByTestId('usage-empty')).toBeVisible();
    await expect(page.getByTestId('usage-headline')).toHaveCount(0);
    await expect(page.getByTestId('context-usage-panel')).not.toContainText('5.000');
  });

  test('riconnessione SSE: dopo la caduta dello stream il pannello si riconcilia con GET /context-usage', async ({ page }) => {
    seed(1);
    await openProject(page);
    await connected(page);
    const before = (await readPanel(page)).prompt;
    expect(before).toBe(1110);

    // Lo stream cade (e la prima riconnessione e' rifiutata); intanto il server completa una nuova richiesta.
    await fake.refuse(page, 503);
    await fake.drop(page);
    const metrics = metricsOf({ prompt: 7300, completion: 250 });
    const { turn } = fake.addPm(P, 'Mentre eri disconnesso');
    const done = fake.completeTurn(P, turn['id'] as string, 'Risposta persa dallo stream', metrics);
    fake.project().usage = usageOf({ metrics, refId: done.message['id'] as string, messageCount: 4 });
    const gets = fake.count('GET', /\/context-usage$/);

    await expect.poll(async () => (await readPanel(page)).prompt, { timeout: 30_000 }).toBe(7300);
    expect(fake.count('GET', /\/context-usage$/)).toBeGreaterThan(gets);
    await expect.poll(async () => (await fake.connections(page)).filter((c) => c.open).length).toBe(1);
    // Nessun comando emesso dalla riconnessione.
    expect(fake.calls.filter((c) => c.method !== 'GET' && !c.path.startsWith('/api/auth'))).toHaveLength(0);
  });

  test('errore del monitor: nessun numero precedente resta mostrato e la chat continua a funzionare', async ({ page }) => {
    seed(1);
    await openProject(page);
    await connected(page);
    expect((await readPanel(page)).prompt).toBe(1110);

    fake.project().usageStatus = 503;
    await fake.send(page, 'chat.state', { id: 't-x', status: 'Running' });
    await expect(page.getByTestId('usage-error')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('usage-headline')).toHaveCount(0);
    await expect(page.getByTestId('context-usage-panel')).not.toContainText('1.110');
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(1);
    await expect(page.getByLabel("Messaggio per l'assistente")).toBeEnabled();
  });
});

test.describe('pannello sticky e layout', () => {
  test('desktop: il pannello resta nel viewport durante lo scroll di una conversazione lunga e non copre la chat', async ({ page }) => {
    test.skip(!isDesktop(page), 'Colonna sticky solo da 1200px.');
    seed(24);
    await openProject(page);
    await connected(page);
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(24);
    await expect(page.getByTestId('usage-headline')).toBeVisible();

    const panel = page.getByTestId('context-usage-panel');
    const host = page.locator('app-context-usage-panel');
    expect(await host.evaluate((el) => getComputedStyle(el).position)).toBe('sticky');
    const doc = await page.evaluate(() => ({ height: document.documentElement.scrollHeight, vh: window.innerHeight }));
    expect(doc.height, 'la conversazione deve richiedere lo scroll').toBeGreaterThan(doc.vh * 3);

    const chatRight = async () => (await page.locator('app-chat').boundingBox())!.x + (await page.locator('app-chat').boundingBox())!.width;
    for (const y of [0, 300, 700, 1500, 3000, doc.height]) {
      await page.evaluate((top) => window.scrollTo(0, top), y);
      await page.waitForTimeout(80);
      const scrolled = await page.evaluate(() => window.scrollY);
      const box = (await panel.boundingBox())!;
      const vh = doc.vh;
      expect(box.y, `scrollY=${scrolled}: pannello sopra il bordo alto`).toBeGreaterThanOrEqual(-1);
      expect(box.y + box.height, `scrollY=${scrolled}: pannello oltre il bordo basso`).toBeLessThanOrEqual(vh + 1);
      expect(box.height).toBeGreaterThan(100);
      // Non copre la chat: colonna distinta, a destra.
      expect(box.x, `scrollY=${scrolled}: il pannello invade la colonna della chat`).toBeGreaterThanOrEqual((await chatRight()) - 1);
    }
    expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(500);

    // Non blocca i click: in fondo alla conversazione il campo del messaggio e' raggiungibile e riceve il testo.
    const textarea = page.getByLabel("Messaggio per l'assistente");
    await textarea.scrollIntoViewIfNeeded();
    const tb = (await textarea.boundingBox())!;
    const topmost = await page.evaluate(([x, y]) => document.elementFromPoint(x, y)?.id ?? document.elementFromPoint(x, y)?.tagName, [tb.x + tb.width / 2, tb.y + tb.height / 2]);
    expect(topmost).toBe('chat-text');
    await textarea.click();
    await page.keyboard.type('prova di click sotto il pannello');
    await expect(textarea).toHaveValue('prova di click sotto il pannello');
    // Un click su un messaggio al centro della pagina non e' intercettato dal pannello.
    await page.evaluate(() => window.scrollTo(0, 1500));
    const msg = page.locator('article[data-role="user"]').nth(12);
    await msg.scrollIntoViewIfNeeded();
    await msg.click({ trial: true });
    await page.screenshot({ path: `${SCREENSHOT_DIR}/sticky-scroll-desktop-1440.png` });
  });

  test('mobile: pannello compatto nel flusso, senza posizione fissa; dettagli espandibili da tastiera', async ({ page }) => {
    test.skip(isDesktop(page), 'Solo sotto 1200px.');
    seed(6);
    await openProject(page);
    await connected(page);
    const host = page.locator('app-context-usage-panel');
    expect(await host.evaluate((el) => getComputedStyle(el).position)).not.toMatch(/fixed|sticky/);
    const toggle = page.getByTestId('usage-toggle');
    const details = page.getByTestId('usage-details');
    await expect(toggle).toBeVisible();
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(toggle).toHaveAttribute('aria-controls', 'context-usage-details');
    await expect(details).toBeHidden();
    await page.screenshot({ path: `${SCREENSHOT_DIR}/panel-compact-mobile-390.png` });

    // Tastiera: Tab fino al pulsante (nessuna trappola), Invio apre, Spazio chiude.
    let reached = false;
    await page.getByTestId('context-usage-panel').getByRole('heading', { name: 'Contesto AI' }).evaluate((el) => (el.setAttribute('tabindex', '-1'), (el as HTMLElement).focus()));
    for (let i = 0; i < 5 && !reached; i++) {
      await page.keyboard.press('Tab');
      reached = await toggle.evaluate((el) => el === document.activeElement);
    }
    expect(reached, 'il pulsante dei dettagli deve essere raggiungibile con Tab').toBe(true);
    await page.keyboard.press('Enter');
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await expect(details).toBeVisible();
    await expect(toggle).toBeFocused();
    await page.keyboard.press('Space');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(details).toBeHidden();

    // Aperto: composizione espandibile con la tastiera, nessun overflow orizzontale.
    await page.keyboard.press('Enter');
    await expect(details).toBeVisible();
    const composition = page.getByTestId('composition-toggle');
    await composition.focus();
    await page.keyboard.press('Enter');
    await expect(composition).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('composition')).toBeVisible();
    await page.getByTestId('context-usage-panel').scrollIntoViewIfNeeded();
    await page.screenshot({ path: `${SCREENSHOT_DIR}/panel-expanded-mobile-390.png` });
  });

  test('nessun overflow orizzontale, con pannello chiuso e aperto, e screenshot del layout', async ({ page }) => {
    seed(4);
    fake.project().usage = usageOf({
      metrics: metricsOf({ prompt: 21256, completion: 1347 }),
      refId: 'm-8',
      messageCount: 8,
      compaction: { at: '2030-01-01T10:04:00Z', tokensBefore: tv(24800), tokensAfter: tv(1700, 'calculated'), tokensSaved: tv(23100, 'calculated'), messagesSummarized: 40 },
      items: [
        { key: 'system_instructions', label: 'Istruzioni di sistema', tokens: tv(406), messageCount: null },
        { key: 'derived_summary', label: 'Riassunto derivato', tokens: tv(1500, 'estimated'), messageCount: 40 },
        { key: 'user_messages', label: 'Messaggi del PM', tokens: tv(9800), messageCount: 12 },
        { key: 'assistant_messages', label: "Risposte dell'assistente", tokens: tv(8000), messageCount: 11 },
      ],
    });
    await openProject(page);
    await connected(page);
    const noOverflow = async (label: string) => {
      const m = await page.evaluate(() => ({
        doc: [document.documentElement.scrollWidth, document.documentElement.clientWidth],
        panel: (() => {
          const el = document.querySelector('[data-testid=context-usage-panel]') as HTMLElement;
          return [el.scrollWidth, el.clientWidth];
        })(),
        chat: (() => {
          const el = document.querySelector('app-chat') as HTMLElement;
          return [el.scrollWidth, el.clientWidth];
        })(),
      }));
      expect(m.doc[0], `${label}: pagina`).toBeLessThanOrEqual(m.doc[1]);
      expect(m.panel[0], `${label}: pannello`).toBeLessThanOrEqual(m.panel[1]);
      expect(m.chat[0], `${label}: chat`).toBeLessThanOrEqual(m.chat[1]);
    };
    await noOverflow('chiuso');
    const name = isDesktop(page) ? 'desktop-1440' : 'mobile-390';
    await page.screenshot({ path: `${SCREENSHOT_DIR}/layout-${name}.png`, fullPage: false });
    if (!isDesktop(page)) await page.getByTestId('usage-toggle').click();
    await page.getByTestId('composition-toggle').click();
    await expect(page.getByTestId('composition')).toBeVisible();
    await noOverflow('aperto');
    await page.getByTestId('context-usage-panel').screenshot({ path: `${SCREENSHOT_DIR}/panel-${name}.png` });
    await page.locator('article[data-role="assistant"]').last().screenshot({ path: `${SCREENSHOT_DIR}/metrics-row-${name}.png` });
    // La composizione di questo scenario usa la stima dichiarata e la quota non attribuita calcolata.
    const rows = await readComposition(page);
    expect(rows['derived_summary'].nature).toBe('stima');
    expect(rows['unattributed']).toEqual({ tokens: 21256 - (406 + 1500 + 9800 + 8000), nature: 'calcolato' });
    expect(parseCount((await page.getByTestId('usage-compaction').textContent()) ?? '')).not.toBeNull();
  });
});

test.describe('riservatezza', () => {
  test('la UI non espone ragionamento, parametri del template né chiavi, anche se presenti nei dati', async ({ page }) => {
    seed(2);
    // Campi che l'API non dovrebbe mai trasportare: se comparissero nei dati, la UI non deve renderli.
    const poisoned = (fake.project().messages.at(-1)!['metrics'] as Json);
    Object.assign(poisoned, { reasoningContent: 'PENSIERO-SEGRETO', reasoning_content: 'PENSIERO-SEGRETO', chat_template_kwargs: { enable_thinking: true } });
    await openProject(page);
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(2);
    const html = await page.content();
    for (const needle of ['PENSIERO-SEGRETO', 'chat_template_kwargs', 'reasoning_content', 'reasoningContent', 'enable_thinking']) {
      expect(html.includes(needle), `il DOM contiene «${needle}»`).toBe(false);
    }
  });
});
