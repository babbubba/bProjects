import { describeFailure, measuredContext } from './failure-text';

const labels = {
  context_overflow: 'La conversazione supera il contesto gestibile dal modello.',
  llm_unavailable: 'Il modello AI non è al momento raggiungibile.',
};
const MEASURED =
  'Contesto di 31000 token oltre il budget di input di 24000 token, non riducibile con la compattazione nativa. Gli originali non sono stati modificati.';

describe('failure-text', () => {
  it('legge token e budget solo se il messaggio li riporta davvero', () => {
    expect(measuredContext(MEASURED)).toEqual({ tokens: 31000, budget: 24000 });
    expect(measuredContext('Il modello interno ha rifiutato la richiesta.')).toBeNull();
    expect(measuredContext(null)).toBeNull();
    expect(measuredContext('Contesto di 0 token oltre il budget di input di 0 token')).toBeNull();
  });

  it('context_overflow: spiegazione, valori misurati e azione praticabile, senza il generico rifiuto', () => {
    const text = describeFailure({ code: 'context_overflow', message: MEASURED }, labels, 'x', 'chat');
    expect(text).toContain('supera il contesto gestibile');
    expect(text).toContain('31000 token');
    expect(text).toContain('24000 token');
    expect(text).toMatch(/messaggio più recente/);
    expect(text).toMatch(/dividi|sintetico/);
    expect(text).not.toMatch(/rifiutato la richiesta/);
    expect(text).not.toMatch(/%/);
  });

  it('context_overflow senza valori nel messaggio: nessun numero né percentuale inventati', () => {
    const generic = 'Il modello interno ha rifiutato la richiesta.';
    for (const message of [null, generic]) {
      const text = describeFailure({ code: 'context_overflow', message }, labels, 'x', 'analysis');
      expect(text).toContain('supera il contesto gestibile');
      expect(text).toMatch(/messaggio più recente/);
      expect(text).not.toMatch(/\d/);
      expect(text).not.toMatch(/rifiutato la richiesta/);
    }
  });

  it('documento: l\'indicazione riguarda il contenuto confermato, non il messaggio più recente', () => {
    const text = describeFailure({ code: 'context_overflow', message: MEASURED }, labels, 'x', 'document');
    expect(text).toMatch(/anteprima/);
    expect(text).not.toMatch(/messaggio più recente/);
  });

  it('gli altri codici restano invariati: etichetta nota, poi messaggio, poi fallback', () => {
    expect(describeFailure({ code: 'llm_unavailable', message: 'x' }, labels, 'f', 'chat')).toBe(labels.llm_unavailable);
    expect(describeFailure({ code: 'model_rejected', message: 'Dettaglio' }, labels, 'f', 'chat')).toBe('Dettaglio');
    expect(describeFailure({ code: 'model_rejected', message: null }, labels, 'f', 'chat')).toBe('f');
    expect(describeFailure(null, labels, 'f', 'chat')).toBe('f');
  });
});
