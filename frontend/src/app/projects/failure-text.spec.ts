import { describeFailure, measuredContext, regimeReasonText } from './failure-text';

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
    const text = describeFailure(
      { code: 'context_overflow', message: MEASURED },
      labels,
      'x',
      'chat',
    );
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

  it("documento: l'indicazione riguarda il contenuto confermato, non il messaggio più recente", () => {
    const text = describeFailure(
      { code: 'context_overflow', message: MEASURED },
      labels,
      'x',
      'document',
    );
    expect(text).toMatch(/anteprima/);
    expect(text).not.toMatch(/messaggio più recente/);
  });

  it('gli altri codici restano invariati: etichetta nota, poi messaggio, poi fallback', () => {
    expect(describeFailure({ code: 'llm_unavailable', message: 'x' }, labels, 'f', 'chat')).toBe(
      labels.llm_unavailable,
    );
    expect(
      describeFailure({ code: 'model_rejected', message: 'Dettaglio' }, labels, 'f', 'chat'),
    ).toBe('Dettaglio');
    expect(describeFailure({ code: 'model_rejected', message: null }, labels, 'f', 'chat')).toBe(
      'f',
    );
    expect(describeFailure(null, labels, 'f', 'chat')).toBe('f');
  });
});

describe('failure-text: regime e scadenze (T065)', () => {
  it('regimeReasonText spiega ogni motivo noto in italiano, senza percentuali né durate promesse', () => {
    for (const reason of [
      'preflight_pending',
      'single_call_fit',
      'multipass_or_capacity',
      'workload_unknown',
      'preparation',
      'additional_work',
    ]) {
      const text = regimeReasonText(reason)!;
      expect(text, reason).toMatch(/\S{3,}/);
      expect(text).not.toContain(reason);
      expect(text).not.toMatch(/%/);
    }
    expect(regimeReasonText('additional_work')).toMatch(/aggiuntivo/);
    expect(regimeReasonText('preflight_pending')).toMatch(/provvisori/);
  });

  it('un motivo ignoto è dichiarato come codice tecnico, null resta null', () => {
    expect(regimeReasonText('nuovo_motivo')).toContain('nuovo_motivo');
    expect(regimeReasonText(null)).toBeNull();
    expect(regimeReasonText('')).toBeNull();
  });

  it('deadline_exceeded distingue il limite massimo dalla durata prevista', () => {
    const text = describeFailure({ code: 'deadline_exceeded', message: null }, {}, 'x', 'chat');
    expect(text).toMatch(/limite massimo/);
    expect(text).toMatch(/non (è )?una durata prevista/);
    expect(text).not.toBe('x');
  });

  it('codici del preflight senza etichetta propria hanno un testo che dichiara che nulla è stato inviato', () => {
    for (const code of [
      'runtime_unverified',
      'config_invalid',
      'document_limit_exceeded',
      'coverage_incomplete',
    ]) {
      const text = describeFailure({ code, message: null }, {}, 'x', 'document');
      expect(text, code).not.toBe('x');
      expect(text, code).toMatch(/nessuna richiesta/i);
    }
  });

  it.each([
    'embedding_unavailable',
    'embedding_model_mismatch',
    'embedding_dimension_mismatch',
    'retrieval_index_not_ready',
    'reranker_unavailable',
    'reranker_malformed_response',
  ])('recupero (003) %s: mancato completamento esplicito, mai un risultato parziale', (code) => {
    const text = describeFailure({ code, message: 'x' }, {}, 'fallback', 'analysis');
    expect(text).not.toBe('fallback');
    expect(text.toLowerCase()).toContain('nessuna valutazione dei gap è stata presentata');
  });

  it("l'etichetta fornita dal chiamante prevale sul testo predefinito", () => {
    expect(
      describeFailure(
        { code: 'deadline_exceeded', message: null },
        { deadline_exceeded: 'Etichetta locale.' },
        'x',
        'chat',
      ),
    ).toBe('Etichetta locale.');
  });
});
