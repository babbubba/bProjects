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
  restartBackend: () => call('POST', '/backend/restart'),
  stopBackend: () => call('POST', '/backend/stop'),
  startBackend: () => call('POST', '/backend/start'),
  /** Query SQL sul database isolato dell'esecuzione (sola lettura per convenzione delle prove). */
  sql: async <T = Record<string, unknown>>(query: string) => call<T[]>('POST', '/sql', { query }),
  llm: () => call<{ requests: RecordedRequest[]; unexpected: string[] }>('GET', '/llm/requests'),
  resetLlm: () => call('POST', '/llm/reset'),
  fault: (fault: Fault) => call('POST', '/llm/fault', fault),
  clearFaults: () => call('POST', '/llm/clear-faults'),
  user: () => process.env['E2E_USER']!,
  password: () => process.env['E2E_PASSWORD']!,
};
