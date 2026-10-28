import { TokenSource, TokenValue } from './project-api.service';

const numberFormat = new Intl.NumberFormat('it-IT');
const percentFormat = new Intl.NumberFormat('it-IT', {
  minimumFractionDigits: 1,
  maximumFractionDigits: 1,
});

/** Numero di token in locale it-IT (49152 → «49.152»). */
export function formatCount(value: number): string {
  return numberFormat.format(value);
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
