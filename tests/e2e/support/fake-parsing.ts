import type { Request, Route } from '@playwright/test';

// Parsing simulato per il fake-stack (slice 002, I2/T032): preparazioni, blocchi, passaggi e retry PM. Rappresenta SOLO il contratto HTTP
// (http-api.md); non prova parser, persistenza o fence del server: quelli sono nel livello [E2E reale]. Spento di default, così le prove
// di I1 (nessun parser: ogni versione resta «In coda») restano invariate.

type Json = Record<string, unknown>;

export type ParseOutcome = 'Ready' | 'Failed' | 'Incomplete';
export type GenStatus = 'Queued' | ParseOutcome;

export interface FakeBlock {
  id: string;
  ref: string;
  ordinal: number;
  parentId: string | null;
  type: string;
  text: string;
  headingPath: string[];
  page: number | null;
  bbox: { values: number[]; coordinateSystem: string | null } | null;
  lines: { start: number; end: number } | null;
  tableCells: unknown;
}

export interface FakeGen {
  id: string;
  generation: number;
  jobId: string;
  status: GenStatus;
  outcome: ParseOutcome;
  pollsLeft: number;
  blocks: FakeBlock[] | null;
  issues: Json[];
  retryOfId: string | null;
  requestedAt: string;
}

export interface RetryReceipt {
  fingerprint: string;
  response: Json;
}

interface VersionLike {
  id: string;
  version: number;
  originalName: string;
}

type Responder = {
  json: (status: number, payload: unknown) => Promise<void>;
  problem: (status: number, code: string, extra?: Json) => Promise<void>;
  faulted: (endpoint: string, save: () => void) => Promise<boolean>;
};

const iso = (n: number) => new Date(Date.UTC(2030, 0, 1, 11, 0, n)).toISOString();

function blocksFor(versionId: string, name: string): FakeBlock[] {
  const mk = (i: number, over: Partial<FakeBlock>): FakeBlock => ({
    id: `b-${versionId}-${i}`,
    ref: `r${i}`,
    ordinal: i,
    parentId: null,
    type: 'Paragraph',
    text: '',
    headingPath: [],
    page: null,
    bbox: null,
    lines: null,
    tableCells: null,
    ...over,
  });
  const ext = /\.([a-z]+)$/i.exec(name)?.[1].toLowerCase() ?? 'txt';
  if (ext === 'pdf') {
    const box = (y: number) => ({ values: [72, y, 520, y + 30], coordinateSystem: 'pdf-points' });
    return [
      mk(0, { type: 'Heading', text: 'Capitolato tecnico', page: 1, bbox: box(700) }),
      mk(1, { text: 'La penale giornaliera è di 500 euro.', page: 1, bbox: box(650) }),
      mk(2, { text: 'Il collaudo avviene entro 30 giorni.', page: 2, bbox: box(700) }),
      mk(3, { text: 'Garanzia di 24 mesi sul software.', page: 2, bbox: box(640) }),
    ];
  }
  if (ext === 'docx') {
    return [
      mk(0, { type: 'Heading', text: 'Requisiti', headingPath: ['Requisiti'] }),
      mk(1, { text: 'Il sistema deve esportare i dati in CSV.', headingPath: ['Requisiti', 'Esportazione'] }),
      mk(2, { text: 'L’accesso richiede autenticazione.', headingPath: ['Requisiti', 'Sicurezza'] }),
    ];
  }
  if (ext === 'md' || ext === 'markdown') {
    return [
      mk(0, { type: 'Heading', text: 'Ambito', headingPath: ['Ambito'], lines: { start: 1, end: 1 } }),
      mk(1, { text: 'Il portale è per i clienti interni.', headingPath: ['Ambito'], lines: { start: 3, end: 3 } }),
      mk(2, { text: 'Fuori ambito: fatturazione.', headingPath: ['Ambito', 'Esclusioni'], lines: { start: 5, end: 6 } }),
    ];
  }
  return [
    mk(0, { text: 'Prima riga del documento.', lines: { start: 1, end: 1 } }),
    mk(1, { text: 'Seconda riga con una scadenza al 30 giugno.', lines: { start: 2, end: 2 } }),
  ];
}

export class FakeParsing {
  /** Esiti dei prossimi parsing (upload o retry), in ordine; esaurita la coda l'esito è `Ready`. */
  outcomes: ParseOutcome[] = [];
  /** Numero di GET dell'elenco prima che un job «Queued» mostri l'esito (0 = subito). */
  delayPolls = 0;
  readonly gens = new Map<string, FakeGen[]>();
  readonly retryReceipts = new Map<string, RetryReceipt>();
  /** Job di retry effettivamente creati (nuovi), per le asserzioni «un solo nuovo job». */
  retryJobsCreated = 0;
  private seq = 0;

  private nextOutcome(): ParseOutcome {
    return this.outcomes.shift() ?? 'Ready';
  }

  private newGen(v: VersionLike, retryOfId: string | null): FakeGen {
    const list = this.gens.get(v.id) ?? [];
    const outcome = this.nextOutcome();
    const n = ++this.seq;
    const withBlocks = outcome !== 'Failed';
    const blocks = withBlocks ? blocksFor(v.id, v.originalName) : null;
    const issues: Json[] =
      outcome === 'Incomplete'
        ? [{ code: 'image_not_extracted', severity: 'Blocking', page: 2, blockId: null, reason: 'Immagine a pagina 2 senza testo estratto', affectsCompleteness: true }]
        : [];
    const gen: FakeGen = {
      id: `g-${n}`,
      generation: list.length + 1,
      jobId: `j-${n}`,
      status: this.delayPolls > 0 ? 'Queued' : outcome,
      outcome,
      pollsLeft: this.delayPolls,
      blocks,
      issues,
      retryOfId,
      requestedAt: iso(n),
    };
    list.push(gen);
    this.gens.set(v.id, list);
    return gen;
  }

  onUpload(v: VersionLike): void {
    this.newGen(v, null);
  }

  /** Un giro di lettura dell'elenco: i job in coda avanzano verso l'esito (il worker del server, simulato). */
  tick(): void {
    for (const list of this.gens.values()) {
      for (const g of list) {
        if (g.status === 'Queued') {
          g.pollsLeft -= 1;
          if (g.pollsLeft <= 0) g.status = g.outcome;
        }
      }
    }
  }

  latest(versionId: string): FakeGen | undefined {
    return this.gens.get(versionId)?.at(-1);
  }

  status(versionId: string): GenStatus | 'Queued' {
    return this.latest(versionId)?.status ?? 'Queued';
  }

  /** Il retry è ammesso solo sull'ultima generazione terminale (Failed/Incomplete). */
  retryAllowed(versionId: string): boolean {
    const g = this.latest(versionId);
    return !!g && (g.status === 'Failed' || g.status === 'Incomplete');
  }

  issuesSummary(versionId: string): Json | null {
    const g = this.latest(versionId);
    if (!g || g.status === 'Ready' || g.status === 'Queued') return null;
    return { code: g.status === 'Failed' ? 'parse_failed' : 'parse_incomplete', message: 'La preparazione del documento non è riuscita' };
  }

  prepView(docId: string, v: VersionLike, g: FakeGen): Json {
    const isLatest = this.latest(v.id) === g;
    return {
      id: g.id,
      documentVersionId: v.id,
      documentId: docId,
      version: v.version,
      generation: g.generation,
      processingStatus: g.status,
      isReady: g.status === 'Ready',
      contentState: g.blocks === null || g.status === 'Queued' ? 'NotParsed' : g.status === 'Ready' ? 'Ready' : 'Parsed',
      blockCount: g.blocks && g.status !== 'Queued' ? g.blocks.length : null,
      retryOfId: g.retryOfId,
      requestedAt: g.requestedAt,
      issuesSummary: g.status === 'Failed' ? { code: 'parse_failed', message: 'La preparazione del documento non è riuscita' } : null,
      issues: g.status === 'Queued' ? [] : g.issues,
      allowedActions: isLatest && (g.status === 'Failed' || g.status === 'Incomplete') ? ['download', 'retry'] : ['download'],
      parseJobId: g.jobId,
    };
  }

  /** Gestisce le rotte del parsing; restituisce true se la richiesta è stata servita. `docs` risolve (documentId, versione). */
  async handle(
    rest: string,
    method: string,
    request: Request,
    key: string | null,
    docs: { findVersion: (docId: string, version: number) => { docId: string; v: VersionLike } | undefined; findByJob: (jobId: string) => { docId: string; v: VersionLike; gen: FakeGen } | undefined },
    r: Responder,
    route: Route,
  ): Promise<boolean> {
    const retry = /^\/document-jobs\/([^/]+)\/retry$/.exec(rest);
    if (retry && method === 'POST') {
      await this.retry(retry[1], key, docs, r);
      return true;
    }
    const m = /^\/documents\/([^/]+)\/versions\/(\d+)\/preparations\/([^/]+)(\/blocks|\/passages\/[^/]+)?$/.exec(rest);
    if (!m || method !== 'GET') return false;
    const found = docs.findVersion(m[1], Number(m[2]));
    if (!found) {
      await r.problem(404, 'not_found');
      return true;
    }
    const list = this.gens.get(found.v.id) ?? [];
    const sel = m[3];
    const gen = sel === 'latest' ? list.at(-1) : list.find((g) => g.id === sel || String(g.generation) === sel);
    if (!gen) {
      await r.problem(404, 'not_found');
      return true;
    }
    const sub = m[4] ?? '';
    if (sub === '') {
      await r.json(200, this.prepView(found.docId, found.v, gen));
      return true;
    }
    if (!gen.blocks || gen.status === 'Queued') {
      await r.problem(409, 'parsing_not_completed');
      return true;
    }
    if (sub === '/blocks') {
      await r.json(200, { preparationId: gen.id, processingStatus: gen.status, isReady: gen.status === 'Ready', items: gen.blocks, nextCursor: null });
      return true;
    }
    const blockId = decodeURIComponent(sub.slice('/passages/'.length));
    const block = gen.blocks.find((b) => b.id === blockId);
    if (!block) {
      await r.problem(404, 'not_found');
      return true;
    }
    const url = new URL(request.url());
    const s = url.searchParams.get('start');
    const e = url.searchParams.get('end');
    let quote: Json | null = null;
    if (s !== null && e !== null) {
      const start = Number(s);
      const end = Number(e);
      if (!(start >= 0 && end > start && end <= block.text.length)) {
        await r.problem(422, 'invalid_range');
        return true;
      }
      quote = { start, end, before: block.text.slice(Math.max(0, start - 20), start), text: block.text.slice(start, end), after: block.text.slice(end, end + 20) };
    }
    await r.json(200, {
      block,
      preparationId: gen.id,
      processingStatus: gen.status,
      isReady: gen.status === 'Ready',
      quote,
      original: { originalName: found.v.originalName, downloadUrl: `/api/documents/${found.docId}/versions/${found.v.version}/download` },
    });
    void route;
    return true;
  }

  private async retry(
    jobId: string,
    key: string | null,
    docs: { findByJob: (jobId: string) => { docId: string; v: VersionLike; gen: FakeGen } | undefined },
    r: Responder,
  ): Promise<void> {
    if (!key) return r.problem(400, 'idempotency_key_required');
    const known = this.retryReceipts.get(key);
    if (known) {
      if (known.fingerprint !== jobId) return r.problem(409, 'idempotency_conflict');
      return r.json(202, known.response);
    }
    const target = docs.findByJob(jobId);
    if (!target) return r.problem(404, 'not_found');
    const latest = this.latest(target.v.id)!;
    if (latest.status === 'Queued') return r.problem(409, 'job_active');
    if (latest !== target.gen) return r.problem(409, 'job_superseded');
    if (latest.status === 'Ready') return r.problem(409, 'job_not_retryable');
    let created: FakeGen | undefined;
    const save = () => {
      created = this.newGen(target.v, latest.id);
      this.retryJobsCreated += 1;
      this.retryReceipts.set(key, {
        fingerprint: jobId,
        response: {
          jobId: created.jobId,
          preparationId: created.id,
          generation: created.generation,
          retryOfJobId: latest.jobId,
          retryOfPreparationId: latest.id,
          status: 'Queued',
          requestedAt: created.requestedAt,
          deadlineAt: iso(3600),
        },
      });
    };
    if (await r.faulted('retryParse', save)) return;
    save();
    return r.json(202, this.retryReceipts.get(key)!.response);
  }
}
