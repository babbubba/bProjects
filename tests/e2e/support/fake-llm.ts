import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';

// Modello LLM FINTO per il solo harness E2E offline (stack reale: backend + PostgreSQL + parser reali, risposte del modello simulate).
// NON e un emulatore OpenAI: implementa soltanto il protocollo che il backend usa davvero verso un runtime llama.cpp
// (backend/src/bProjects.App/AI/InternalModelClient.cs):
//   GET  /v1/models, /props, /slots  (sonda del runtime: ModelRuntimeMonitor)
//   POST /tokenize, /apply-template  (conteggio dei token: parole separate da spazi, come il FlowModel dei test in-process)
//   POST /v1/chat/completions        (risposta non in streaming e SSE; response_format json_schema ignorato: le risposte sono gia conformi)
// Si lega SOLO a 127.0.0.1. Ogni richiesta non riconosciuta riceve un errore esplicito (mai un successo generico) ed e registrata in `unexpected`.
// Le risposte sono deterministiche e derivano SOLO dal testo del messaggio inviato (come farebbe un modello reale), con le stesse regole dei
// modelli finti in backend/tests/bProjects.IntegrationTests (DocumentSmallFlowTests.FlowModel e simili): i validatori del backend restano attivi.

export type Graph = 'extract' | 'extract-pm' | 'doc-chat' | 'unknown';

export interface RecordedRequest {
  seq: number;
  at: string;
  graph: Graph;
  system: string;
  text: string;
  stream: boolean;
  /** Esito servito: 'ok' | 'error:<status>' | 'drop' | 'delay'. */
  outcome: string;
}

/** Comportamento programmato per le prove di errore: si applica alle prossime `times` richieste del grafo (o di tutti, '*'). */
export interface Fault {
  graph: Graph | '*';
  times: number;
  /** HTTP status da restituire (es. 503); oppure 'drop' per chiudere la connessione, oppure 'delay' (con delayMs) e poi rispondere normalmente. */
  kind: number | 'drop' | 'delay';
  delayMs?: number;
}

export interface FakeLlmOptions {
  host?: string;
  port?: number;
  modelId: string;
  /** Finestra del contesto riportata da /props (deve essere >= Ai__ContextWindowTokens, altrimenti il backend rileva WindowMismatch). */
  contextWindowTokens: number;
}

const MARKERS: [Graph, string][] = [
  ["extract", "Sei l'estrattore delle evidenze documentali di bProjects"],
  ['extract-pm', "Sei l'estrattore delle evidenze di bProjects"],
  ['doc-chat', "Sei l'assistente conversazionale di bProjects. Parli con un project manager italiano che descrive un progetto software e può consultare i documenti"],
];

const norm = (s: string) => s.replace(/\s+/g, ' ').trim();

export function classify(system: string): Graph {
  const s = norm(system);
  for (const [graph, marker] of MARKERS) if (s.includes(norm(marker))) return graph;
  return 'unknown';
}

const words = (text: string) => text.split(/\s+/).filter(Boolean).length;

function textOf(content: unknown): string {
  if (typeof content === 'string') return content;
  if (Array.isArray(content)) return content.map((p) => (p && typeof p === 'object' && 'text' in p ? String((p as { text: unknown }).text) : '')).join('');
  return '';
}

/** Una evidenza per ogni intervallo Primary con almeno quattro parole (citazione = tutto l'intervallo, enunciato = il testo); altrimenti zero evidenze motivate. */
export function extractionReply(text: string): string {
  const range = /<<<INTERVALLO rangeKey=(\S+) ruolo=(Primary|Context)>>>\n([\s\S]*?)\n<<<FINE rangeKey=\1>>>/g;
  const ranges = [...text.matchAll(range)]
    .map((m) => ({ key: m[1], primary: m[2] === 'Primary', text: m[3] }))
    .filter((r) => r.primary && words(r.text) >= 4);
  const evidences = ranges.map((r, i) => ({
    key: `e${i + 1}`,
    sources: [{ kind: 'DocumentPassage', rangeKey: r.key, quote: r.text, occurrence: 1, role: 'Primary', citationGroupKey: `g${i + 1}`, part: 0 }],
    kind: 'Requirement', kindRationale: null, statement: r.text, subject: null, action: null, object: null,
    scope: null, purpose: null, instrument: null, channel: null, recipient: null,
    qualifiers: [], polarity: 'Affirmed', certainty: 'Declared', relations: [],
  }));
  return JSON.stringify({
    schemaVersion: 'evidence-1.1.0', evidences,
    noEvidenceReason: evidences.length === 0 ? 'Solo titoli o frasi troppo brevi per un requisito.' : null,
  });
}

/** Messaggi del PM: nessuna informazione registrata (risposta valida dello schema evidence-1.0.0, mai inventare). */
export function pmExtractionReply(): string {
  return JSON.stringify({ schemaVersion: 'evidence-1.0.0', evidences: [], noEvidenceReason: 'Il messaggio non contiene informazioni sul progetto da registrare.' });
}

/** Cita le fonti il cui testo risponde alla domanda (contiene una parola chiave del messaggio PM); se nessuna, la prima fonte disponibile. */
export function chatReply(text: string): string {
  const blocks = [...text.matchAll(/<<<FONTE fonte:([0-9a-f-]{36}|F[0-9]+) ruolo=\w+>>>\n([\s\S]*?)\n<<<FINE FONTE>>>/g)].map((m) => ({ id: m[1], quote: m[2] }));
  const marker = text.lastIndexOf('Messaggio del project manager:');
  const question = text.slice(marker + 1).toLowerCase();
  const keywords = [...question.matchAll(/\p{L}{6,}/gu)].map((m) => m[0]);
  let cited = [...new Set(blocks.filter((b) => keywords.some((k) => b.quote.toLowerCase().includes(k))).map((b) => b.id))].slice(0, 4);
  if (cited.length === 0) cited = blocks.slice(0, 1).map((b) => b.id);
  return JSON.stringify({ mode: 'documented', answer: 'Secondo i documenti: ' + cited.map((s) => `[fonte:${s}]`).join(' ') + '.' });
}

function reply(graph: Graph, text: string): string | null {
  switch (graph) {
    case 'extract': return extractionReply(text);
    case 'extract-pm': return pmExtractionReply();
    case 'doc-chat': return chatReply(text);
    default: return null;
  }
}

export class FakeLlm {
  readonly requests: RecordedRequest[] = [];
  /** Richieste fuori dal protocollo o di grafi non riconosciuti: devono restare vuote (le prove lo verificano). */
  readonly unexpected: string[] = [];
  readonly faults: Fault[] = [];
  private server!: Server;
  private readonly sockets = new Set<import('node:net').Socket>();
  private seq = 0;

  private constructor(private readonly options: FakeLlmOptions) {}

  static async start(options: FakeLlmOptions): Promise<FakeLlm> {
    const llm = new FakeLlm(options);
    llm.server = createServer((req, res) => void llm.handle(req, res));
    llm.server.on('connection', (s) => {
      llm.sockets.add(s);
      s.on('close', () => llm.sockets.delete(s));
    });
    await new Promise<void>((resolve, reject) => {
      llm.server.once('error', reject);
      llm.server.listen(options.port ?? 0, options.host ?? '127.0.0.1', resolve);
    });
    return llm;
  }

  get port(): number {
    return (this.server.address() as AddressInfo).port;
  }

  /** Base URL da passare ad Ai__Endpoint. */
  get endpoint(): string {
    return `http://127.0.0.1:${this.port}/v1/`;
  }

  async stop(): Promise<void> {
    for (const s of this.sockets) s.destroy();
    await new Promise<void>((resolve) => this.server.close(() => resolve()));
  }

  reset(): void {
    this.requests.length = 0;
    this.unexpected.length = 0;
    this.faults.length = 0;
  }

  countOf(graph: Graph): number {
    return this.requests.filter((r) => r.graph === graph).length;
  }

  private async handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const path = new URL(req.url ?? '/', 'http://x').pathname;
    const chunks: Buffer[] = [];
    for await (const c of req) chunks.push(c as Buffer);
    const body = Buffer.concat(chunks).toString('utf8');
    const json = (status: number, value: unknown) => {
      res.writeHead(status, { 'content-type': 'application/json' });
      res.end(JSON.stringify(value));
    };
    const unexpected = (why: string) => {
      this.unexpected.push(`${req.method} ${path}: ${why}`);
      json(501, { error: { message: `fake-llm: ${why}`, type: 'fake_llm_unexpected_request' } });
    };

    try {
      if (req.method === 'GET' && path === '/v1/models') {
        return json(200, { object: 'list', data: [{ id: this.options.modelId, object: 'model', status: { value: 'loaded', args: ['llama-server'] } }] });
      }
      if (req.method === 'GET' && path === '/props') {
        return json(200, { default_generation_settings: { n_ctx: this.options.contextWindowTokens, params: { n_predict: -1 } }, total_slots: 1 });
      }
      if (req.method === 'GET' && path === '/slots') return json(404, { error: { message: 'slots non esposti' } });
      if (req.method === 'POST' && path === '/tokenize') {
        const content = String((JSON.parse(body) as { content?: unknown }).content ?? '');
        return json(200, { tokens: Array.from({ length: words(content) }, (_, i) => i) });
      }
      if (req.method === 'POST' && path === '/apply-template') {
        const messages = (JSON.parse(body) as { messages: { role: string; content: unknown }[] }).messages;
        return json(200, { prompt: messages.map((m) => `[${m.role}] ${textOf(m.content)}`).join('\n') });
      }
      if (req.method === 'POST' && path === '/v1/chat/completions') return await this.chat(body, res, json, unexpected);
      return unexpected('endpoint non supportato');
    } catch (e) {
      return unexpected(`richiesta non valida: ${(e as Error).message}`);
    }
  }

  private async chat(body: string, res: ServerResponse, json: (s: number, v: unknown) => void, unexpected: (why: string) => void): Promise<void> {
    const request = JSON.parse(body) as { messages: { role: string; content: unknown }[]; stream?: boolean; model?: string };
    const messages = request.messages.map((m) => ({ role: m.role, text: textOf(m.content) }));
    const system = messages.filter((m) => m.role === 'system' || m.role === 'developer').map((m) => m.text).join('\n');
    const text = messages.filter((m) => m.role !== 'system' && m.role !== 'developer').map((m) => m.text).join('\n');
    const graph = classify(system);
    const record: RecordedRequest = {
      seq: ++this.seq, at: new Date().toISOString(), graph, system: system.slice(0, 200), text, stream: request.stream === true, outcome: 'ok',
    };
    this.requests.push(record);

    const fault = this.faults.find((f) => (f.graph === '*' || f.graph === graph) && f.times > 0);
    if (fault) {
      fault.times--;
      if (fault.kind === 'drop') {
        record.outcome = 'drop';
        res.socket?.destroy();
        return;
      }
      if (fault.kind === 'delay') {
        record.outcome = 'delay';
        await new Promise((r) => setTimeout(r, fault.delayMs ?? 1000));
      } else {
        record.outcome = `error:${fault.kind}`;
        return json(fault.kind, { error: { message: 'fake-llm: errore simulato', type: 'fake_llm_fault' } });
      }
    }

    const content = reply(graph, text);
    if (content === null) {
      record.outcome = 'error:501';
      return unexpected(`grafo non riconosciuto (sistema: ${norm(system).slice(0, 80)})`);
    }
    const usage = { prompt_tokens: words(system) + words(text), completion_tokens: words(content), total_tokens: words(system) + words(text) + words(content) };
    const model = request.model ?? this.options.modelId;
    if (request.stream === true) {
      const chunk = (delta: unknown, finish: string | null, u?: unknown) =>
        'data: ' + JSON.stringify({ id: 'fake', object: 'chat.completion.chunk', created: 1, model, choices: [{ index: 0, delta, finish_reason: finish }], ...(u ? { usage: u } : {}) }) + '\n\n';
      res.writeHead(200, { 'content-type': 'text/event-stream' });
      res.end(chunk({ role: 'assistant', content }, null) + chunk({}, 'stop', usage) + 'data: [DONE]\n\n');
      return;
    }
    return json(200, {
      id: 'fake', object: 'chat.completion', created: 1, model,
      choices: [{ index: 0, finish_reason: 'stop', message: { role: 'assistant', content } }], usage,
    });
  }
}
