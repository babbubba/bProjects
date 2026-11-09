import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

// Fixture deterministiche della slice 002 per le prove browser (T001): builder delle risposte document-parse-1 / evidence-1.1.0,
// loader del corpus sintetico (tests/fixtures/documents) e clock controllabile. Nessuna rete, nessun segreto, nessun modello reale;
// riusa il fake-stack esistente (fake-stack.ts) senza duplicarlo.

type Json = Record<string, unknown>;

export const documentFixturesDir = join(__dirname, '..', '..', 'fixtures', 'documents');

export const sha256 = (data: string | Buffer) => createHash('sha256').update(data).digest('hex');

export interface CorpusFile {
  id: string;
  path: string;
  format: 'pdf' | 'docx' | 'md' | 'txt';
  mediaType: string;
  byteLength: number;
  sha256: string;
  license: string;
  expected: { parse: 'Complete' | 'Incomplete' | 'Rejected'; [key: string]: unknown };
}

export interface CorpusManifest {
  schemaVersion: string;
  files: CorpusFile[];
  facts: Json[];
  conflicts: Json[];
  classifications: Json[];
}

export function loadCorpus(): CorpusManifest {
  return JSON.parse(readFileSync(join(documentFixturesDir, 'manifest.json'), 'utf-8')) as CorpusManifest;
}

export function corpusBytes(file: CorpusFile): Buffer {
  return readFileSync(join(documentFixturesDir, file.path));
}

/** Risposta document-parse-1 valida; l'inventario rappresenta ogni blocco e un'issue che incide sulla completezza rende il manifest Incomplete. */
export function parseManifest(options: {
  format?: string;
  pageCount?: number | null;
  originalSha256?: string;
  blocks?: { ref: string; type: string; text: string; page?: number | null }[];
  issues?: { code: string; blocking: boolean; affectsCompleteness: boolean; reason?: string }[];
} = {}): Json {
  const blocks = (options.blocks ?? []).map((b, order) => ({
    ref: b.ref,
    order,
    parentRef: null,
    type: b.type,
    text: b.text,
    headingPath: [],
    locator: { page: b.page ?? null, bbox: null, coordinateSystem: null },
  }));
  const issues = (options.issues ?? []).map((i) => ({
    code: i.code,
    severity: i.blocking ? 'blocking' : 'warning',
    reason: i.reason ?? 'elemento non trattato',
    affectsCompleteness: i.affectsCompleteness,
  }));
  return {
    schemaVersion: 'document-parse-1',
    originalSha256: options.originalSha256 ?? 'a'.repeat(64),
    parserVersion: 'docling-test',
    dependencyLockHash: 'b'.repeat(64),
    modelArtifactsHash: 'c'.repeat(64),
    configHash: 'd'.repeat(64),
    status: issues.some((i) => i.affectsCompleteness) ? 'Incomplete' : 'Complete',
    originFormat: options.format ?? 'pdf',
    originalByteLength: 1,
    pageCount: options.pageCount === undefined ? 1 : options.pageCount,
    document: { serialization: 'test-1' },
    blocks,
    inventory: blocks.map((b) => ({ item: b.ref, state: 'represented', blockRefs: [b.ref], issueRefs: [] })),
    chunks: [],
    issues,
  };
}

/** Output di unità evidence-1.1.0: tutte le proprietà obbligatorie; zero evidenze richiede una motivazione. */
export function evidenceOutput(
  evidences: { key: string; rangeKey: string; quote: string; statement: string; kind?: string; polarity?: string; certainty?: string }[],
  noEvidenceReason: string | null = null,
): Json {
  return {
    schemaVersion: 'evidence-1.1.0',
    evidences: evidences.map((e) => ({
      key: e.key,
      sources: [{ kind: 'DocumentPassage', rangeKey: e.rangeKey, quote: e.quote, occurrence: 1, role: 'Primary', citationGroupKey: `g-${e.key}`, part: 0 }],
      kind: e.kind ?? 'Requirement',
      kindRationale: null,
      statement: e.statement,
      subject: null,
      action: null,
      object: null,
      scope: null,
      purpose: null,
      instrument: null,
      channel: null,
      recipient: null,
      qualifiers: [],
      polarity: e.polarity ?? 'Affirmed',
      certainty: e.certainty ?? 'Declared',
      relations: [],
    })),
    noEvidenceReason: evidences.length === 0 ? noEvidenceReason : null,
  };
}

/** Clock controllabile: il tempo avanza solo quando lo decide il test. */
export class ManualClock {
  constructor(private nowMs = Date.UTC(2030, 0, 1, 10, 0, 0)) {}
  now(): Date {
    return new Date(this.nowMs);
  }
  advance(seconds: number): void {
    this.nowMs += seconds * 1000;
  }
}

/** Contatore degli invii al modello finto: una prova che non deve inviare lo vede a zero. */
export class SendCounter {
  count = 0;
  record(): number {
    return ++this.count;
  }
}
