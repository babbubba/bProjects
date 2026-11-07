import { randomUUID } from 'node:crypto';
import type { Page } from '@playwright/test';
import type { DocSource, FlowStack } from './fake-document-flow';
import { executionOf } from './fake-document-flow';

// Documenti oltre contesto (T092, E2E-OFFLINE): estensioni del fake-stack per elaborazioni multipasso, report di copertura e bozze con
// fonti multi-blocco. Rappresentano SOLO il contratto HTTP letto dal browser (ExecutionView, CoverageReportView, DraftView.sourceMap):
// nessun server, parser o modello. Le garanzie del server (scansione di tutte le unita, riduzione, fail-fast, ripresa) sono provate da
// backend/tests/bProjects.IntegrationTests (DocumentLongChatTests, T089, T091).

type Json = Record<string, unknown>;

/** `execution` di un root multipasso: il piano cresce con gli output intermedi, le unita sono contatori del server, mai una percentuale. */
export function multipassExecution(o: {
  stage: string; completed: number | null; planned: number | null; elapsedMs: number; regime?: 'Ordinary' | 'Extended';
  initialRegime?: 'Ordinary' | 'Extended'; promotionReason?: string | null;
}): Json {
  const regime = o.regime ?? 'Extended';
  const initialRegime = o.initialRegime ?? regime;
  const base = executionOf({ regime, reason: regime === 'Extended' ? 'multipass_or_capacity' : 'single_call_fit' });
  const promoted = initialRegime !== regime;
  return {
    ...base,
    initialRegime,
    elapsedMs: o.elapsedMs,
    stage: o.stage,
    completedUnits: o.completed,
    plannedUnits: o.planned,
    promotedAt: promoted ? '2030-01-01T10:03:10Z' : null,
    promotionReason: promoted ? (o.promotionReason ?? 'additional_work') : null,
    promotionReceiptHash: promoted ? 'cd'.repeat(32) : null,
    ordinaryDeadlineAt: initialRegime === 'Ordinary' ? '2030-01-01T10:03:00Z' : null,
    effectiveDeadlineAt: regime === 'Ordinary' ? '2030-01-01T10:03:00Z' : '2030-01-01T11:00:00Z',
  };
}

/** Imposta l'ultima richiesta del monitor con la telemetria di esecuzione data (il browser la rilegge a ogni evento del progetto). */
export function setExecution(fake: FlowStack, execution: Json, projectId = 'p-1'): void {
  const usage = fake.project(projectId).usage;
  if (!usage) throw new Error('usage non inizializzato');
  (usage['last'] as Json)['execution'] = execution;
}

/** Evidenze in coda per i comandi del registro: nessun messaggio, nessuna estrazione, ma le evidenze documentali correnti. */
export async function installEvidenceRoutes(
  page: Page,
  current: () => Json[],
  coverage: { onCreate: () => { status: number; body: Json }; stored: Json[] },
  projectId = 'p-1',
): Promise<void> {
  await page.route(/\/api\//, async (route) => {
    const url = new URL(route.request().url());
    const method = route.request().method();
    const p = url.pathname;
    const json = (status: number, body: unknown) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    if (method === 'GET' && p === `/api/projects/${projectId}/evidence/status`) {
      return json(200, { messages: [], queue: { queued: 0, running: 0, failed: 0 } });
    }
    if (method === 'GET' && p === `/api/projects/${projectId}/evidence`) {
      return json(200, { items: current(), nextCursor: null });
    }
    if (/\/analyses\/[^/]+\/evidence-selection$/.test(p)) {
      return json(404, { status: 404, code: 'selection_not_available', title: 'selection_not_available' });
    }
    if (/\/analyses\/[^/]+\/coverage-reports$/.test(p)) {
      if (method === 'GET') {
        return json(200, { items: coverage.stored.map((r) => ({ ...r, findings: undefined, limits: undefined, summary: undefined, reconciliation: undefined })) });
      }
      if (method === 'POST') {
        const result = coverage.onCreate();
        if (result.status === 201) coverage.stored.push(result.body);
        return json(result.status, result.body);
      }
    }
    const one = /\/analyses\/[^/]+\/coverage-reports\/([^/]+)$/.exec(p);
    if (one && method === 'GET') {
      const found = coverage.stored.find((r) => r['id'] === decodeURIComponent(one[1]));
      return found ? json(200, found) : json(404, { status: 404, code: 'not_found', title: 'not_found' });
    }
    return route.fallback();
  });
}

export interface ReportOptions {
  id: string;
  analysisId: string;
  revisionId: string;
  draftId?: string | null;
  expected: number;
  represented: number;
  portionsExpected: number;
  portionsProcessed: number;
  blocking?: number;
  findings?: Json[];
  reconciliation?: Json;
}

/** Report coverage-2 come lo serializza il server: sintesi per ID e per porzioni, mai un booleano di completezza. */
export function coverageReport(o: ReportOptions): Json {
  const findings = o.findings ?? [];
  const count = (c: string) => findings.filter((f) => f['category'] === c).length;
  return {
    id: o.id, analysisId: o.analysisId, revisionId: o.revisionId, draftId: o.draftId ?? null, checkerVersion: 'coverage-2',
    createdAt: '2030-01-01T10:09:00Z', inputHash: 'f'.repeat(64), createdBy: 'pm',
    counts: { deterministicErrors: count('DeterministicError'), semanticRisks: count('SemanticRisk'), pmAssessments: count('PmAssessment') },
    findings,
    limits: ['La copertura per ID non verifica il significato del testo.', 'Nessun controllo semantico automatico sulle fonti.'],
    summary: {
      expectedEvidence: o.expected, representedEvidence: o.represented, portionsExpected: o.portionsExpected, portionsProcessed: o.portionsProcessed,
      portionsNoEvidence: 0, portionsMissing: o.portionsExpected - o.portionsProcessed, blockingFindings: o.blocking ?? 0,
    },
    ...(o.reconciliation ? { reconciliation: o.reconciliation } : {}),
  };
}

/** Voci della mappa persistita: due citazioni che condividono la prima fonte, una di esse su due blocchi distinti. */
export function twoGroupSourceMap(sources: DocSource[], itemId = 'req-1'): { map: Json[]; shared: DocSource; groups: DocSource[][] } {
  const [a, b, c] = sources;
  const evidenceId = 'ev-1';
  const g1 = randomUUID();
  const g2 = randomUUID();
  const entry = (s: DocSource, group: string, part: number): Json => ({ itemId, evidenceId, citationGroupId: group, part, sourceId: s.sourceId });
  return { map: [entry(a, g1, 0), entry(b, g1, 1), entry(a, g2, 0), entry(c, g2, 1)], shared: a, groups: [[a, b], [a, c]] };
}
