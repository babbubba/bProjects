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

export type Graph = 'extract' | 'extract-pm' | 'doc-chat' | 'doc-chat-scan' | 'doc-chat-reduce' | 'doc-analysis' | 'doc-reconcile' | 'doc-draft' | 'unknown';

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

/** Risposta imposta alle prossime 	imes richieste del grafo (T106: un modello che obbedisce al documento ostile). I validatori del backend restano attivi. */
export interface Override {
  graph: Graph | '*';
  times: number;
  content: string;
}

export interface FakeLlmOptions {
  host?: string;
  port?: number;
  modelId: string;
  /** Finestra del contesto riportata da /props (deve essere >= Ai__ContextWindowTokens, altrimenti il backend rileva WindowMismatch). */
  contextWindowTokens: number;
}

const MARKERS: [Graph, string][] = [
  ['doc-chat-scan', '# Scansione documentale oltre contesto'],
  ['doc-chat-reduce', '# Riduzione delle note'],
  ['doc-reconcile', 'Sei il riconciliatore globale delle evidenze di bProjects'],
  ['doc-draft', "Sei l'assistente di redazione di bProjects. Ricevi una PARTE"],
  ["extract", "Sei l'estrattore delle evidenze documentali di bProjects"],
  ['extract-pm', "Sei l'estrattore delle evidenze di bProjects"],
  ['doc-analysis', "Sei l'assistente di analisi di bProjects, in modalità documentale."],
  ['doc-draft', "Sei l'assistente di redazione di bProjects. Ricevi l'analisi dei requisiti di un progetto, gia verificata"],
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

/** Messaggi del PM: nessuna informazione registrata (`ExtractionOutput` di evidence-1.0.0: solo `evidences` e `noEvidenceReason`, nessun altro campo; mai inventare). */
export function pmExtractionReply(): string {
  return JSON.stringify({ evidences: [], noEvidenceReason: 'Il messaggio non contiene informazioni sul progetto da registrare.' });
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

/**
 * Analisi documentale v5 (contratto alias-2, una sola chiamata): legge SOLO le voci `<<<VOCE [E<n>] origine=.. tipo=..>>>` del messaggio, come un modello
 * reale. Un requisito per evidenza; per ogni evidenza `tipo=Gap` una domanda aperta con valutazione Open/NoAnswer (il caso piu prudente).
 * Senza voci con `tipo=` (contratti storici) la richiesta non e supportata.
 */
export function analysisReply(text: string): string | null {
  const entries = [...text.matchAll(/<<<VOCE \[(E[1-9][0-9]*)\] origine=(\w+) tipo=(\w+)[^>\n]*>>>\n([\s\S]*?)\n<<</g)]
    .map((m) => ({ alias: m[1], origin: m[2], kind: m[3], text: m[4] }));
  if (entries.length === 0) return null;
  const aliases = entries.map((e) => e.alias);
  const requirements: unknown[] = [];
  const questions: unknown[] = [];
  const gapAssessments: unknown[] = [];
  entries.forEach((e, i) => {
    const classification = e.origin === 'Message' ? 'Declared' : 'Documented';
    const item = { key: `i${i + 1}`, text: e.text, classification, evidence: [e.alias] };
    if (e.kind === 'Gap') {
      questions.push(item);
      gapAssessments.push({ gap: e.alias, outcome: 'Open', reason: 'NoAnswer', rationale: 'Nessun riscontro nelle fonti.', supportingEvidence: [], openItem: item.key, openConflict: null });
    } else requirements.push(item);
  });
  return JSON.stringify({
    contract: 'alias-2', visited: { A: aliases, B: aliases }, noFindings: { A: 'Nessuna relazione oltre quelle note.', B: 'Nessuna relazione oltre quelle note.', Cross: null },
    items: { goals: [], scopeOverview: [], scopeIncluded: [], scopeExcluded: [], requirements, flows: [], constraints: [], dependencies: [], assumptions: [], openQuestions: questions },
    conflicts: [], findings: [], knownRelationChecks: [], patches: [], gapAssessments,
  });
}

/** Bozza v3 (alias): un unico blocco con tutti gli item `[I<n>]` del messaggio e le loro fonti `F<n>` (DTO document-draft-alias-1). */
export function draftReply(text: string): string | null {
  const elements = [...text.matchAll(/<<<(?:ELEMENTO|ALTERNATIVA) \[(I[1-9][0-9]*)\][^>\n]*fonti=([^>\n]*)>>>/g)]
    .map((m) => ({ item: m[1], sources: m[2].split(',').map((x) => x.trim()).filter(Boolean) }));
  if (elements.length === 0) return null;
  const sources = [...new Set(elements.flatMap((e) => e.sources))];
  return JSON.stringify({ sectionKey: 'documento', blocks: [{ items: elements.map((e) => e.item), markdown: '- Requisiti confermati ' + sources.map((s) => `[fonte:${s}]`).join(' '), sources }] });
}

// ---- oltre contesto: chat a scansione/riduzione/finale e riconciliazione gerarchica (stessi contratti dei modelli finti in-process)

interface EvidenceBlock {
  label: string;
  body: string;
}

const evidenceBlocks = (text: string): EvidenceBlock[] =>
  [...text.matchAll(/<<<EVIDENZA[^\n]*>>>\n([\s\S]*?)<<<FINE EVIDENZA>>>/g)]
    .map((m) => ({ label: /fonte:(F[1-9][0-9]*|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/.exec(m[1])?.[1] ?? '', body: m[1] }))
    .filter((b) => b.label !== '');

/** Parole (>= 6 lettere) della domanda del PM: nel messaggio e dopo l'ultimo blocco di evidenze/note oppure dopo «Messaggio del project manager:». */
function questionKeywords(text: string): string[] {
  const marker = text.lastIndexOf('Messaggio del project manager:');
  const tail = marker >= 0 ? text.slice(marker) : text.slice(Math.max(text.lastIndexOf('<<<FINE EVIDENZA>>>'), text.lastIndexOf('<<<FINE NOTA>>>'), 0));
  return [...tail.toLowerCase().matchAll(/\p{L}{6,}/gu)].map((m) => m[0]);
}

/** Evidenze che rispondono alla domanda, raggruppate per la prima parola chiave della domanda che contengono (una nota per gruppo, mai una per evidenza). */
function groupsByKeyword(blocks: EvidenceBlock[], keywords: string[]): Map<string, EvidenceBlock[]> {
  const groups = new Map<string, EvidenceBlock[]>();
  for (const b of blocks) {
    const keyword = keywords.find((k) => b.body.toLowerCase().includes(k));
    if (keyword) groups.set(keyword, [...(groups.get(keyword) ?? []), b]);
  }
  return groups;
}

/** Scansione di un gruppo: una nota per argomento trovato (non per evidenza), che cita la PRIMA evidenza pertinente del gruppo; altrimenti no_support motivato. */
export function scanReply(text: string, noteWords: number): string {
  const groups = groupsByKeyword(evidenceBlocks(text), questionKeywords(text));
  if (groups.size === 0) {
    return JSON.stringify({ outcome: 'no_support', reason: 'Nessuna evidenza di questo gruppo riguarda la domanda del project manager.', notes: [] });
  }
  const notes = [...groups].map(([keyword, blocks]) => ({
    kind: 'support',
    text: `Nota ${keyword}: ${blocks.length} evidenze pertinenti. ` + Array.from({ length: noteWords }, (_, i) => `riempitivo${i}`).join(' '),
    sources: [blocks[0].label],
  }));
  return JSON.stringify({ outcome: 'support', reason: '', notes });
}

/** Riduzione: una sintesi breve e a lunghezza limitata (converge sempre), con gli argomenti delle note unite. */
export function reduceReply(text: string): string {
  const notes = [...text.matchAll(/<<<NOTA[^\n]*>>>\n([\s\S]*?)\n<<<FINE NOTA>>>/g)].map((m) => m[1]);
  const keywords = [...new Set(notes.map((n) => /^Nota (\S+):/.exec(n)?.[1]).filter(Boolean))];
  return JSON.stringify({ text: `Sintesi unita di ${notes.length} note: ${keywords.join(', ')}.`, conflict: false });
}

/** Risposta finale oltre contesto: cita, per ogni argomento della domanda, la prima fonte riaperta che lo contiene; altrimenti dichiara l'assenza di uso dei documenti. */
export function longFinalReply(text: string): string | null {
  const blocks = evidenceBlocks(text);
  if (blocks.length === 0) return null;
  const groups = groupsByKeyword(blocks, questionKeywords(text));
  if (groups.size === 0) return JSON.stringify({ mode: 'no_document_use', answer: 'Nei documenti selezionati non risulta nulla di pertinente.' });
  return JSON.stringify({ mode: 'documented', answer: 'Zone trovate: ' + [...groups].map(([keyword, found]) => `${keyword} [fonte:${found[0].label}]`).join('; ') });
}

/**
 * Riconciliazione gerarchica (`reconcile-alias-1`, un batch per chiamata): nelle foglie della vista A una voce `requirements` per evidenza; altrove soltanto il descrittore.
 * Nessun candidato, nessuna patch: i collegamenti li trova solo un modello reale (qui il modello finto non emette relazioni, come negli altri modelli finti).
 */
export function reconcileReply(text: string): string | null {
  const stageName = /^Tipo di batch: (\w+)$/m.exec(text)?.[1];
  if (!stageName) return null;
  const stage = ({ foglia: 'leaf', riduzione: 'reduce', espansione: 'expand', confronto: 'compare' } as Record<string, string>)[stageName] ?? stageName;
  const view = /^Vista: (\w+)$/m.exec(text)?.[1] ?? '';
  const voci = [...text.matchAll(/<<<VOCE \[(E[1-9][0-9]*)\][^>\n]*>>>\n([\s\S]*?)\n<<<FONTE/g)].map((m) => ({ alias: m[1], statement: m[2] }));
  const nodes = [...text.matchAll(/<<<NODO \[(N[1-9][0-9]*)\]/g)].map((m) => m[1]);
  const items = stage === 'leaf' && view === 'A'
    ? voci.map((v, i) => ({ key: `i${i + 1}`, section: 'requirements', text: v.statement, classification: 'Documented', evidence: [v.alias] }))
    : [];
  const refs = [...voci.map((v) => v.alias), ...nodes];
  return JSON.stringify({
    contract: 'reconcile-alias-1',
    descriptor: stage === 'leaf' || stage === 'reduce' ? `gruppo ${stage} ${view} ${refs[0] ?? ''}-${refs.at(-1) ?? ''} (${refs.length})` : null,
    noFindings: items.length > 0 ? null : 'nessun collegamento emerso',
    items, candidates: [], focus: stage === 'compare' ? { outcome: 'Compared', reason: null } : null, patches: [],
  });
}

function reply(graph: Graph, text: string, config: LlmConfig): string | null {
  switch (graph) {
    case 'extract': return extractionReply(text);
    case 'extract-pm': return pmExtractionReply();
    case 'doc-chat': return longFinalReply(text) ?? chatReply(text);
    case 'doc-chat-scan': return scanReply(text, config.noteWords);
    case 'doc-chat-reduce': return reduceReply(text);
    case 'doc-reconcile': return reconcileReply(text);
    case 'doc-analysis': return analysisReply(text);
    case 'doc-draft': return draftReply(text);
    default: return null;
  }
}

/** Parametri di comportamento delle risposte (non del protocollo). */
export interface LlmConfig {
  /** Parole di riempimento di ogni nota di scansione: note grandi forzano la riduzione gerarchica della chat. */
  noteWords: number;
}

/** Stato del runtime simulato (readiness): `down` = ogni richiesta risponde 503; `missing` = il modello non e elencato da /v1/models; `unloaded` = elencato ma non caricato. */
export interface RuntimeState {
  mode: 'normal' | 'down' | 'missing' | 'unloaded';
  contextWindowTokens: number;
}

export class FakeLlm {
  readonly requests: RecordedRequest[] = [];
  readonly runtime: RuntimeState;
  readonly config: LlmConfig = { noteWords: 10 };
  /** Richieste fuori dal protocollo o di grafi non riconosciuti: devono restare vuote (le prove lo verificano). */
  readonly unexpected: string[] = [];
  readonly faults: Fault[] = [];
  readonly overrides: Override[] = [];
  private server!: Server;
  private readonly sockets = new Set<import('node:net').Socket>();
  private seq = 0;

  private constructor(private readonly options: FakeLlmOptions) {
    this.runtime = { mode: 'normal', contextWindowTokens: options.contextWindowTokens };
  }

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
    this.overrides.length = 0;
    this.runtime.mode = 'normal';
    this.runtime.contextWindowTokens = this.options.contextWindowTokens;
    this.config.noteWords = 10;
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
      if (this.runtime.mode === 'down') return json(503, { error: { message: 'fake-llm: runtime non raggiungibile (simulato)', type: 'fake_llm_fault' } });
      if (req.method === 'GET' && path === '/v1/models') {
        const data = this.runtime.mode === 'missing' ? [{ id: 'altro-modello', object: 'model' }] : [{ id: this.options.modelId, object: 'model', status: { value: this.runtime.mode === 'unloaded' ? 'unloaded' : 'loaded', args: ['llama-server'] } }];
        return json(200, { object: 'list', data });
      }
      if (req.method === 'GET' && path === '/props') {
        return json(200, { default_generation_settings: { n_ctx: this.runtime.contextWindowTokens, params: { n_predict: -1 } }, total_slots: 1 });
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

    let content = reply(graph, text, this.config);
    const override = this.overrides.find((o) => (o.graph === '*' || o.graph === graph) && o.times > 0);
    if (override) {
      override.times--;
      content = override.content;
      record.outcome = 'override';
    }
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
