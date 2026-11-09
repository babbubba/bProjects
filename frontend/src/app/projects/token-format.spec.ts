import { formatDuration } from './token-format';

describe('formatDuration', () => {
  it('null o non finito è «indisponibile», mai zero', () => {
    expect(formatDuration(null)).toBe('indisponibile');
    expect(formatDuration(undefined)).toBe('indisponibile');
    expect(formatDuration(Number.NaN)).toBe('indisponibile');
  });

  it('zero reale resta zero e si distingue dall’indisponibile', () => {
    expect(formatDuration(0)).toBe('0 ms');
  });

  it('millisecondi, secondi con virgola it-IT e minuti', () => {
    expect(formatDuration(850)).toBe('850 ms');
    expect(formatDuration(1200)).toBe('1,2 s');
    expect(formatDuration(59_000)).toBe('59,0 s');
    expect(formatDuration(125_000)).toBe('2 min 5 s');
    expect(formatDuration(180_000)).toBe('3 min 0 s');
  });
});
