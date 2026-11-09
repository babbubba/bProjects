import { randomUUID } from 'node:crypto';
import type { Page } from '@playwright/test';
import { FakeStack, type ProjectState } from './fake-stack';
import type { FakeBlock } from './fake-parsing';

// Percorso piccolo US1 (T066, E2E-OFFLINE): estensioni del fake-stack per evidenze documentali, analisi 1.1.0, bozza con fonti e telemetria
// di esecuzione. Rappresentano SOLO il contratto HTTP letto dal browser: nessun parser, nessun modello, nessun server. Il livello che prova
// il server (classificazione Pending -> Classified, deadline dal t0, validazione delle citazioni, falsi Completed) e
// backend/tests/bProjects.IntegrationTests/DocumentSmallFlowTests.cs.

type Json = Record<string, unknown>;

/** Come FakeStack, ma le analisi 1.1.0 dichiarano lo schema del contenuto e i flag di fonti cambiate (se richiesti dalla prova). */
export class FlowStack extends FakeStack {
  /** Imposta `hasSourceChanges` nelle viste di analisi (il backend non lo espone ancora: la UI lo legge se presente). */
  sourceChanges = false;

  override analysisView(p: ProjectState): Json {
    const view = super.analysisView(p);
    const content = view['content'] as Json;
    if (content['schemaVersion'] === '1.1.0') {
      view['schemaVersion'] = '1.1.0';
      view['hasSourceChanges'] = this.sourceChanges;
    }
    return view;
  }
}

export interface DocSource {
  sourceId: string;
  documentId: string;
  documentVersionId: string;
  preparationId: string;
  blockId: string;
  artifactId: string;
  start: number;
  end: number;
  quote: string;
  citationGroupId: string;
  documentName: string;
  statement: string;
}

/** Fonti documentali (una per blocco paragrafo con testo non banale) di tutte le versioni Ready del progetto, dai blocchi del parsing simulato. */
export function documentSources(fake: FlowStack, projectId = 'p-1'): DocSource[] {
  const p = fake.project(projectId);
  const out: DocSource[] = [];
  for (const d of p.documents) {
    const v = d.versions.at(-1)!;
    const gen = fake.parsing?.latest(v.id);
    if (!gen || gen.status !== 'Ready' || !gen.blocks) continue;
    for (const b of gen.blocks as FakeBlock[]) {
      if (b.type !== 'Paragraph' || b.text.split(/\s+/).length < 4) continue;
      out.push({
        sourceId: randomUUID(), documentId: d.id, documentVersionId: v.id, preparationId: gen.id, blockId: b.id, artifactId: `a-${v.id}`,
        start: 0, end: b.text.length, quote: b.text, citationGroupId: randomUUID(), documentName: v.originalName, statement: b.text,
      });
    }
  }
  return out;
}

/** Busta di GET /evidence?originKind=Document&viewVersion=1.1.0 (le evidenze documentali correnti, una per fonte). */
export function evidenceEnvelopes(sources: DocSource[], projectId = 'p-1'): Json[] {
  return sources.map((s, i) => ({
    evidenceViewVersion: '1.1.0',
    evidence: {
      id: `ev-${i + 1}`, versionId: 'ver-1', version: 1, isCurrent: true, projectId, ordinal: i + 1, key: `e${i + 1}`, kind: 'Requirement',
      polarity: 'Affirmed', certainty: 'Declared', statement: s.statement,
      origin: { kind: 'Document', documentId: s.documentId, documentVersionId: s.documentVersionId, preparationId: s.preparationId, originalName: s.documentName },
      content: {
        schemaVersion: 'evidence-1.1.0', kindRationale: null,
        structure: { subject: null, action: null, object: null, scope: null, purpose: null, instrument: null, channel: null, recipient: null },
        qualifiers: [],
        sources: [{
          sourceId: s.sourceId, kind: 'DocumentPassage', role: 'Primary', citationGroupId: s.citationGroupId, part: 0, documentId: s.documentId,
          documentVersionId: s.documentVersionId, preparationId: s.preparationId, artifactId: s.artifactId, blockId: s.blockId, start: s.start, end: s.end, quote: s.quote,
        }],
      },
    },
  }));
}

/**
 * Instrada le sole rotte che il fake-stack non conosce (evidenze documentali, passaggi per id versione); il resto passa al fake-stack.
 * `current` restituisce le fonti correnti al momento della richiesta (stesse della revisione servita al browser).
 */
export async function installFlowRoutes(page: Page, fake: FlowStack, current: () => DocSource[], projectId = 'p-1'): Promise<void> {
  await page.route(/\/api\//, async (route) => {
    const url = new URL(route.request().url());
    if (route.request().method() === 'GET' && url.pathname === `/api/projects/${projectId}/evidence` && url.searchParams.get('originKind') === 'Document') {
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ items: evidenceEnvelopes(current(), projectId), nextCursor: null }),
      });
    }
    // Il browser cita una versione per id (documentVersionId): il fake-stack del parsing conosce solo il numero di versione.
    const byId = /^(\/api\/projects\/[^/]+\/documents\/[^/]+\/versions\/)([^/]+)(\/preparations\/.*)$/.exec(url.pathname);
    if (byId && !/^\d+$/.test(byId[2])) {
      for (const d of fake.project(projectId).documents) {
        const v = d.versions.find((x) => x.id === decodeURIComponent(byId[2]));
        if (v) return route.fallback({ url: `${url.origin}${byId[1]}${v.version}${byId[3]}${url.search}` });
      }
    }
    return route.fallback();
  });
}

/** Contenuto AnalysisContent 1.1.0 come lo produrrebbe il server: un obiettivo del PM e un requisito Documented per ogni fonte documentale. */
export function documentAnalysisContent(fake: FlowStack, runId: string, sources: DocSource[], projectId = 'p-1'): Json {
  const message = fake.project(projectId).messages.find((m) => m['role'] === 'user');
  const sMessage = randomUUID();
  return {
    schemaVersion: '1.1.0', analysisId: runId, projectId, sourceCutoffSequence: fake.project(projectId).messages.length, validationState: 'Unvalidated',
    sources: [
      ...(message ? [{ id: sMessage, kind: 'Message', messageId: message['id'], messageRole: 'user', revisionId: null }] : []),
      ...sources.map((s) => ({
        id: s.sourceId, kind: 'DocumentPassage', messageId: null, messageRole: null, revisionId: null, documentId: s.documentId, documentVersionId: s.documentVersionId,
        preparationId: s.preparationId, artifactId: s.artifactId, blockId: s.blockId, start: s.start, end: s.end, quote: s.quote,
      })),
    ],
    goals: message ? [{ id: randomUUID(), text: 'Rispondere alle domande sui documenti del progetto', classification: 'Declared', evidenceRefs: [sMessage] }] : [],
    contextAndScope: { overview: [], included: [], excluded: [] },
    requirements: sources.map((s) => ({ id: randomUUID(), text: s.statement, classification: 'Documented', evidenceRefs: [s.sourceId] })),
    operationalFlows: [], constraints: [], dependencies: [], assumptions: [], openQuestions: [], conflicts: [],
  };
}

/** Il server ha completato Analizza: revisione AI 1.1.0 non validata, run Completed e notifica SSE. */
export async function completeDocumentAnalysis(page: Page, fake: FlowStack, runId: string, sources: DocSource[], projectId = 'p-1'): Promise<Json> {
  const p = fake.project(projectId);
  p.analysisId = runId;
  p.revisions = [{ revisionId: 'rev-1', origin: 'Ai', content: documentAnalysisContent(fake, runId, sources, projectId), validationState: 'Unvalidated' }];
  const run = fake.setRun(projectId, runId, { status: 'Completed', completedAt: '2030-01-01T10:06:00Z', analysisId: runId, resultRevisionId: 'rev-1' });
  await fake.send(page, 'processing.state', run);
  return run;
}

/** Il server ha completato Genera: bozza Markdown che cita le fonti della revisione confermata, mai approvata. */
export async function completeDocumentDraft(page: Page, fake: FlowStack, runId: string, projectId = 'p-1'): Promise<Json> {
  const p = fake.project(projectId);
  const revision = p.revisions.at(-1)!;
  const content = revision['content'] as { sources: { id: string; kind: string }[] };
  const cited = content.sources.filter((s) => s.kind === 'DocumentPassage').map((s) => `[fonte:${s.id}]`).join(' ');
  p.draft = {
    id: 'd-1', projectId, processingRunId: runId, analysisId: p.analysisId, analysisRevisionId: revision['revisionId'], analysisVersion: p.revisions.length,
    markdown: `# Requisiti confermati\n\n- Requisiti dalle fonti documentali ${cited}\n`, createdAt: '2030-01-01T10:08:00Z', status: 'Proposed', approvalStatus: 'NotApproved',
    hasNewNotes: false, hasNewRevision: false, label: 'Bozza AI — da revisionare',
  };
  const run = fake.setRun(projectId, runId, { status: 'Completed', completedAt: '2030-01-01T10:08:00Z', draftId: 'd-1' });
  await fake.send(page, 'processing.state', run);
  return run;
}

/** `execution` di un root documentale classificato dal preflight (stesso contratto di ExecutionView). */
export function executionOf(o: { regime: 'Ordinary' | 'Extended'; reason?: string | null; receipt?: string; requestedAt?: string }): Json {
  const t0 = o.requestedAt ?? '2030-01-01T10:00:00Z';
  const at = (s: number) => new Date(Date.parse(t0) + s * 1000).toISOString();
  const ordinary = o.regime === 'Ordinary';
  return {
    initialRegime: o.regime, regime: o.regime, classificationStatus: 'Classified', classificationVersion: 'document-regime-v2',
    classificationRecordHash: o.receipt ?? 'a'.repeat(64), reason: o.reason ?? null, requestedAt: t0,
    ordinaryDeadlineAt: ordinary ? at(180) : null, hardDeadlineAt: at(3600), effectiveDeadlineAt: ordinary ? at(180) : at(3600),
    promotedAt: null, promotionReason: null, promotionReceiptHash: null, elapsedMs: 4200, queueMs: 1300, processingMs: 1800, preflightMs: 150,
    backoffMs: 0, recoveryMs: null, stage: null, completedUnits: null, plannedUnits: null,
  };
}
