/** Errore di un turno o run asincrono (ChatTurnView.error / ProcessingRunView.error). */
export interface FailureError {
  code: string;
  message: string | null;
}

export type FailureScope = 'chat' | 'analysis' | 'document';

const MEASURED = /\b(\d+) token oltre il budget di input di (\d+) token\b/;

/**
 * Token e budget misurati dal backend per context_overflow (ModelFailure.Detail). Il contratto trasporta solo
 * testo: i numeri si mostrano se e solo se il messaggio li riporta, altrimenti null (mai valori o percentuali inventati).
 */
export function measuredContext(
  message: string | null | undefined,
): { tokens: number; budget: number } | null {
  const match = message ? MEASURED.exec(message) : null;
  if (!match) return null;
  const tokens = Number(match[1]);
  const budget = Number(match[2]);
  return tokens > 0 && budget > 0 ? { tokens, budget } : null;
}

/**
 * Errori del preflight documentale e della scadenza del comando, quando il chiamante non ha un'etichetta propria.
 * La scadenza è un limite massimo di tempo (un tetto), mai una durata prevista né una promessa di completamento.
 */
const preflightFailure: Record<string, string> = {
  deadline_exceeded:
    'È stato raggiunto il limite massimo di tempo del comando (un tetto, non una durata prevista): il lavoro non è terminato e nessun risultato parziale viene presentato come completo.',
  runtime_unverified:
    'Il runtime del modello non è verificato: nessuna richiesta è stata inviata al modello.',
  config_invalid:
    'La configurazione dei limiti del modello non è valida: nessuna richiesta è stata inviata al modello.',
  document_limit_exceeded:
    'Il lavoro supera i limiti consentiti per i documenti: nessuna richiesta è stata inviata al modello.',
  coverage_incomplete:
    'La copertura dei documenti non è completa: nessuna richiesta è stata inviata al modello.',
};

const overflowHint: Record<FailureScope, string> = {
  chat: 'Riduci o dividi in più parti il messaggio più recente, oppure riformulalo in modo più sintetico, poi invia di nuovo.',
  analysis:
    'Riduci o dividi il messaggio più recente, oppure riformulalo in modo più sintetico, poi premi di nuovo Analizza.',
  document:
    "Il contenuto dell'anteprima confermata è troppo esteso: sintetizza le voci più lunghe, salva, conferma di nuovo e riprova a generare.",
};

/**
 * Testo comprensibile di un errore asincrono: etichetta nota per codice, altrimenti il messaggio del server,
 * altrimenti il fallback. context_overflow aggiunge i valori misurati (se presenti) e un'azione praticabile.
 */
export function describeFailure(
  error: FailureError | null | undefined,
  labels: Record<string, string>,
  fallback: string,
  scope: FailureScope,
): string {
  if (!error) return fallback;
  const known = labels[error.code] ?? preflightFailure[error.code];
  if (error.code !== 'context_overflow') return known ?? error.message ?? fallback;
  const measured = measuredContext(error.message);
  const detail = measured
    ? ` Contesto misurato: ${measured.tokens} token, oltre il limite di ${measured.budget} token per l'input del modello.`
    : '';
  return `${known ?? 'Il contesto supera quanto il modello può gestire.'}${detail} ${overflowHint[scope]}`;
}

const regimeReasons: Record<string, string> = {
  preflight_pending:
    'Classificazione in corso: il regime è provvisorio finché il lavoro non è misurato.',
  single_call_fit: 'Il lavoro entra in una sola chiamata del modello.',
  multipass_or_capacity:
    'Il lavoro richiede più chiamate o supera la capacità di una sola chiamata del modello.',
  workload_unknown: 'Il carico di lavoro non è misurabile in anticipo: si procede con più tempo.',
  preparation: 'La preparazione dei documenti richiede più tempo per sua natura.',
  additional_work:
    'Durante l’elaborazione è emerso lavoro aggiuntivo non previsto: il limite è stato esteso.',
};

/** Motivo (codice tecnico del server) di regime o di cambio di regime, in italiano; un codice ignoto è dichiarato come tale. */
export function regimeReasonText(reason: string | null | undefined): string | null {
  if (!reason) return null;
  return regimeReasons[reason] ?? `Motivo tecnico: ${reason}.`;
}
