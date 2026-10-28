import { TokenSource, TokenValue } from './project-api.service';

const percentFormat = new Intl.NumberFormat('it-IT', {
  minimumFractionDigits: 1,
  maximumFractionDigits: 1,
});

/**
 * Numero di token con il punto delle migliaia (49152 → «49.152»). Si raggruppa sempre: la regola it-IT di Intl
 * omette il separatore sotto le 5 cifre (8192), mentre la UI mostra «8.192».
 */
export function formatCount(value: number): string {
  return Math.round(value)
    .toString()
    .replace(/\B(?=(\d{3})+(?!\d))/g, '.');
}

export function formatPercent(value: number): string {
  return `${percentFormat.format(value)}%`;
}

/** Valore con provenienza: nessuna cifra se non disponibile, «≈» se stimato. */
export function formatToken(token: TokenValue | null | undefined): string {
  if (!token || token.source === 'unavailable' || token.value === null) return 'n/d';
  const text = formatCount(token.value);
  return token.source === 'estimated' ? `≈ ${text}` : text;
}

const sourceLabels: Record<TokenSource, string> = {
  measured: 'misurato',
  calculated: 'calcolato',
  estimated: 'stima',
  unavailable: 'non disponibile',
};

export function sourceLabel(token: TokenValue | null | undefined): string {
  return sourceLabels[token?.source ?? 'unavailable'] ?? sourceLabels.unavailable;
}

/** Valore effettivamente presente (provenienza diversa da unavailable e numero valorizzato). */
export function hasValue(
  token: TokenValue | null | undefined,
): token is TokenValue & { value: number } {
  return !!token && token.source !== 'unavailable' && token.value !== null;
}
