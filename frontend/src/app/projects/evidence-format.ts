import { HttpErrorResponse } from '@angular/common/http';
import {
  DocumentEvidenceSource,
  DocumentEvidenceView,
  EvidenceEnvelope,
  EvidenceQualifier,
  EvidenceView,
  SourceDocumentView,
} from './evidence-api.service';

/** Segmento di un testo originale: `role` non nullo = passo citato da un'evidenza (da rendere con <mark>). */
export interface TextSegment {
  text: string;
  role: 'Primary' | 'Context' | 'Referent' | null;
}

export interface TextSpan {
  /** Indice UTF-16 incluso. */
  start: number;
  /** Indice UTF-16 escluso. */
  end: number;
  role?: string;
}

const roleRank = { Primary: 3, Context: 2, Referent: 1 } as const;
type SpanRole = keyof typeof roleRank;
const asRole = (role: string | undefined): SpanRole =>
  role === 'Context' || role === 'Referent' ? role : 'Primary';

/**
 * Divide il testo originale in segmenti, evidenziando i passi citati. Gli offset sono UTF-16 come gli indici delle
 * stringhe JavaScript, quindi `slice(start, end)` coincide con la citazione anche dopo emoji (coppie surrogate).
 * Span fuori intervallo vengono tagliati, quelli vuoti o non numerici scartati; dove più span si sovrappongono prevale
 * il ruolo più forte (Primary > Context > Referent). Il risultato contiene solo porzioni del testo: va reso con
 * interpolazione, mai con innerHTML.
 */
export function highlightSegments(text: string, spans: readonly TextSpan[]): TextSegment[] {
  if (!text) return [];
  const valid = spans
    .filter((s) => Number.isFinite(s.start) && Number.isFinite(s.end))
    .map((s) => ({
      start: Math.max(0, Math.trunc(s.start)),
      end: Math.min(text.length, Math.trunc(s.end)),
      role: asRole(s.role),
    }))
    .filter((s) => s.end > s.start);
  const points = [...new Set([0, text.length, ...valid.flatMap((s) => [s.start, s.end])])].sort(
    (a, b) => a - b,
  );
  const out: TextSegment[] = [];
  for (let i = 0; i + 1 < points.length; i++) {
    const from = points[i];
    const to = points[i + 1];
    let role: SpanRole | null = null;
    for (const s of valid) {
      if (s.start <= from && s.end >= to && (!role || roleRank[s.role] > roleRank[role])) {
        role = s.role;
      }
    }
    const piece = text.slice(from, to);
    const last = out[out.length - 1];
    if (last && last.role === role) last.text += piece;
    else out.push({ text: piece, role });
  }
  return out;
}

export interface StateBadge {
  label: string;
  /** Classi Bootstrap del badge; il significato è sempre nel testo, mai solo nel colore. */
  cls: string;
}

const badges: Record<string, StateBadge> = {
  none: { label: 'Non analizzato', cls: 'text-bg-light border' },
  pending: { label: 'In coda', cls: 'text-bg-info' },
  running: { label: 'In elaborazione', cls: 'text-bg-primary' },
  extracted: { label: 'Estratto', cls: 'text-bg-success' },
  empty: { label: 'Nessuna evidenza', cls: 'text-bg-secondary' },
  failed: { label: 'Non riuscito', cls: 'text-bg-danger' },
};

/** Badge dello stato di estrazione di un messaggio (`state` di EvidenceStatusView). */
export function stateBadge(state: string | null | undefined): StateBadge {
  return (state && badges[state]) || { label: state || 'Sconosciuto', cls: 'text-bg-light border' };
}

export const kindLabels: Record<string, string> = {
  Goal: 'Obiettivo',
  Context: 'Contesto',
  Requirement: 'Requisito',
  Action: 'Azione / flusso',
  Constraint: 'Vincolo',
  Exclusion: 'Esclusione',
  Dependency: 'Dipendenza',
  Assumption: 'Assunzione',
  Gap: 'Lacuna',
  Alternative: 'Alternativa',
  Decision: 'Decisione',
  Other: 'Altro',
};

export const polarityLabels: Record<string, string> = {
  Affirmed: 'Affermata',
  Negated: 'Negata',
  Excluded: 'Esclusa',
  Prohibited: 'Vietata',
  Undefined: 'Non definita',
};

export const certaintyLabels: Record<string, string> = {
  Declared: 'Dichiarata dal PM',
  Inferred: 'Inferita',
  Hypothesized: 'Ipotizzata',
  ToClarify: 'Da chiarire',
};

const qualifierLabels: Record<string, string> = {
  Quantity: 'Quantità',
  Deadline: 'Scadenza',
  Duration: 'Durata',
  Frequency: 'Frequenza',
  Minimum: 'Minimo',
  Maximum: 'Massimo',
  Condition: 'Condizione',
  Exception: 'Eccezione',
  Obligation: 'Obbligatorietà',
  Optionality: 'Facoltatività',
  Exclusivity: 'Esclusività',
  Timing: 'Tempistica',
  Other: 'Altro',
};

const relationLabels: Record<string, string> = {
  Qualifies: 'qualifica',
  Confirms: 'conferma',
  Contradicts: 'contraddice',
  Corrects: 'rettifica',
  Supersedes: 'sostituisce',
  DependsOn: 'dipende da',
  AlternativeTo: 'è alternativa a',
  SameRule: 'appartiene alla stessa regola di',
};

const sourceRoleLabels: Record<string, string> = {
  Primary: 'Passo citato',
  Context: 'Contesto precedente',
  Referent: 'Referente (assistente, solo contesto)',
};

export const diffStatusLabels: Record<string, string> = {
  Added: 'Aggiunta',
  Removed: 'Rimossa',
  Unchanged: 'Invariata',
  Modified: 'Modificata',
  Ambiguous: 'Ambigua',
};

const diffFieldLabels: Record<string, string> = {
  kind: 'Tipologia',
  polarity: 'Polarità',
  certainty: 'Certezza',
  statement: 'Enunciato',
  subject: 'Soggetto',
  action: 'Azione',
  object: 'Oggetto',
  scope: 'Ambito',
  purpose: 'Scopo',
  instrument: 'Strumento',
  channel: 'Canale',
  recipient: 'Destinatario',
  qualifiers: 'Qualificatori',
  sources: 'Fonti',
  relations: 'Relazioni',
};

export const categoryLabels: Record<string, string> = {
  DeterministicError: 'Errore deterministico',
  SemanticRisk: 'Rischio stimato',
  PmAssessment: 'Valutazione PM',
};

const categoryHints: Record<string, string> = {
  DeterministicError: 'Verificabile formalmente: riferimento, fonte, intervallo o regola.',
  SemanticRisk: 'Stima che interpreta il linguaggio naturale: può essere imprecisa, va verificata.',
  PmAssessment: 'Effetto di una decisione del PM nella revisione: da valutare, non un errore.',
};

const confidenceLabels: Record<string, string> = { low: 'bassa', medium: 'media' };

export const labelOf = (map: Record<string, string>, key: string | null | undefined): string =>
  (key && map[key]) || key || '';
export const kindLabel = (k: string | null | undefined) => labelOf(kindLabels, k);
export const polarityLabel = (k: string | null | undefined) => labelOf(polarityLabels, k);
export const certaintyLabel = (k: string | null | undefined) => labelOf(certaintyLabels, k);
export const qualifierLabel = (k: string | null | undefined) => labelOf(qualifierLabels, k);
export const sourceRoleLabel = (k: string | null | undefined) => labelOf(sourceRoleLabels, k);
export const diffStatusLabel = (k: string | null | undefined) => labelOf(diffStatusLabels, k);
export const diffFieldLabel = (k: string | null | undefined) => labelOf(diffFieldLabels, k);
export const categoryLabel = (k: string | null | undefined) => labelOf(categoryLabels, k);
export const categoryHint = (k: string | null | undefined) => labelOf(categoryHints, k);
export const confidenceLabel = (k: string | null | undefined) => labelOf(confidenceLabels, k);
export const relationLabel = (k: string | null | undefined) => labelOf(relationLabels, k);

/** Campi strutturali non nulli di un'evidenza, nell'ordine di lettura; i campi assenti non si mostrano. */
export function structureFields(
  e: Pick<
    EvidenceView,
    'subject' | 'action' | 'object' | 'scope' | 'purpose' | 'instrument' | 'channel' | 'recipient'
  >,
): { label: string; value: string }[] {
  const all: [string, string | null][] = [
    ['Soggetto', e.subject],
    ['Azione', e.action],
    ['Oggetto', e.object],
    ['Ambito', e.scope],
    ['Scopo', e.purpose],
    ['Strumento', e.instrument],
    ['Canale', e.channel],
    ['Destinatario', e.recipient],
  ];
  return all.filter(([, v]) => !!v).map(([label, value]) => ({ label, value: value! }));
}

/** Discrimina l'evidenza documentale (1.1.0, ha `origin`) da quella di messaggio (1.0.0). */
export const isDocumentEvidence = (
  e: EvidenceView | DocumentEvidenceView,
): e is DocumentEvidenceView => 'origin' in e && e.origin?.kind === 'Document';

/** Parti di una citazione documentale, ordinate: le citazioni multi-blocco restano parti separate, mai fuse. */
export interface SourceGroup {
  citationGroupId: string;
  parts: { source: DocumentEvidenceSource; resolved: SourceDocumentView | null }[];
}

/** Riga comune di lista: un solo modello per le due origini, con i campi propri di ciascuna già discriminati. */
export interface EvidenceRow {
  id: string;
  origin: 'Message' | 'Document';
  kind: string;
  polarity: string;
  certainty: string;
  statement: string;
  fields: { label: string; value: string }[];
  qualifiers: EvidenceQualifier[];
  /** Solo origine messaggio: numero del messaggio; nessun MessageId fittizio per i documenti. */
  messageSequence: number | null;
  groups: SourceGroup[];
  document: { documentId: string; documentVersionId: string; preparationId: string } | null;
}

export function sourceGroups(
  sources: readonly DocumentEvidenceSource[],
  resolved: readonly SourceDocumentView[] = [],
): SourceGroup[] {
  const bySource = new Map(resolved.map((r) => [r.sourceId, r]));
  const groups = new Map<string, SourceGroup>();
  for (const s of sources) {
    const g = groups.get(s.citationGroupId) ?? { citationGroupId: s.citationGroupId, parts: [] };
    g.parts.push({ source: s, resolved: bySource.get(s.sourceId) ?? null });
    groups.set(s.citationGroupId, g);
  }
  return [...groups.values()].map((g) => ({
    ...g,
    parts: [...g.parts].sort((a, b) => a.source.part - b.source.part),
  }));
}

/** Righe di lista per un elenco misto; gli envelope di versione ignota non si deformano: restano fuori e vengono contati. */
export function evidenceRows(items: readonly EvidenceEnvelope[]): {
  rows: EvidenceRow[];
  unsupported: number;
} {
  const rows: EvidenceRow[] = [];
  let unsupported = 0;
  for (const env of items) {
    if (env.evidenceViewVersion === '1.0.0') {
      const e = env.evidence;
      rows.push({
        id: e.id,
        origin: 'Message',
        kind: e.kind,
        polarity: e.polarity,
        certainty: e.certainty,
        statement: e.statement,
        fields: structureFields(e),
        qualifiers: e.qualifiers,
        messageSequence: e.messageSequence,
        groups: [],
        document: null,
      });
    } else if (env.evidenceViewVersion === '1.1.0' && isDocumentEvidence(env.evidence)) {
      const e = env.evidence;
      rows.push({
        id: e.id,
        origin: 'Document',
        kind: e.kind,
        polarity: e.polarity,
        certainty: e.certainty,
        statement: e.statement,
        fields: structureFields(e.content.structure),
        qualifiers: e.content.qualifiers,
        messageSequence: null,
        groups: sourceGroups(e.content.sources),
        document: {
          documentId: e.origin.documentId,
          documentVersionId: e.origin.documentVersionId,
          preparationId: e.origin.preparationId,
        },
      });
    } else {
      unsupported++;
    }
  }
  return { rows, unsupported };
}

/** Posizione di una parte citata come la fornisce il server; i dati mancanti non si inventano. */
export function locatorText(r: SourceDocumentView | null): string {
  if (!r) return 'Posizione non risolvibile';
  const l = r.locator;
  const parts = [`${r.originalName} v${r.documentVersion}`];
  if (l.page !== null) parts.push(`pagina ${l.page}`);
  if (l.headingPath.length > 0) parts.push(l.headingPath.join(' › '));
  if (l.sourceLines) {
    parts.push(
      l.sourceLines.start === l.sourceLines.end
        ? `riga ${l.sourceLines.start}`
        : `righe ${l.sourceLines.start}-${l.sourceLines.end}`,
    );
  }
  return parts.join(' · ');
}

/** Estratto su una riga (spazi compressi), troncato per punti di codice senza spezzare coppie surrogate. */
export function excerpt(text: string | null | undefined, max = 140): string {
  const flat = (text ?? '').replace(/\s+/g, ' ').trim();
  const chars = Array.from(flat);
  return chars.length > max ? `${chars.slice(0, max - 1).join('')}…` : flat;
}

export const plural = (n: number, one: string, many: string): string =>
  `${n} ${n === 1 ? one : many}`;

const jobErrorLabels: Record<string, string> = {
  invalid_agent_output: 'Il risultato del modello non era valido e non è stato salvato.',
  llm_unavailable: 'Il modello AI non è al momento raggiungibile.',
  model_timeout: 'Il modello non ha risposto entro il tempo previsto.',
  model_rejected: 'Il modello ha rifiutato la richiesta.',
  model_not_available: 'Il modello richiesto non è disponibile.',
  context_overflow: 'Il messaggio supera il contesto gestibile dal modello.',
  deadline_exceeded: "Il tempo massimo per l'estrazione è scaduto.",
  attempts_exhausted: "I tentativi disponibili per l'estrazione sono esauriti.",
  processing_failed: 'Elaborazione non riuscita.',
};

/** Testo dell'errore di un lavoro fallito: etichetta nota per codice, altrimenti il messaggio (già redatto) del server. */
export function jobErrorText(error: { code: string; message: string | null } | null): string {
  if (!error) return 'Estrazione non riuscita.';
  return jobErrorLabels[error.code] ?? error.message ?? 'Estrazione non riuscita.';
}

export interface ApiProblem {
  status: number;
  code: string | null;
  jobId: string | null;
}

/** Stato e codice applicativo (`code` del problem+json) di un errore HTTP; null se non è una risposta del server. */
export function problemOf(e: unknown): ApiProblem | null {
  if (!(e instanceof HttpErrorResponse)) return null;
  const body = (e.error ?? {}) as { code?: unknown; jobId?: unknown };
  return {
    status: e.status,
    code: typeof body.code === 'string' ? body.code : null,
    jobId: typeof body.jobId === 'string' ? body.jobId : null,
  };
}

/** 409 promotion_conflict: lo stato dei messaggi è cambiato dopo il caricamento. */
export function isPromotionConflict(e: unknown): boolean {
  const p = problemOf(e);
  return p?.status === 409 && p.code === 'promotion_conflict';
}

/** Esito HTTP incerto (rete, timeout, 5xx): la stessa Idempotency-Key va riusata per ripetere il comando. */
export function isUncertainOutcome(e: unknown): boolean {
  return !(e instanceof HttpErrorResponse) || e.status === 0 || e.status >= 500 || e.status === 408;
}
