import type { RecordedRequest, Fault } from '../fake-llm';

// Client (lato prova) dell'API di controllo dello stack reale con modello finto, avviato da real-stack.global-setup.ts.

const control = () => {
  const base = process.env['E2E_STACK_CONTROL'];
  if (!base) throw new Error('E2E_STACK_CONTROL non impostata: stack reale non avviato (usare playwright.real.config.ts)');
  return base;
};

async function call<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
  const response = await fetch(control() + path, { method, body: body === undefined ? undefined : JSON.stringify(body), headers: { 'content-type': 'application/json' } });
  const json = (await response.json()) as T & { error?: string };
  if (!response.ok) throw new Error(`controllo ${method} ${path}: ${json.error ?? response.status}`);
  return json;
}

export const realStack = process.env['E2E_STACK'] === 'real-fake-llm';

export const stack = {
  /** Riavvia il backend; con `env` ne sostituisce le variabili aggiuntive (es. Ai__ContextWindowTokens), `{}` ripristina la configurazione base. */
  restartBackend: (env?: Record<string, string>) => call('POST', '/backend/restart', env === undefined ? {} : { env }),
  stopBackend: () => call('POST', '/backend/stop'),
  startBackend: () => call('POST', '/backend/start'),
  /** Query SQL sul database isolato dell'esecuzione (sola lettura per convenzione delle prove). */
  sql: async <T = Record<string, unknown>>(query: string) => call<T[]>('POST', '/sql', { query }),
  /** Svuota progetti e dipendenze nel database isolato (isolamento fra le prove); gli account restano. */
  resetData: () => call('POST', '/db/reset'),
  llm: () => call<{ requests: RecordedRequest[]; unexpected: string[] }>('GET', '/llm/requests'),
  resetLlm: () => call('POST', '/llm/reset'),
  fault: (fault: Fault) => call('POST', '/llm/fault', fault),
  clearFaults: () => call('POST', '/llm/clear-faults'),
  /** Comportamento delle risposte (es. `noteWords` per forzare la riduzione gerarchica della chat). */
  llmConfig: (config: { noteWords?: number }) => call('POST', '/llm/config', config),
  /** Readiness del runtime simulato: normal | down | missing | unloaded (e/o finestra riportata). */
  runtime: (state: { mode?: 'normal' | 'down' | 'missing' | 'unloaded'; contextWindowTokens?: number }) => call('POST', '/llm/runtime', state),
  user: () => process.env['E2E_USER']!,
  password: () => process.env['E2E_PASSWORD']!,
};

// ---- API del backend reale via il proxy dello stack (token PM dell'account dell'esecuzione): letture/confronti indipendenti dalla UI.
let token: string | null = null;

async function bearer(refresh = false): Promise<string> {
  if (token && !refresh) return token;
  const base = process.env['E2E_BASE_URL']!;
  const response = await fetch(`${base}/api/auth/login`, {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ username: stack.user(), password: stack.password() }),
  });
  if (!response.ok) throw new Error(`login API fallito: ${response.status}`);
  token = ((await response.json()) as { accessToken: string }).accessToken;
  return token;
}

/** Chiamata autenticata all'API reale (JSON). Restituisce stato e corpo senza lanciare sugli errori HTTP. */
export async function api<T = unknown>(method: 'GET' | 'POST' | 'PUT', path: string, body?: unknown, headers: Record<string, string> = {}): Promise<{ status: number; body: T }> {
  const base = process.env['E2E_BASE_URL']!;
  const call = async (t: string) => fetch(base + path, { method, headers: { authorization: `Bearer ${t}`, ...(body === undefined ? {} : { 'content-type': 'application/json' }), ...headers }, body: body === undefined ? undefined : JSON.stringify(body) });
  let response = await call(await bearer());
  if (response.status === 401) response = await call(await bearer(true));
  const text = await response.text();
  return { status: response.status, body: (text ? JSON.parse(text) : null) as T };
}
