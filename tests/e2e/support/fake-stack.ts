import { FakeParsing } from './fake-parsing';
import type { Page, Request, Route } from '@playwright/test';
import { createHash } from 'node:crypto';
import { emptyUsage } from './context-usage';

// Stack finto per le prove d'errore (T050): API HTTP e flusso SSE sostituiti nel browser, cosi' i guasti
// (POST incerto, stream caduto, run Failed, PUT rifiutato) sono provocati in modo deterministico, senza DGX e senza
// PostgreSQL. I contratti sono quelli di specs/001-project-draft/contracts/http-api.md; nessuna logica del prodotto
// e' replicata oltre cio' che serve a rispondere come il server.
//
// Una prova d'errore con un guasto indotto non e' una prova di successo: le prove di successo restano su stack reale
// (project-draft-success.spec.ts) e, senza stack, risultano ignorate.

type Json = Record<string, unknown>;

/** Guasto una tantum su un endpoint: 'drop-after-save' salva e poi interrompe la connessione (esito incerto). */
export type Fault = 'drop-before-save' | 'drop-after-save' | number | `after-save:${number}`;

export interface Call {
  method: string;
  path: string;
  key: string | null;
  ifMatch: string | null;
  body: unknown;
}

export interface ProjectState {
  id: string;
  name: string;
  messages: Json[];
  turns: Json[];
  runs: Json[];
  /** Revisioni dell'analisi corrente (versione = indice + 1). */
  revisions: Json[];
  analysisId: string | null;
  draft: Json | null;
  keys: Map<string, string>;
  /** Risposta di GET context-usage (null = monitor vuoto: nessuna richiesta AI ancora). */
  usage: Json | null;
  /** Ritardo (ms) e stato HTTP di errore opzionale di GET context-usage. */
  usageDelayMs: number;
  usageStatus: number | null;
  /** Archivio documenti (slice 002): solo cio' che serve a rispondere come il server; nessun parser, nessun modello. */
  documents: FakeDocument[];
  /** Preferenze di fonte: revisione e voci per documento. */
  selection: { revision: number; items: Map<string, { included: boolean; mode: string; versionId: string | null }> };
  /** Ricevute di idempotenza dell'upload: chiave -> impronta del contenuto e versione creata. */
  uploadReceipts: Map<string, { fingerprint: string; versionId: string }>;
  /** Quota degli originali in byte (null = illimitata): oltre, l'upload risponde 507 quota_exceeded. */
  documentQuotaBytes: number | null;
}

export interface FakeDocVersion {
  id: string;
  version: number;
  originalName: string;
  mediaType: string;
  bytes: Buffer;
  uploadedAt: string;
}

export interface FakeDocument {
  id: string;
  displayName: string;
  versions: FakeDocVersion[];
}

const iso = (offsetSeconds = 0) => new Date(Date.UTC(2030, 0, 1, 10, 0, offsetSeconds)).toISOString();

export function analysisContent(analysisId: string, projectId: string, goal = 'Realizzare un portale clienti'): Json {
  return {
    schemaVersion: '1.0.0',
    analysisId,
    projectId,
    sourceCutoffSequence: 1,
    validationState: 'Unvalidated',
    sources: [{ id: 's-1', kind: 'Message', messageId: 'm-1', messageRole: 'user', revisionId: null }],
    goals: [{ id: 'g-1', text: goal, classification: 'Declared', evidenceRefs: ['s-1'] }],
    contextAndScope: { overview: [], included: [], excluded: [] },
    requirements: [],
    operationalFlows: [],
    constraints: [],
    dependencies: [],
    assumptions: [],
    openQuestions: [],
    conflicts: [],
  };
}

const sha256Hex = (data: Buffer) => createHash('sha256').update(data).digest('hex');

const mediaTypeOf = (name: string): string =>
  /\.pdf$/i.test(name)
    ? 'application/pdf'
    : /\.docx$/i.test(name)
      ? 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
      : /\.(md|markdown)$/i.test(name)
        ? 'text/markdown'
        : 'text/plain';

interface Multipart {
  file: { name: string; bytes: Buffer } | null;
  fields: Record<string, string>;
}

/** Multipart minimale: un file e campi testuali. I byte del file non passano per una stringa (nessuna alterazione). */
function parseMultipart(body: Buffer, contentType: string): Multipart {
  const boundary = /boundary=(?:"([^"]+)"|([^;]+))/i.exec(contentType);
  const result: Multipart = { file: null, fields: {} };
  if (!boundary) return result;
  const delimiter = Buffer.from(`--${boundary[1] ?? boundary[2]}`);
  let at = body.indexOf(delimiter);
  while (at >= 0) {
    const next = body.indexOf(delimiter, at + delimiter.length);
    if (next < 0) break;
    const part = body.subarray(at + delimiter.length + 2, next - 2); // salta CRLF dopo il delimitatore e prima del successivo
    const split = part.indexOf(Buffer.from('\r\n\r\n'));
    if (split >= 0) {
      const head = part.subarray(0, split).toString('utf-8');
      const content = part.subarray(split + 4);
      const name = /name="([^"]*)"/.exec(head)?.[1];
      const fileName = /filename="([^"]*)"/.exec(head)?.[1];
      if (fileName !== undefined) result.file = { name: fileName, bytes: Buffer.from(content) };
      else if (name) result.fields[name] = content.toString('utf-8');
    }
    at = next;
  }
  return result;
}

const isActive =(r: Json) => r['status'] === 'Queued' || r['status'] === 'Running';
const withoutClientId = ({ clientMessageId: _c, ...view }: Json): Json => view;

export class FakeStack {
  readonly calls: Call[] = [];
  readonly projects = new Map<string, ProjectState>();
  /** Sessione valida: refresh/login riescono (il cookie di refresh non e' simulato). */
  session = false;
  private readonly faults = new Map<string, Fault[]>();
  /** Parsing simulato (I2): null = nessun parser, ogni versione resta «In coda» come in I1. */
  parsing: FakeParsing | null = null;
  private seq = 0;

  constructor() {
    this.addProject('p-1', 'Aurora');
  }

  addProject(id: string, name: string): ProjectState {
    const state: ProjectState = {
      id, name, messages: [], turns: [], runs: [], revisions: [], analysisId: null, draft: null, keys: new Map(), usage: null, usageDelayMs: 0, usageStatus: null,
      documents: [], selection: { revision: 0, items: new Map() }, uploadReceipts: new Map(), documentQuotaBytes: null,
    };
    this.projects.set(id, state);
    return state;
  }

  project(id = 'p-1'): ProjectState {
    return this.projects.get(id)!;
  }

  /** Accoda guasti per un endpoint: 'postMessage' | 'analyze' | 'generate' | 'savePreview' | 'confirm'. */
  fail(endpoint: string, ...faults: Fault[]): void {
    this.faults.set(endpoint, [...(this.faults.get(endpoint) ?? []), ...faults]);
  }

  count(method: string, pattern: RegExp): number {
    return this.calls.filter((c) => c.method === method && pattern.test(c.path)).length;
  }

  callsTo(method: string, pattern: RegExp): Call[] {
    return this.calls.filter((c) => c.method === method && pattern.test(c.path));
  }

  // --- Stato del server (le prove lo cambiano come farebbe un altro processo o un'altra sessione).

  /** Messaggio PM salvato insieme al suo turno (come POST /messages). */
  addPm(projectId: string, text: string, clientMessageId = `c-${++this.seq}`): { message: Json; turn: Json } {
    const p = this.project(projectId);
    const sequence = p.messages.length + 1;
    const message: Json = {
      id: `m-${sequence}`, sequence, role: 'user', text, createdAt: iso(sequence), authorId: 'u-1',
      authorDisplayName: 'Pm Uno', agentId: null, chatTurnId: `t-${sequence}`, replyToMessageId: null, clientMessageId,
    };
    const turn: Json = {
      id: `t-${sequence}`, triggerMessageId: message['id'], sourceCutoffSequence: sequence, status: 'Queued',
      requestedAt: iso(sequence), startedAt: null, completedAt: null, deadlineAt: iso(600), attemptCount: 0, maxAttempts: 3,
      nextAttemptAt: null, resultMessageId: null, error: null,
    };
    p.messages.push(message);
    p.turns.push(turn);
    return { message, turn };
  }

  turn(projectId: string, id: string): Json {
    return this.project(projectId).turns.find((t) => t['id'] === id)!;
  }

  setTurn(projectId: string, id: string, patch: Json): Json {
    return Object.assign(this.turn(projectId, id), patch);
  }

  completeTurn(projectId: string, id: string, reply: string, metrics?: Json | null): { turn: Json; message: Json } {
    const p = this.project(projectId);
    const turn = this.turn(projectId, id);
    const sequence = p.messages.length + 1;
    const message: Json = {
      id: `m-${sequence}`, sequence, role: 'assistant', text: reply, createdAt: iso(sequence), authorId: null,
      authorDisplayName: null, agentId: 'conversational', chatTurnId: id, replyToMessageId: turn['triggerMessageId'],
      ...(metrics === undefined ? {} : { metrics }),
    };
    p.messages.push(message);
    Object.assign(turn, { status: 'Completed', completedAt: iso(sequence), resultMessageId: message['id'] });
    return { turn, message };
  }

  failTurn(projectId: string, id: string, code: string, message: string | null): Json {
    return this.setTurn(projectId, id, { status: 'Failed', completedAt: iso(500), error: { code, message } });
  }

  run(projectId: string, id: string): Json {
    return this.project(projectId).runs.find((r) => r['id'] === id)!;
  }

  setRun(projectId: string, id: string, patch: Json): Json {
    return Object.assign(this.run(projectId, id), patch);
  }

  /** Analisi riuscita: prima revisione dell'AI (Unvalidated) e run Completed. */
  completeAnalysis(projectId: string, runId: string): Json {
    const p = this.project(projectId);
    p.analysisId = runId;
    p.revisions = [{ revisionId: 'rev-1', origin: 'Ai', content: analysisContent(runId, projectId), validationState: 'Unvalidated' }];
    return this.setRun(projectId, runId, { status: 'Completed', completedAt: iso(300), analysisId: runId, resultRevisionId: 'rev-1' });
  }

  /** Analisi gia' presente per il progetto, non confermata. */
  seedAnalysis(projectId = 'p-1'): void {
    this.addPm(projectId, 'Voglio un portale clienti');
    const run = this.newRun(projectId, 'Analysis', 'seed');
    this.completeAnalysis(projectId, run['id'] as string);
  }

  newRun(projectId: string, kind: 'Analysis' | 'Document', key: string): Json {
    const p = this.project(projectId);
    const run: Json = {
      id: `r-${p.runs.length + 1}`, kind, analysisId: kind === 'Document' ? p.analysisId : null,
      sourceCutoffSequence: kind === 'Analysis' ? p.messages.length : null, status: 'Queued', requestedAt: iso(100),
      startedAt: null, completedAt: null, deadlineAt: iso(1000), attemptCount: 0, maxAttempts: 3, nextAttemptAt: null,
      lastObservedStage: null, resultRevisionId: null, draftId: null, error: null,
    };
    p.runs.push(run);
    p.keys.set(`${kind}:${key}`, run['id'] as string);
    return run;
  }

  // --- Viste.

  projectView(p: ProjectState): Json {
    const active = p.runs.find(isActive);
    const analyses = p.runs.filter((r) => r['kind'] === 'Analysis');
    const current = p.revisions.at(-1);
    return {
      id: p.id, conversationId: `c-${p.id}`, name: p.name, description: null, createdAt: iso(), createdBy: 'pm',
      activeProcessingRunId: active ? active['id'] : null, latestAnalysisRunId: analyses.at(-1)?.['id'] ?? null,
      currentAnalysisId: p.analysisId, currentRevisionId: current?.['revisionId'] ?? null,
      validationState: current?.['validationState'] ?? null,
    };
  }

  analysisView(p: ProjectState): Json {
    const version = p.revisions.length;
    const rev = p.revisions[version - 1];
    const active = p.runs.some(isActive);
    const confirmed = rev['validationState'] === 'PmConfirmed';
    return {
      analysisId: p.analysisId, projectId: p.id, currentRevisionId: rev['revisionId'], version, schemaVersion: '1.0.0',
      content: { ...(rev['content'] as Json), validationState: rev['validationState'] }, validationState: rev['validationState'],
      origin: rev['origin'], createdBy: rev['origin'] === 'Ai' ? null : 'Pm Uno', createdAt: iso(300 + version),
      confirmedBy: confirmed ? 'Pm Uno' : null, confirmedAt: confirmed ? iso(400) : null, sourceCutoffSequence: 1,
      hasNewNotes: false, canEdit: !active, canConfirm: !active && !confirmed, canGenerate: !active && confirmed,
    };
  }

  // --- Archivio documenti.

  private pref(p: ProjectState, d: FakeDocument) {
    return p.selection.items.get(d.id) ?? { included: true, mode: 'LatestReady', versionId: null };
  }

  private documentListItem(p: ProjectState, d: FakeDocument): Json {
    const pref = this.pref(p, d);
    const latest = d.versions.at(-1)!;
    return {
      documentId: d.id, displayName: d.displayName, createdAt: d.versions[0].uploadedAt, versionCount: d.versions.length,
      latestVersion: { id: latest.id, version: latest.version, processingStatus: this.statusOf(latest), uploadedAt: latest.uploadedAt },
      applicableVersionId: null, included: pref.included, selectionMode: pref.mode,
      availability: this.availabilityOf(pref.included, latest), reason: pref.included ? this.reasonOf(latest) : 'excluded_by_pm',
    };
  }

  private versionView(p: ProjectState, d: FakeDocument, v: FakeDocVersion): Json {
    const pref = this.pref(p, d);
    return {
      id: v.id, documentId: d.id, projectId: p.id, version: v.version, originalName: v.originalName, mediaType: v.mediaType,
      byteLength: v.bytes.length, sha256: sha256Hex(v.bytes), uploadedBy: 'Pm Uno', uploadedAt: v.uploadedAt, processingStatus: this.statusOf(v),
      applicationStatus: 'NotApplicable', currentPreparationId: this.parsing?.latest(v.id)?.id ?? null, currentEvidenceVersionId: null, bundleRevision: 0,
      candidatePreparations: [], latestJobId: this.parsing?.latest(v.id)?.jobId ?? null, included: pref.included, selectionMode: pref.mode, isApplicable: false,
      previousReadyVersionId: null, issuesSummary: this.parsing?.issuesSummary(v.id) ?? null, allowedActions: ['Download'],
    };
  }

  private documentDetail(p: ProjectState, d: FakeDocument): Json {
    const pref = this.pref(p, d);
    return {
      documentId: d.id, projectId: p.id, displayName: d.displayName, createdBy: 'Pm Uno', createdAt: d.versions[0].uploadedAt,
      versions: d.versions.map((v) => this.versionView(p, d, v)),
      sourcePreference: { included: pref.included, mode: pref.mode, versionId: pref.versionId },
      availability: this.availabilityOf(pref.included, d.versions.at(-1)!), applicableVersionId: null,
    };
  }

  private statusOf(v: FakeDocVersion): string {
    return this.parsing ? this.parsing.status(v.id) : 'Queued';
  }

  private availabilityOf(included: boolean, latest: FakeDocVersion): string {
    if (!included) return 'Excluded';
    return this.statusOf(latest) === 'Ready' ? 'Ready' : 'Unavailable';
  }

  private reasonOf(latest: FakeDocVersion): string | null {
    const status = this.statusOf(latest);
    return status === 'Ready' ? null : status === 'Failed' ? 'preparation_failed' : status === 'Incomplete' ? 'preparation_incomplete' : 'preparation_queued';
  }

  private selectionResponse(p: ProjectState, route: Route): Promise<void> {
    const items = p.documents.map((d) => {
      const pref = this.pref(p, d);
      return {
        documentId: d.id, included: pref.included, mode: pref.mode, versionId: pref.versionId, resolvedVersionId: null,
        preparationId: null, availability: this.availabilityOf(pref.included, d.versions.at(-1)!),
      };
    });
    const blockingSources = p.documents
      .filter((d) => this.pref(p, d).included && this.statusOf(d.versions.at(-1)!) !== 'Ready')
      .map((d) => ({
        documentId: d.id, displayName: d.displayName, versionId: d.versions.at(-1)!.id, version: d.versions.at(-1)!.version,
        status: this.statusOf(d.versions.at(-1)!), allowedActions: ['Exclude'],
      }));
    return route.fulfill({
      status: 200, contentType: 'application/json', headers: { etag: `"${p.selection.revision}"` },
      body: JSON.stringify({ revision: p.selection.revision, items, blockingSources }),
    });
  }

  /** Upload (nuovo documento se `target` e' null, altrimenti nuova versione): idempotenza per chiave, quota, guasti iniettabili. */
  private async uploadDocument(
    p: ProjectState,
    request: Request,
    call: Call,
    target: FakeDocument | null,
    faulted: (endpoint: string, save: () => void) => Promise<boolean>,
    problem: (status: number, code: string, extra?: Json) => Promise<void>,
    json: (status: number, payload: unknown) => Promise<void>,
  ): Promise<void> {
    if (!call.key) return problem(400, 'idempotency_key_required');
    const form = parseMultipart(request.postDataBuffer() ?? Buffer.alloc(0), request.headers()['content-type'] ?? '');
    const file = form.file;
    if (!file) return problem(400, 'malformed_request');
    const fingerprint = sha256Hex(
      Buffer.concat([Buffer.from(`${target?.id ?? ''}|${file.name}|${form.fields['displayName'] ?? ''}|`), file.bytes]),
    );
    const known = p.uploadReceipts.get(call.key);
    if (known) {
      if (known.fingerprint !== fingerprint) return problem(409, 'idempotency_conflict');
      for (const d of p.documents) {
        const v = d.versions.find((x) => x.id === known.versionId);
        if (v) return json(200, this.versionView(p, d, v));
      }
    }
    const used = p.documents.reduce((n, d) => n + d.versions.reduce((m, v) => m + v.bytes.length, 0), 0);
    if (p.documentQuotaBytes !== null && used + file.bytes.length > p.documentQuotaBytes) return problem(507, 'quota_exceeded');
    let created: { d: FakeDocument; v: FakeDocVersion } | undefined;
    const save = () => {
      const index = ++this.seq;
      const version: FakeDocVersion = {
        id: `v-${index}`, version: (target?.versions.length ?? 0) + 1, originalName: file.name, mediaType: mediaTypeOf(file.name),
        bytes: file.bytes, uploadedAt: iso(500 + index),
      };
      const d: FakeDocument = target ?? { id: `d-${index}`, displayName: form.fields['displayName']?.trim() || file.name, versions: [] };
      if (!target) p.documents.push(d);
      d.versions.push(version);
      this.parsing?.onUpload(version);
      p.uploadReceipts.set(call.key!, { fingerprint, versionId: version.id });
      created = { d, v: version };
    };
    if (await faulted('uploadDocument', save)) return;
    save();
    return json(201, this.versionView(p, created!.d, created!.v));
  }

  // --- Installazione nella pagina.

  async install(page: Page): Promise<void> {
    await page.addInitScript(SSE_SCRIPT);
    await page.route(/\/api\//, (route) => this.handle(route));
  }

  private async handle(route: Route): Promise<void> {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    let body: unknown = null;
    try {
      body = request.postDataJSON();
    } catch {
      body = null;
    }
    const call: Call = {
      method, path, key: request.headers()['idempotency-key'] ?? null, ifMatch: request.headers()['if-match'] ?? null, body,
    };
    this.calls.push(call);

    const json = (status: number, payload: unknown) =>
      route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(payload) });
    const problem = (status: number, code: string, extra: Json = {}) => json(status, { status, code, title: code, ...extra });
    /** Applica un guasto: restituisce true se la risposta e' gia' stata data (o interrotta). */
    const faulted = async (endpoint: string, save: () => void): Promise<boolean> => {
      const fault = this.faults.get(endpoint)?.shift();
      if (fault === undefined) return false;
      if (fault === 'drop-before-save') {
        await route.abort('connectionreset');
      } else if (fault === 'drop-after-save') {
        save();
        await route.abort('connectionreset');
      } else if (typeof fault === 'number') {
        await problem(fault, `fault_${fault}`);
      } else {
        save();
        await problem(Number(fault.split(':')[1]), 'fault_after_save');
      }
      return true;
    };
    const replay = (run: Json) => json(isActive(run) ? 202 : 200, run);

    // Autenticazione.
    if (path === '/api/auth/login' && method === 'POST') {
      this.session = true;
      return void (await json(200, { accessToken: 'tok-1', expiresAt: iso(3600) }));
    }
    if (path === '/api/auth/refresh' && method === 'POST') {
      return void (this.session ? await json(200, { accessToken: 'tok-1', expiresAt: iso(3600) }) : await problem(401, 'unauthorized'));
    }
    if (path === '/api/auth/logout' && method === 'POST') {
      this.session = false;
      return void (await route.fulfill({ status: 204 }));
    }
    if (path === '/api/projects' && method === 'GET') {
      return void (await json(200, { items: [...this.projects.values()].map((x) => this.projectView(x)), nextCursor: null }));
    }

    const m = /^\/api\/projects\/([^/]+)(\/.*)?$/.exec(path);
    const p = m ? this.projects.get(m[1]) : undefined;
    if (!m || !p) return void (await problem(404, 'not_found'));
    const rest = m[2] ?? '';

    if (rest === '' && method === 'GET') return void (await json(200, this.projectView(p)));

    if (rest === '/context-usage' && method === 'GET') {
      if (p.usageDelayMs) await new Promise((resolve) => setTimeout(resolve, p.usageDelayMs));
      if (p.usageStatus) return void (await problem(p.usageStatus, 'usage_fault'));
      return void (await json(200, p.usage ?? emptyUsage()));
    }

    if (rest === '/messages' && method === 'GET') {
      const after = Number(url.searchParams.get('afterSequence') ?? 0);
      const items = p.messages.filter((x) => (x['sequence'] as number) > after).map(withoutClientId);
      return void (await json(200, { items, nextAfterSequence: items.at(-1)?.['sequence'] ?? null }));
    }
    if (rest === '/messages' && method === 'POST') {
      const input = body as { clientMessageId: string; text: string };
      const existing = p.messages.find((x) => x['clientMessageId'] === input.clientMessageId);
      if (existing) {
        if (existing['text'] !== input.text) return void (await problem(409, 'idempotency_conflict'));
        return void (await json(200, { message: withoutClientId(existing), chatTurnId: existing['chatTurnId'] }));
      }
      let created: Json | undefined;
      const save = () => {
        created = this.addPm(p.id, input.text, input.clientMessageId).message;
      };
      if (await faulted('postMessage', save)) return;
      save();
      return void (await json(201, { message: withoutClientId(created!), chatTurnId: created!['chatTurnId'] }));
    }
    const turn = /^\/chat-turns\/([^/]+)$/.exec(rest);
    if (turn && method === 'GET') {
      const t = p.turns.find((x) => x['id'] === turn[1]);
      return void (t ? await json(200, t) : await problem(404, 'not_found'));
    }

    if (rest === '/analyses' && method === 'POST') {
      const key = call.key ?? '';
      const known = p.keys.get(`Analysis:${key}`);
      if (known) return void (await replay(this.run(p.id, known)));
      if (!p.messages.some((x) => x['role'] === 'user')) return void (await problem(422, 'no_pm_input'));
      const active = p.runs.find(isActive);
      if (active) return void (await problem(409, 'processing_active', { processingRunId: active['id'] }));
      let run: Json | undefined;
      const save = () => {
        run = this.newRun(p.id, 'Analysis', key);
      };
      if (await faulted('analyze', save)) return;
      save();
      return void (await json(202, run));
    }
    const runPath = /^\/processing-runs\/([^/]+)$/.exec(rest);
    if (runPath && method === 'GET') {
      const r = p.runs.find((x) => x['id'] === runPath[1]);
      return void (r ? await json(200, r) : await problem(404, 'not_found'));
    }
    const analysis = /^\/analyses\/([^/]+)(\/.*)?$/.exec(rest);
    if (analysis && analysis[1] === p.analysisId) {
      const sub = analysis[2] ?? '';
      if (sub === '' && method === 'GET') return void (await json(200, this.analysisView(p)));
      const rev = /^\/revisions\/(\d+)$/.exec(sub);
      if (rev && method === 'GET') {
        const version = Number(rev[1]);
        const r = p.revisions[version - 1];
        return void (r
          ? await json(200, {
              analysisId: p.analysisId, revisionId: r['revisionId'], version, isCurrent: version === p.revisions.length,
              content: r['content'], validationState: r['validationState'],
            })
          : await problem(404, 'not_found'));
      }
      const current = p.revisions.at(-1)!;
      const stale = call.ifMatch !== `"${current['revisionId']}"`;
      if (sub === '/preview' && method === 'PUT') {
        if (!call.ifMatch) return void (await problem(400, 'revision_required'));
        if (stale) return void (await problem(409, 'revision_conflict', { currentRevisionId: current['revisionId'] }));
        const save = () => {
          p.revisions.push({
            revisionId: `rev-${p.revisions.length + 1}`, origin: 'PmRevision', content: body, validationState: 'Unvalidated',
          });
        };
        if (await faulted('savePreview', save)) return;
        save();
        return void (await json(200, this.analysisView(p)));
      }
      if (sub === '/confirm' && method === 'POST') {
        if (stale) return void (await problem(409, 'revision_conflict', { currentRevisionId: current['revisionId'] }));
        const save = () => {
          current['validationState'] = 'PmConfirmed';
        };
        if (await faulted('confirm', save)) return;
        save();
        return void (await json(200, this.analysisView(p)));
      }
      if (sub === '/document-generations' && method === 'POST') {
        const input = body as { revisionId: string };
        if (current['validationState'] !== 'PmConfirmed') return void (await problem(409, 'analysis_not_confirmed'));
        if (input.revisionId !== current['revisionId']) return void (await problem(409, 'revision_conflict'));
        const key = call.key ?? '';
        const known = p.keys.get(`Document:${key}`);
        if (known) return void (await replay(this.run(p.id, known)));
        let run: Json | undefined;
        const save = () => {
          run = this.newRun(p.id, 'Document', key);
        };
        if (await faulted('generate', save)) return;
        save();
        return void (await json(202, run));
      }
    }
    // Archivio documenti (slice 002): le versioni restano 'Queued' perche' non c'e' alcun parser.
    if (this.parsing) {
      const parsing = this.parsing;
      const docs = {
        findVersion: (docId: string, ver: number) => {
          const d = p.documents.find((x) => x.id === docId);
          const v = d?.versions.find((x) => x.version === ver);
          return d && v ? { docId: d.id, v } : undefined;
        },
        findByJob: (jobId: string) => {
          for (const d of p.documents) for (const v of d.versions) {
            const gen = parsing.gens.get(v.id)?.find((g) => g.jobId === jobId);
            if (gen) return { docId: d.id, v, gen };
          }
          return undefined;
        },
      };
      if (await parsing.handle(rest, method, request, call.key, docs, { json, problem, faulted }, route)) return;
    }
    if (rest === '/documents' && method === 'GET') {
      this.parsing?.tick();
      return void (await json(200, { items: p.documents.map((d) => this.documentListItem(p, d)), nextCursor: null }));
    }
    if (rest === '/documents' && method === 'POST') {
      return void (await this.uploadDocument(p, request, call, null, faulted, problem, json));
    }
    const doc = /^\/documents\/([^/]+)(\/.*)?$/.exec(rest);
    if (doc) {
      const d = p.documents.find((x) => x.id === doc[1]);
      if (!d) return void (await problem(404, 'not_found'));
      const sub = doc[2] ?? '';
      if (sub === '' && method === 'GET') return void (await json(200, this.documentDetail(p, d)));
      if (sub === '/versions' && method === 'POST') {
        return void (await this.uploadDocument(p, request, call, d, faulted, problem, json));
      }
      const ver = /^\/versions\/(\d+)(\/download)?$/.exec(sub);
      const v = ver ? d.versions.find((x) => x.version === Number(ver[1])) : undefined;
      if (ver && method === 'GET') {
        if (!v) return void (await problem(404, 'not_found'));
        if (!ver[2]) return void (await json(200, this.versionView(p, d, v)));
        if (await faulted('download', () => undefined)) return;
        return void (await route.fulfill({
          status: 200,
          body: v.bytes,
          headers: {
            'content-type': v.mediaType,
            'content-disposition': `attachment; filename*=UTF-8''${encodeURIComponent(v.originalName)}`,
            'cache-control': 'no-store',
            'x-content-type-options': 'nosniff',
          },
        }));
      }
    }
    if (rest === '/source-selection' && method === 'GET') {
      return void (await this.selectionResponse(p, route));
    }
    if (rest === '/source-selection' && method === 'PUT') {
      if (call.ifMatch !== `"${p.selection.revision}"`) {
        return void (await problem(409, 'source_selection_conflict', { currentRevision: p.selection.revision }));
      }
      const input = body as { items: { documentId: string; included: boolean; mode: string; versionId: string | null }[] };
      p.selection.items = new Map(input.items.map((i) => [i.documentId, { included: i.included, mode: i.mode, versionId: i.versionId }]));
      p.selection.revision += 1;
      return void (await this.selectionResponse(p, route));
    }
    if (rest === '/draft' && method === 'GET') {
      return void (p.draft ? await json(200, p.draft) : await problem(404, 'draft_not_available'));
    }
    return void (await problem(404, 'not_found'));
  }

  // --- Flusso SSE nel browser (window.__sse, installato dallo script di avvio).

  /** Invia un evento SSE sulla connessione piu' recente ancora aperta. */
  async send(page: Page, event: string, data: unknown): Promise<void> {
    await page.evaluate(([e, d]) => (window as unknown as SseWindow).__sse.send(e as string, d), [event, data] as const);
  }

  /** Cade l'ultima connessione aperta (il client deve riconnettersi con backoff). */
  async drop(page: Page): Promise<void> {
    await page.evaluate(() => (window as unknown as SseWindow).__sse.drop());
  }

  /** Le prossime connessioni ricevono questi stati HTTP (es. 503) invece di aprirsi. */
  async refuse(page: Page, ...statuses: number[]): Promise<void> {
    await page.evaluate((s) => (window as unknown as SseWindow).__sse.failNext.push(...s), statuses);
  }

  async connections(page: Page): Promise<{ project: string; open: boolean; auth: string | null }[]> {
    return page.evaluate(() =>
      (window as unknown as SseWindow).__sse.conns.map((c) => ({ project: c.project, open: !c.closed, auth: c.auth })),
    );
  }
}

interface SseConn {
  project: string;
  closed: boolean;
  auth: string | null;
  ctrl: ReadableStreamDefaultController<Uint8Array> | null;
}

interface SseWindow {
  __sse: {
    conns: SseConn[];
    failNext: number[];
    send(event: string, data: unknown): void;
    drop(): void;
  };
}

/** Sostituisce fetch per GET /api/projects/{p}/events con un flusso controllabile dalla prova. */
const SSE_SCRIPT = `
(() => {
  const state = { conns: [], failNext: [] };
  const enc = new TextEncoder();
  const last = () => [...state.conns].reverse().find((c) => !c.closed && c.ctrl);
  state.send = (event, data) => {
    const c = last();
    if (!c) throw new Error('nessuna connessione SSE aperta');
    c.ctrl.enqueue(enc.encode('event: ' + event + '\\ndata: ' + JSON.stringify(data) + '\\n\\n'));
  };
  state.drop = () => {
    const c = last();
    if (!c) throw new Error('nessuna connessione SSE aperta');
    c.closed = true;
    try { c.ctrl.close(); } catch {}
  };
  window.__sse = state;
  const realFetch = window.fetch.bind(window);
  window.fetch = (input, init) => {
    const href = typeof input === 'string' ? input : input.url;
    const match = /^\\/api\\/projects\\/([^/]+)\\/events$/.exec(new URL(href, location.href).pathname);
    if (!match) return realFetch(input, init);
    const headers = (init && init.headers) || {};
    const conn = { project: decodeURIComponent(match[1]), closed: false, auth: headers.Authorization || null, ctrl: null };
    state.conns.push(conn);
    const status = state.failNext.shift();
    if (status) {
      conn.closed = true;
      return Promise.resolve(new Response('', { status }));
    }
    const body = new ReadableStream({
      start(c) { conn.ctrl = c; },
      cancel() { conn.closed = true; },
    });
    if (init && init.signal) init.signal.addEventListener('abort', () => { conn.closed = true; });
    return Promise.resolve(new Response(body, { status: 200, headers: { 'Content-Type': 'text/event-stream' } }));
  };
})();
`;
