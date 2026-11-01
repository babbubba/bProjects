import { createServer, request as httpRequest, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import { expect, test, type Page, type Request } from '@playwright/test';

// Caduta reale della rete con lo stream SSE aperto, su stack reale (Angular + API + PostgreSQL; il DGX non serve:
// il messaggio PM e' salvato anche se il turno poi fallisce).
//
// Il browser raggiunge lo stack attraverso un proxy HTTP locale che inoltra tutto senza modificarlo. "La rete cade"
// = context.setOffline(true) (Network.emulateNetworkConditions offline: le nuove richieste del browser falliscono con
// ERR_INTERNET_DISCONNECTED) + il proxy smette di recapitare i byte dello stream gia' aperto SENZA chiuderlo (connessione
// half-open, come un wifi perso o un laptop in sospensione). Il solo setOffline non basta: Chromium lascia scorrere gli
// stream SSE gia' stabiliti, quindi non riprodurrebbe la connessione muta. "La rete torna" = entrambi ripristinati.
//
// Prova che il watchdog d'inattivita' del client (45 s = 3 x keep-alive del server, 15 s) aborta la vecchia connessione
// anche a rete ancora assente, che al ritorno della rete ne apre UNA sola e che lo stato si riconcilia con i GET senza
// duplicati ne' comandi automatici. Variabili: E2E_BASE_URL (stack), E2E_USER, E2E_PASSWORD. Senza stack: ignorata.
const user = process.env['E2E_USER'];
const password = process.env['E2E_PASSWORD'];
const stack = process.env['E2E_BASE_URL'];

test.skip(!user || !password || !stack, 'E2E_BASE_URL, E2E_USER ed E2E_PASSWORD non impostate: stack di prova non disponibile.');

const unique = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
const isEvents = (r: Request) => /^\/api\/projects\/[^/]+\/events$/.test(new URL(r.url()).pathname);
const isCommand = (r: Request) => r.method() !== 'GET' && /^\/api\/projects\//.test(new URL(r.url()).pathname);

/** Proxy inverso locale: conta le connessioni SSE fisiche e puo' "far sparire" i byte dello stream senza chiuderlo. */
class NetworkProxy {
  private readonly server: Server;
  private blackholed = false;
  /** Connessioni SSE aperte dal browser (lato proxy) e totale aperte da inizio prova. */
  sseOpen = 0;
  sseTotal = 0;

  private constructor(target: URL) {
    this.server = createServer((req, res) => {
      const upstream = httpRequest(
        { host: target.hostname, port: target.port, path: req.url, method: req.method, headers: { ...req.headers, host: target.host } },
        (response) => {
          const sse = String(response.headers['content-type'] ?? '').startsWith('text/event-stream');
          res.writeHead(response.statusCode ?? 502, response.headers);
          if (sse) {
            this.sseOpen++;
            this.sseTotal++;
            res.on('close', () => {
              this.sseOpen--;
              upstream.destroy();
            });
            res.flushHeaders();
          }
          // Rete caduta: i byte dello stream sono persi (non bufferizzati), la connessione resta aperta e muta.
          response.on('data', (chunk) => (sse && this.blackholed ? undefined : res.write(chunk)));
          response.on('end', () => res.end());
        },
      );
      upstream.on('error', () => res.destroy());
      req.on('aborted', () => upstream.destroy());
      req.pipe(upstream);
    });
  }

  static async start(target: URL): Promise<NetworkProxy> {
    const proxy = new NetworkProxy(target);
    await new Promise<void>((resolve) => proxy.server.listen(0, '127.0.0.1', resolve));
    return proxy;
  }

  get url(): string {
    return `http://127.0.0.1:${(this.server.address() as AddressInfo).port}`;
  }

  blackhole(on: boolean): void {
    this.blackholed = on;
  }

  async stop(): Promise<void> {
    this.server.closeAllConnections();
    await new Promise((resolve) => this.server.close(resolve));
  }
}

async function login(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill(user!);
  await page.getByLabel('Password').fill(password!);
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible();
}

test('rete caduta con SSE aperto: il client abortisce la connessione muta, ne riapre una sola e riconcilia lo stato', async ({ browser, request }, info) => {
  test.skip(info.project.name !== 'desktop-1440', 'Prova eseguita una sola volta (desktop).');
  test.setTimeout(180_000);

  const proxy = await NetworkProxy.start(new URL(stack!));
  const context = await browser.newContext({ baseURL: proxy.url, viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  try {
    const name = `Rete ${unique()}`;
    const commands: string[] = [];
    page.on('request', (r) => {
      if (!isEvents(r) && isCommand(r)) commands.push(`${r.method()} ${new URL(r.url()).pathname}`);
    });

    await login(page);
    await page.getByRole('button', { name: 'Nuovo progetto' }).click();
    await page.getByLabel('Nome', { exact: true }).fill(name);
    await page.getByRole('button', { name: 'Crea progetto' }).click();
    await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
    const projectId = /\/projects\/([0-9a-f-]{36})/.exec(page.url())![1];
    const commandsBefore = commands.length;

    // Una sola connessione fisica per il progetto (chat, monitor, anteprima e documento la condividono).
    await expect.poll(() => proxy.sseOpen).toBe(1);
    expect(proxy.sseTotal).toBe(1);

    // --- La rete cade: lo stream aperto resta muto e le nuove richieste del browser falliscono.
    proxy.blackhole(true);
    await context.setOffline(true);
    const droppedAt = Date.now();

    // Mentre il browser e' offline un altro client (API diretta allo stack) salva un messaggio PM.
    const loginResponse = await request.post('/api/auth/login', { data: { userName: user, password } });
    expect(loginResponse.ok(), 'login API').toBe(true);
    const token = (await loginResponse.json()).accessToken as string;
    const text = `Scritto mentre il browser era offline ${unique()}`;
    const posted = await request.post(`/api/projects/${projectId}/messages`, {
      headers: { Authorization: `Bearer ${token}` },
      data: { clientMessageId: crypto.randomUUID(), text },
    });
    expect(posted.status(), 'POST messaggio da altro client').toBe(201);
    const userMessages = page.locator('article[data-role="user"]');

    // A rete ancora assente il watchdog aborta la connessione muta: non prima della soglia, non molto dopo.
    await expect.poll(() => proxy.sseOpen, { timeout: 70_000, message: 'vecchia connessione SSE abortita dal client' }).toBe(0);
    const abortedAfterMs = Date.now() - droppedAt;
    expect(abortedAfterMs).toBeGreaterThanOrEqual(40_000);
    expect(proxy.sseTotal, "nessuna riconnessione possibile (e nessun polling) finche' la rete manca").toBe(1);
    await expect(userMessages).toHaveCount(0); // lo stato locale e' ancora quello precedente

    // --- La rete torna: il client riapre lo stream (evento online / backoff) e riconcilia con i GET.
    proxy.blackhole(false);
    await context.setOffline(false);
    await expect(userMessages.filter({ hasText: text })).toHaveCount(1, { timeout: 30_000 });
    console.log(
      `[sse-network-drop] connessione abortita dopo ${Math.round(abortedAfterMs / 1000)} s, stato riconciliato ${Math.round((Date.now() - droppedAt) / 1000)} s dopo la caduta`,
    );

    // Esattamente una connessione fisica aperta (la vecchia e' chiusa, la nuova e' l'unica).
    expect(proxy.sseTotal).toBe(2);
    await expect.poll(() => proxy.sseOpen, { timeout: 10_000 }).toBe(1);

    // Nessun duplicato (messaggio) e nessun comando emesso dal browser nel frattempo.
    await expect(userMessages).toHaveCount(1);
    await expect(userMessages.first().locator('.saved-badge')).toHaveText('Salvato');
    expect(commands.slice(commandsBefore)).toEqual([]);

    // Logout: la connessione si chiude e non ne viene aperta un'altra.
    await page.getByRole('button', { name: 'Esci' }).click();
    await expect(page).toHaveURL(/\/login$/);
    await expect.poll(() => proxy.sseOpen, { timeout: 10_000 }).toBe(0);
    await page.waitForTimeout(3_000);
    expect(proxy.sseTotal).toBe(2);
    expect(proxy.sseOpen).toBe(0);
  } finally {
    await context.close();
    await proxy.stop();
  }
});
