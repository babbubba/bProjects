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
  const known = labels[error.code];
  if (error.code !== 'context_overflow') return known ?? error.message ?? fallback;
  const measured = measuredContext(error.message);
  const detail = measured
    ? ` Contesto misurato: ${measured.tokens} token, oltre il limite di ${measured.budget} token per l'input del modello.`
    : '';
  return `${known ?? 'Il contesto supera quanto il modello può gestire.'}${detail} ${overflowHint[scope]}`;
}
