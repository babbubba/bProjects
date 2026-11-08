import type { FakeGen, FakeParsing } from './fake-parsing';

// Ciclo di vita delle versioni nel fake-stack (T103, E2E-OFFLINE livello fake): bundle corrente per versione file, candidate (preparazioni Ready non correnti),
// rielaborazione (POST preparations) e promozione CAS (POST promote), con la stessa semantica HTTP di contracts/http-api.md. Rappresenta SOLO il contratto letto
// dal browser: concorrenza, atomicita e persistenza del server sono provate dal livello [E2E reale + LLM finto] e dai test backend (T093-T100).

type Json = Record<string, unknown>;

interface Bundle {
  currentGenId: string | null;
  revision: number;
  /** Preparazioni che sono state correnti: storiche (Superseded), mai piu candidate. */
  former: Set<string>;
}

export interface VersionRef {
  id: string;
  version: number;
  originalName: string;
}

type Responder = {
  json: (status: number, payload: unknown) => Promise<void>;
  problem: (status: number, code: string, extra?: Json) => Promise<void>;
};

const iso = (n: number) => new Date(Date.UTC(2030, 0, 1, 12, 0, n)).toISOString();

export class FakeLifecycle {
  private readonly bundles = new Map<string, Bundle>();
  /** Promozioni effettivamente eseguite (non i replay), per le asserzioni. */
  promotions: { versionId: string; from: string | null; to: string; revision: number }[] = [];
  reprocessJobsCreated = 0;
  private readonly receipts = new Map<string, { fingerprint: string; response: Json }>();

  constructor(private readonly parsing: FakeParsing) {}

  private bundle(versionId: string): Bundle {
    let b = this.bundles.get(versionId);
    if (!b) this.bundles.set(versionId, (b = { currentGenId: null, revision: 0, former: new Set() }));
    return b;
  }

  /** La prima preparazione completa di una versione file diventa corrente da sola (non una candidata). */
  private sync(versionId: string): Bundle {
    const b = this.bundle(versionId);
    if (b.currentGenId === null) {
      const first = (this.parsing.gens.get(versionId) ?? []).find((g) => g.status === 'Ready');
      if (first) {
        b.currentGenId = first.id;
        b.revision = 1;
        this.promotions.push({ versionId, from: null, to: first.id, revision: 1 });
      }
    }
    return b;
  }

  private candidates(versionId: string): FakeGen[] {
    const b = this.sync(versionId);
    return (this.parsing.gens.get(versionId) ?? []).filter((g) => g.status === 'Ready' && g.id !== b.currentGenId && !b.former.has(g.id));
  }

  /** Campi della VersionView che dipendono dal bundle. */
  view(versionId: string): Json {
    const b = this.sync(versionId);
    return {
      currentPreparationId: b.currentGenId,
      currentEvidenceVersionId: b.currentGenId ? `ev-${b.currentGenId}` : null,
      bundleRevision: b.revision,
      candidatePreparations: this.candidates(versionId).map((g) => ({
        preparationId: g.id, generation: g.generation, processingStatus: 'Ready', evidenceVersionId: `ev-${g.id}`, createdAt: g.requestedAt, canPromote: true,
      })),
    };
  }

  /** Gestisce POST .../preparations (rielaborazione) e POST .../preparations/{sel}/promote; true se servita. */
  async handle(
    rest: string,
    method: string,
    key: string | null,
    body: unknown,
    docs: { findVersion: (docId: string, version: number) => { docId: string; v: VersionRef } | undefined },
    r: Responder,
  ): Promise<boolean> {
    const m = /^\/documents\/([^/]+)\/versions\/(\d+)\/preparations(?:\/([^/]+)\/promote)?$/.exec(rest);
    if (!m || method !== 'POST') return false;
    const found = docs.findVersion(m[1], Number(m[2]));
    if (!found) {
      await r.problem(404, 'not_found');
      return true;
    }
    const v = found.v;
    const b = this.sync(v.id);
    const gens = this.parsing.gens.get(v.id) ?? [];

    if (m[3] === undefined) {
      // --- rielaborazione candidata
      if (!key) {
        await r.problem(400, 'idempotency_key_required');
        return true;
      }
      const known = this.receipts.get(key);
      if (known) {
        if (known.fingerprint !== v.id) await r.problem(409, 'idempotency_conflict');
        else await r.json(202, known.response);
        return true;
      }
      if (gens.some((g) => g.status === 'Queued')) {
        await r.problem(409, 'preparation_active');
        return true;
      }
      const created = this.parsing.createGeneration(v, gens.at(-1)?.id ?? null);
      this.reprocessJobsCreated += 1;
      const response = {
        jobId: created.jobId, preparationId: created.id, generation: created.generation, retryOfJobId: gens.at(-1)?.jobId ?? null,
        retryOfPreparationId: gens.at(-1)?.id ?? null, status: 'Queued', requestedAt: created.requestedAt, deadlineAt: iso(3600),
      };
      this.receipts.set(key, { fingerprint: v.id, response });
      await r.json(202, response);
      return true;
    }

    // --- promozione CAS
    const gen = gens.find((g) => g.id === m[3] || String(g.generation) === m[3]);
    if (!gen) {
      await r.problem(404, 'not_found');
      return true;
    }
    const expected = (body as { expectedRevision?: unknown } | null)?.expectedRevision;
    const result = () => ({
      documentId: found.docId, documentVersionId: v.id, bundleRevision: b.revision, currentPreparationId: b.currentGenId,
      currentEvidenceVersionId: b.currentGenId ? `ev-${b.currentGenId}` : null, generation: gen.generation, promotion: { fromPreparationId: null, toPreparationId: gen.id },
    });
    if (typeof expected !== 'number') {
      await r.problem(400, 'expected_revision_required');
    } else if (b.currentGenId === gen.id) {
      await r.json(200, result());                                                   // replay: gia corrente
    } else if (expected !== b.revision) {
      await r.problem(409, 'promotion_conflict', { currentRevision: b.revision });
    } else if (gen.status !== 'Ready') {
      await r.problem(422, 'preparation_not_promotable');
    } else {
      if (b.currentGenId) b.former.add(b.currentGenId);
      this.promotions.push({ versionId: v.id, from: b.currentGenId, to: gen.id, revision: b.revision + 1 });
      b.currentGenId = gen.id;
      b.revision += 1;
      await r.json(200, result());
    }
    return true;
  }
}
