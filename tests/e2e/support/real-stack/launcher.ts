import { execFileSync, spawn, type ChildProcess } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { closeSync, existsSync, mkdirSync, openSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { createServer, request as httpRequest, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import { tmpdir } from 'node:os';
import { extname, join, normalize, resolve } from 'node:path';
import { FakeLlm, type Fault } from '../fake-llm';

// HARNESS UNICO dello stack reale con modello FINTO (T092 meta reale e T103). Avvia, e al termine ferma con pulizia garantita:
//   - un database PostgreSQL ISOLATO nel container di test esistente (mai i dati reali), migrato con `migrate` e con un account PM creato da `bootstrap-accounts`;
//   - il modello finto (support/fake-llm.ts) su 127.0.0.1;
//   - il backend REALE (bProjects.App.dll, nessun hook di test) configurato solo con le normali variabili Ai__*, ConnectionStrings__*, Parser__*, Documents__*;
//   - un reverse proxy 127.0.0.1 che serve l'Angular compilato e inoltra /api al backend (stesso origin, come nginx in infra/web.conf);
//   - un'API di controllo (127.0.0.1) usata dalle prove per riavviare il backend, interrogare il DB e leggere/programmare il modello finto.
// Il parser e il PostgreSQL sono i container di test gia in esecuzione (vedi variabili sotto). Nessun DGX: le variabili Ai__*, AI_*, BPROJECTS_* dell'ambiente
// NON sono mai ereditate dal backend. Credenziali solo dall'ambiente di test locale (docker inspect), mai stampate ne scritte nel repository.

const repo = resolve(__dirname, '..', '..', '..', '..');
const logsDir = join(repo, 'diagnostics-local', 'real-stack');

export interface StackPorts {
  proxy: number;
  control: number;
  backend: number;
  llm: number;
}

export const PORTS: StackPorts = {
  proxy: Number(process.env['E2E_STACK_PROXY_PORT'] ?? 4290),
  control: Number(process.env['E2E_STACK_CONTROL_PORT'] ?? 4291),
  backend: Number(process.env['E2E_STACK_BACKEND_PORT'] ?? 5291),
  llm: Number(process.env['E2E_STACK_LLM_PORT'] ?? 4292),
};

const PG_CONTAINER = process.env['E2E_STACK_PG_CONTAINER'] ?? 'bprojects-002-test-pg2';
const PG_HOST_PORT = process.env['E2E_STACK_PG_PORT'] ?? '15432';
const PARSER_CONTAINER = process.env['E2E_STACK_PARSER_CONTAINER'] ?? 'bprojects-t055-parser';
const PARSER_PORT = process.env['E2E_STACK_PARSER_PORT'] ?? '58300';
const MODEL_ID = 'fake-llm-model';
// Finestra piccola e DICHIARATA: i documenti del corpus di prova la superano, quindi il percorso "oltre contesto" e quello reale del backend.
const CONTEXT_WINDOW = Number(process.env['E2E_STACK_CONTEXT_WINDOW'] ?? 32768);

const dockerEnv = (container: string, name: string): string => {
  const out = execFileSync('docker', ['inspect', container, '--format', '{{range .Config.Env}}{{println .}}{{end}}'], { encoding: 'utf8' });
  const line = out.split(/\r?\n/).find((l) => l.startsWith(name + '='));
  if (!line) throw new Error(`variabile ${name} assente nel container ${container}`);
  return line.slice(name.length + 1);
};

/** SQL via psql nel container (stdin, nessun segreto sulla riga di comando); restituisce le righe come oggetti JSON. */
export function sql<T = Record<string, unknown>>(database: string, query: string): T[] {
  const wrapped = `SELECT coalesce(json_agg(t), '[]'::json) FROM (${query.replace(/;\s*$/, '')}) t`;
  const out = execFileSync('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', 'postgres', '-d', database, '-v', 'ON_ERROR_STOP=1', '-tA'], { input: wrapped, encoding: 'utf8' });
  return JSON.parse(out.trim()) as T[];
}

/** Esegue un comando SQL (senza risultato) sul database isolato dell'esecuzione. */
export function execSql(database: string, statement: string) {
  execFileSync('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', 'postgres', '-d', database, '-v', 'ON_ERROR_STOP=1'], { input: 'SET client_min_messages = warning;\n' + statement, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] });
}

function psqlAdmin(statement: string) {
  execFileSync('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1'], { input: statement, encoding: 'utf8' });
}

/** Rimuove dall'ambiente ereditato tutto cio che potrebbe far arrivare il backend a un DGX, a un DB reale o a segreti non previsti. */
function cleanEnv(): Record<string, string> {
  const blocked = /^(Ai__|AI_|BPROJECTS_|ConnectionStrings__|Auth__|Parser__|Documents__|Execution__|Bootstrap__|ASPNETCORE_|DOTNET_ENVIRONMENT)/i;
  const env: Record<string, string> = {};
  for (const [k, v] of Object.entries(process.env)) if (v !== undefined && !blocked.test(k)) env[k] = v;
  return env;
}

function killTree(child: ChildProcess | undefined) {
  if (!child || child.pid === undefined || child.exitCode !== null) return;
  try {
    if (process.platform === 'win32') execFileSync('taskkill', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
    else process.kill(-child.pid, 'SIGKILL');
  } catch {
    /* gia terminato */
  }
}

async function waitFor(check: () => Promise<boolean>, what: string, timeoutMs: number) {
  const end = Date.now() + timeoutMs;
  for (;;) {
    try {
      if (await check()) return;
    } catch {
      /* non ancora pronto */
    }
    if (Date.now() > end) throw new Error(`timeout in attesa di ${what}`);
    await new Promise((r) => setTimeout(r, 300));
  }
}

const mime: Record<string, string> = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon', '.png': 'image/png', '.woff2': 'font/woff2', '.woff': 'font/woff', '.txt': 'text/plain', '.map': 'application/json',
};

export class RealStack {
  llm!: FakeLlm;
  database = '';
  user = 'pm-e2e';
  password = randomBytes(12).toString('hex') + 'Aa1!';
  backendRestarts = 0;
  /** Variabili Ai__*/Documents__*/... aggiuntive per il backend (configurazione normale, nessun hook): es. finestra piccola per la prova oltre contesto. */
  envOverride: Record<string, string> = {};
  private backend?: ChildProcess;
  private proxy?: Server;
  private control?: Server;
  private storageRoot = '';
  private signingKey = randomBytes(32).toString('hex');
  private pgPassword = '';
  private parserSecret = '';
  private backendLogFd?: number;
  private stopped = false;
  private readonly distDir = join(repo, 'frontend', 'dist', 'bprojects', 'browser');

  get baseUrl() {
    return `http://127.0.0.1:${PORTS.proxy}`;
  }

  static async start(): Promise<RealStack> {
    const stack = new RealStack();
    try {
      await stack.boot();
    } catch (e) {
      await stack.stop();
      throw e;
    }
    return stack;
  }

  private connectionString() {
    return `Host=127.0.0.1;Port=${PG_HOST_PORT};Database=${this.database};Username=postgres;Password=${this.pgPassword};Maximum Pool Size=20`;
  }

  private backendEnv(): Record<string, string> {
    return {
      ...cleanEnv(),
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: `http://127.0.0.1:${PORTS.backend}`,
      'Logging__LogLevel__Microsoft.EntityFrameworkCore': 'Warning',
      Auth__SigningKey: this.signingKey,
      ConnectionStrings__AppDb: this.connectionString(),
      // Modello finto, valori fittizi: nessun segreto reale, nessun DGX.
      Ai__Endpoint: this.llm.endpoint,
      Ai__ModelId: MODEL_ID,
      Ai__ApiKey: 'fake-llm-dummy-key',
      Ai__ContextWindowTokens: String(CONTEXT_WINDOW),
      Ai__RuntimeCheckSeconds: '5',
      Documents__StorageRoot: this.storageRoot,
      Parser__BaseUrl: `http://127.0.0.1:${PARSER_PORT}/`,
      Parser__Secret: this.parserSecret,
      ...this.envOverride,
      ...(process.env['E2E_STACK_BACKEND_ENV'] ? (JSON.parse(process.env['E2E_STACK_BACKEND_ENV']) as Record<string, string>) : {}),
    };
  }

  private runOnce(args: string[], extra: Record<string, string> = {}) {
    const dir = join(repo, 'backend', 'src', 'bProjects.App', 'bin', 'Debug', 'net10.0');
    return execFileSync('dotnet', [join(dir, 'bProjects.App.dll'), ...args], { cwd: dir, env: { ...this.backendEnv(), ...extra }, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  }

  private async boot() {
    mkdirSync(logsDir, { recursive: true });
    this.cleanStale();
    for (const [name, port] of Object.entries(PORTS)) await this.assertFree(name, port);
    this.pgPassword = dockerEnv(PG_CONTAINER, 'POSTGRES_PASSWORD');
    this.parserSecret = dockerEnv(PARSER_CONTAINER, 'PARSER_INTERNAL_SECRET');
    await waitFor(async () => (await fetch(`http://127.0.0.1:${PARSER_PORT}/health`)).ok, 'il parser', 20_000);

    // Build del backend (incrementale) e del frontend, se mancante o piu vecchio dei sorgenti.
    execFileSync('dotnet', ['build', join(repo, 'backend', 'src', 'bProjects.App', 'bProjects.App.csproj'), '--no-restore', '-v', 'q', '-nologo'], { stdio: 'pipe' });
    this.ensureFrontend();

    this.llm = await FakeLlm.start({ port: PORTS.llm, modelId: MODEL_ID, contextWindowTokens: CONTEXT_WINDOW });

    this.database = `bprojects_e2e_${randomBytes(5).toString('hex')}`;
    psqlAdmin(`CREATE DATABASE "${this.database}"`);
    this.storageRoot = join(tmpdir(), `bprojects-e2e-${this.database}`);
    mkdirSync(this.storageRoot, { recursive: true });
    writeFileSync(join(logsDir, 'active.json'), JSON.stringify({ database: this.database, storageRoot: this.storageRoot, pid: process.pid, startedAt: new Date().toISOString() }));

    this.runOnce(['migrate']);
    this.runOnce(['bootstrap-accounts'], { Bootstrap__Accounts__0__UserName: this.user, Bootstrap__Accounts__0__Password: this.password });
    await this.startBackend();
    await this.startProxy();
    await this.startControl();
    process.env['E2E_BASE_URL'] = this.baseUrl;
    process.env['E2E_USER'] = this.user;
    process.env['E2E_PASSWORD'] = this.password;
    process.env['E2E_STACK'] = 'real-fake-llm';
    process.env['E2E_STACK_CONTROL'] = `http://127.0.0.1:${PORTS.control}`;
    process.env['E2E_STACK_DB'] = this.database;
  }

  /** Resti di una esecuzione precedente interrotta con la forza: database e cartella di storage. */
  private cleanStale() {
    const file = join(logsDir, 'active.json');
    if (!existsSync(file)) return;
    try {
      const old = JSON.parse(readFileSync(file, 'utf8')) as { database: string; storageRoot: string };
      if (/^bprojects_e2e_[0-9a-f]+$/.test(old.database)) psqlAdmin(`DROP DATABASE IF EXISTS "${old.database}" WITH (FORCE)`);
      if (old.storageRoot.includes('bprojects-e2e-')) rmSync(old.storageRoot, { recursive: true, force: true });
    } catch {
      /* best effort */
    }
    rmSync(file, { force: true });
  }

  private async assertFree(name: string, port: number) {
    await new Promise<void>((ok, ko) => {
      const probe = createServer();
      probe.once('error', () => ko(new Error(`porta ${port} (${name}) occupata: un'altra esecuzione dello stack e ancora attiva`)));
      probe.listen(port, '127.0.0.1', () => probe.close(() => ok()));
    });
  }

  private ensureFrontend() {
    const index = join(this.distDir, 'index.html');
    const newest = (dir: string): number => {
      let max = 0;
      for (const e of readdirSync(dir, { withFileTypes: true })) {
        const p = join(dir, e.name);
        max = Math.max(max, e.isDirectory() ? newest(p) : statSync(p).mtimeMs);
      }
      return max;
    };
    if (existsSync(index) && statSync(index).mtimeMs >= newest(join(repo, 'frontend', 'src')) && !process.env['E2E_REBUILD_FRONTEND']) return;
    execFileSync(process.platform === 'win32' ? 'npm.cmd' : 'npm', ['--prefix', join(repo, 'frontend'), 'run', 'build'], { stdio: 'pipe', shell: process.platform === 'win32' });
  }

  async startBackend() {
    if (this.backend && this.backend.exitCode === null) throw new Error('backend gia in esecuzione');
    this.backendRestarts++;
    const dir = join(repo, 'backend', 'src', 'bProjects.App', 'bin', 'Debug', 'net10.0');
    this.backendLogFd = openSync(join(logsDir, `backend-${this.backendRestarts}.log`), 'w');
    this.backend = spawn('dotnet', [join(dir, 'bProjects.App.dll')], { cwd: dir, env: this.backendEnv(), stdio: ['ignore', this.backendLogFd, this.backendLogFd], windowsHide: true, detached: process.platform !== 'win32' });
    this.backend.once('exit', () => {
      if (this.backendLogFd !== undefined) closeSync(this.backendLogFd);
      this.backendLogFd = undefined;
    });
    await waitFor(async () => (await fetch(`http://127.0.0.1:${PORTS.backend}/health`)).ok, 'il backend', 90_000);
  }

  async stopBackend() {
    killTree(this.backend);
    await new Promise((r) => setTimeout(r, 500));
  }

  async restartBackend() {
    await this.stopBackend();
    await this.startBackend();
  }

  // ---------------------------------------------------------------- proxy (frontend statico + /api)

  private async startProxy() {
    this.proxy = createServer((req, res) => {
      const url = new URL(req.url ?? '/', 'http://x');
      if (url.pathname.startsWith('/api/') || url.pathname === '/health') return this.forward(req, res);
      const safe = normalize(decodeURIComponent(url.pathname)).replace(/^([/\\])+/, '');
      let file = join(this.distDir, safe);
      if (!file.startsWith(this.distDir) || !existsSync(file) || !statSync(file).isFile()) file = join(this.distDir, 'index.html');
      res.writeHead(200, { 'content-type': mime[extname(file)] ?? 'application/octet-stream', 'cache-control': 'no-cache' });
      res.end(readFileSync(file));
    });
    await new Promise<void>((ok) => this.proxy!.listen(PORTS.proxy, '127.0.0.1', ok));
  }

  private forward(req: IncomingMessage, res: ServerResponse) {
    const upstream = httpRequest({ host: '127.0.0.1', port: PORTS.backend, method: req.method, path: req.url, headers: { ...req.headers, host: `127.0.0.1:${PORTS.proxy}` } }, (up) => {
      res.writeHead(up.statusCode ?? 502, up.headers);
      res.flushHeaders();
      up.pipe(res);
    });
    upstream.on('error', () => {
      if (!res.headersSent) res.writeHead(502, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ status: 502, code: 'backend_down' }));
    });
    res.on('close', () => upstream.destroy());
    req.pipe(upstream);
  }

  // ---------------------------------------------------------------- API di controllo per le prove

  private async startControl() {
    this.control = createServer((req, res) => {
      void (async () => {
        const chunks: Buffer[] = [];
        for await (const c of req) chunks.push(c as Buffer);
        const body = chunks.length ? (JSON.parse(Buffer.concat(chunks).toString('utf8')) as Record<string, unknown>) : {};
        const path = new URL(req.url ?? '/', 'http://x').pathname;
        const out = (value: unknown, status = 200) => {
          res.writeHead(status, { 'content-type': 'application/json' });
          res.end(JSON.stringify(value));
        };
        try {
          switch (`${req.method} ${path}`) {
            case 'POST /backend/restart': {
              if (body['env'] !== undefined) this.envOverride = body['env'] as Record<string, string>;
              await this.restartBackend();
              return out({ ok: true, env: Object.keys(this.envOverride) });
            }
            case 'POST /backend/stop': await this.stopBackend(); return out({ ok: true });
            case 'POST /backend/start': await this.startBackend(); return out({ ok: true });
            case 'POST /sql': return out(sql(this.database, String(body['query'])));
            case 'GET /llm/requests': return out({ requests: this.llm.requests, unexpected: this.llm.unexpected });
            // Isolamento fra prove: svuota progetti e tutto cio che ne dipende (account e ruoli restano). Il backend non tiene stato in memoria sui progetti.
            case 'POST /db/reset': execSql(this.database, 'TRUNCATE TABLE "Projects" CASCADE'); return out({ ok: true });
            case 'POST /llm/reset': this.llm.reset(); return out({ ok: true });
            case 'POST /llm/fault': this.llm.faults.push(body as unknown as Fault); return out({ ok: true });
            case 'POST /llm/runtime': Object.assign(this.llm.runtime, body); return out({ ok: true, runtime: this.llm.runtime });
            case 'POST /llm/config': Object.assign(this.llm.config, body); return out({ ok: true, config: this.llm.config });
            case 'POST /llm/clear-faults': this.llm.faults.length = 0; return out({ ok: true });
            default: return out({ error: 'non supportato' }, 404);
          }
        } catch (e) {
          return out({ error: (e as Error).message }, 500);
        }
      })();
    });
    await new Promise<void>((ok) => this.control!.listen(PORTS.control, '127.0.0.1', ok));
  }

  // ---------------------------------------------------------------- chiusura

  async stop() {
    if (this.stopped) return;
    this.stopped = true;
    killTree(this.backend);
    await new Promise((r) => setTimeout(r, 300));
    for (const s of [this.proxy, this.control]) if (s) await new Promise<void>((ok) => { s.closeAllConnections?.(); s.close(() => ok()); });
    await this.llm?.stop();
    if (this.database && !process.env['E2E_KEEP_DB']) {
      try {
        psqlAdmin(`DROP DATABASE IF EXISTS "${this.database}" WITH (FORCE)`);
      } catch {
        /* resta in active.json: ripulito alla prossima esecuzione */
        return;
      }
      if (this.storageRoot) rmSync(this.storageRoot, { recursive: true, force: true });
      rmSync(join(logsDir, 'active.json'), { force: true });
    }
  }
}

/** Registra la pulizia anche per uscite anomale del processo del runner. */
export function guardExit(stack: RealStack) {
  const bye = () => {
    // Sincrono: l'uscita non attende promesse. Il backend e il database residui sono ripuliti dall'esecuzione successiva (active.json).
    try {
      (stack as unknown as { backend?: ChildProcess }).backend && killTree((stack as unknown as { backend?: ChildProcess }).backend);
    } catch {
      /* best effort */
    }
  };
  process.once('exit', bye);
  for (const sig of ['SIGINT', 'SIGTERM'] as const) process.once(sig, () => { bye(); process.exit(1); });
}
