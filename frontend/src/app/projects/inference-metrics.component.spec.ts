import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  calculated,
  contextUsage,
  estimated,
  execution,
  metrics,
  unavailable,
} from './context-usage.fixtures';
import { ContextUsageStore } from './context-usage.store';
import {
  AttemptView,
  ExecutionView,
  InferenceMetrics,
  InferencePhase,
  ModelFingerprint,
} from './project-api.service';
import { InferenceMetricsComponent } from './inference-metrics.component';

describe('InferenceMetricsComponent', () => {
  let fixture: ComponentFixture<InferenceMetricsComponent>;
  let el: HTMLElement;

  function render(
    inputs: {
      metrics?: InferenceMetrics | null;
      running?: boolean;
      phase?: InferencePhase;
      execution?: ExecutionView | null;
      attempts?: AttemptView[] | null;
      modelFingerprint?: ModelFingerprint | null;
    } = {},
  ) {
    for (const [key, value] of Object.entries(inputs)) fixture.componentRef.setInput(key, value);
    fixture.detectChanges();
  }

  /** Testo visibile (esclude il testo riservato agli screen reader). */
  const shown = (id: string) =>
    el.querySelector(`[data-testid=${id}] [aria-hidden=true]`)?.textContent?.trim();
  const line = () =>
    [...el.querySelectorAll('.inference-metrics > *')]
      .map(
        (n) => n.querySelector('[aria-hidden=true]')?.textContent?.trim() ?? n.textContent?.trim(),
      )
      .join(' ')
      .replace(/\s+/g, ' ');

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(InferenceMetricsComponent);
    el = fixture.nativeElement;
  });

  it('mostra CTX, Max output e Generati in locale it-IT', () => {
    render({ metrics: metrics() });

    expect(shown('metric-ctx')).toBe('CTX 49.152');
    expect(shown('metric-max')).toBe('Max output 8.192');
    expect(shown('metric-gen')).toBe('Generati 1.347 token');
    expect(line()).toBe('CTX 49.152 · Max output 8.192 · Generati 1.347 token');
    expect(el.querySelector('[data-testid=metrics-truncated]')).toBeNull();
  });

  it('distingue le tre cifre con tooltip e testo accessibile, non solo hover', () => {
    render({ metrics: metrics() });

    const ctx = el.querySelector<HTMLElement>('[data-testid=metric-ctx]')!;
    expect(ctx.title).toContain('Capacità contestuale reale dello slot');
    expect(ctx.querySelector('.visually-hidden')?.textContent).toBe(
      'Capacità contestuale: 49.152 token',
    );
    expect(el.querySelector('[data-testid=metric-max] .visually-hidden')?.textContent).toBe(
      'Massimo di output: 8.192 token',
    );
    const gen = el.querySelector<HTMLElement>('[data-testid=metric-gen]')!;
    expect(gen.title).toContain('inclusi quelli consumati dal ragionamento');
    expect(gen.querySelector('.visually-hidden')?.textContent).toBe('Token generati: 1.347 token');
    expect(el.querySelector('[role=group]')?.getAttribute('aria-label')).toBe(
      'Metriche della richiesta AI',
    );
  });

  it('etichetta come «limite richiesto» un massimo di output non confermato', () => {
    render({ metrics: metrics({ maxOutputTokens: { value: 4096, confirmed: false } }) });

    expect(shown('metric-max')).toBe('Max output 4.096 (limite richiesto)');
    expect(el.querySelector<HTMLElement>('[data-testid=metric-max]')!.title).toContain(
      'non verificato sul server',
    );
  });

  it('un valore calcolato è mostrato normalmente', () => {
    render({ metrics: metrics({ completionTokens: calculated(900) }) });

    expect(shown('metric-gen')).toBe('Generati 900 token');
    expect(el.querySelector('[data-testid=metric-gen]')!.getAttribute('data-source')).toBe(
      'calculated',
    );
    expect(el.querySelector<HTMLElement>('[data-testid=metric-gen]')!.title).toContain('calcolato');
  });

  it('un valore stimato ha il prefisso «≈» e la dicitura «stima»', () => {
    render({
      metrics: metrics({
        contextWindowTokens: estimated(32768),
        completionTokens: estimated(1200),
      }),
    });

    expect(shown('metric-ctx')).toBe('CTX ≈ 32.768');
    expect(el.querySelector<HTMLElement>('[data-testid=metric-ctx]')!.title).toContain('(stima)');
    expect(el.querySelector('[data-testid=metric-ctx] .visually-hidden')?.textContent).toContain(
      '(stima)',
    );
    expect(shown('metric-gen')).toBe('Generati ≈ 1.200 token');
  });

  it('un valore non disponibile mostra «n/d» senza cifre inventate', () => {
    render({
      metrics: metrics({
        contextWindowTokens: unavailable,
        completionTokens: { value: null, source: 'measured' },
      }),
    });

    expect(shown('metric-ctx')).toBe('CTX n/d');
    expect(shown('metric-gen')).toBe('Generati n/d');
    expect(el.querySelector('[data-testid=metric-ctx] .visually-hidden')?.textContent).toBe(
      'Capacità contestuale: non disponibile',
    );
    expect(el.querySelector<HTMLElement>('[data-testid=metric-ctx]')!.title).toContain(
      'non disponibile',
    );
  });

  it('segnala una risposta troncata dal limite di output (finishReason length)', () => {
    render({ metrics: metrics({ finishReason: 'length' }) });

    expect(el.querySelector('[data-testid=metrics-truncated]')?.textContent).toContain(
      'troncata dal limite di output',
    );
  });

  it('metrics null (storico) mostra «Metriche non disponibili» senza cifre', () => {
    render({ metrics: null });

    expect(el.querySelector('[data-testid=metrics-unavailable]')?.textContent).toBe(
      'Metriche non disponibili',
    );
    expect(el.querySelector('[data-testid=metric-ctx]')).toBeNull();
    expect(el.textContent).not.toMatch(/\d/);
  });

  it('una richiesta fallita o interrotta non mostra alcuna cifra', () => {
    for (const state of ['failed', 'interrupted'] as const) {
      render({ metrics: metrics({ state }) });

      expect(el.querySelector('[data-testid=metrics-failed]')).not.toBeNull();
      expect(el.querySelector('[data-testid=metric-gen]')).toBeNull();
      expect(el.textContent).not.toMatch(/\d/);
    }
  });

  it('in corso senza metriche usa la configurazione nota dal monitor del contesto', () => {
    TestBed.inject(ContextUsageStore).usage.set(contextUsage());
    render({ running: true, phase: 'chat', metrics: undefined });

    expect(line()).toBe('CTX 32.768 · Max output 8.192 (limite richiesto) · generazione in corso…');
    expect(el.querySelector('[data-testid=metric-gen]')).toBeNull();
  });

  it('in corso senza monitor del contesto mostra n/d, mai cifre inventate', () => {
    render({ running: true, metrics: undefined });

    expect(shown('metric-ctx')).toBe('CTX n/d');
    expect(shown('metric-max')).toBe('Max output n/d');
    expect(el.querySelector('[data-testid=metrics-running]')).not.toBeNull();
  });

  it('metriche con state running mostrano la configurazione della richiesta e non i generati', () => {
    render({ metrics: metrics({ state: 'running', completionTokens: unavailable }) });

    expect(line()).toBe('CTX 49.152 · Max output 8.192 · generazione in corso…');
    expect(el.querySelector('[data-testid=metric-gen]')).toBeNull();
  });

  it('non espone mai il contenuto del ragionamento', () => {
    const withReasoning = {
      ...metrics(),
      reasoningContent: 'PENSIERO-SEGRETO',
    } as InferenceMetrics;
    render({ metrics: withReasoning });

    expect(el.innerHTML).not.toContain('PENSIERO-SEGRETO');
    expect(el.textContent).not.toMatch(/ragionamento attivo|reasoning/i);
  });
  describe('tempi per fase (T065)', () => {
    const timing = (id: string) =>
      el
        .querySelector<HTMLElement>(`[data-testid=timing-${id}]`)
        ?.textContent?.replace(/\s+/g, ' ')
        .trim();

    it('mostra coda, preflight, calcolo, backoff e totale dalle sole misure del server', () => {
      render({ metrics: metrics(), execution: execution() });

      expect(timing('queue')).toBe('Coda 1,2 s');
      expect(timing('preflight')).toBe('Preflight 300 ms');
      expect(timing('processing')).toBe('Calcolo 6,5 s');
      expect(timing('backoff')).toBe('Backoff 0 ms');
      expect(timing('elapsed')).toBe('Totale trascorso 9,0 s');
    });

    it('il recupero osservato è mostrato come tempo distinto dalla coda; zero osservato non è indisponibile', () => {
      render({ metrics: metrics(), execution: execution({ recoveryMs: 7000, queueMs: 1200 }) });
      expect(timing('recovery')).toBe('Recupero 7,0 s');
      expect(timing('queue')).toBe('Coda 1,2 s');

      render({ metrics: metrics(), execution: execution({ recoveryMs: 0 }) });
      expect(timing('recovery')).toBe('Recupero 0 ms');
      expect(
        el.querySelector('[data-testid=timing-recovery]')!.getAttribute('data-available'),
      ).toBe('true');
    });

    it('un intervallo null è «indisponibile» e non zero; un recupero non registrato resta indisponibile', () => {
      render({
        metrics: metrics(),
        execution: execution({
          queueMs: null,
          processingMs: null,
          backoffMs: null,
          recoveryMs: null,
        }),
      });

      expect(timing('queue')).toBe('Coda indisponibile');
      expect(timing('processing')).toBe('Calcolo indisponibile');
      expect(timing('backoff')).toBe('Backoff indisponibile');
      expect(timing('recovery')).toBe('Recupero indisponibile');
      expect(el.querySelector('[data-testid=timing-queue]')!.getAttribute('data-available')).toBe(
        'false',
      );
      expect(el.textContent).not.toMatch(/Coda 0|Calcolo 0|Backoff 0|Recupero 0/);
    });

    it('senza execution (record 001) non mostra alcun tempo e la riga delle metriche è invariata', () => {
      render({ metrics: metrics(), execution: null });

      expect(el.querySelector('[data-testid=execution-timing]')).toBeNull();
      expect(line()).toBe('CTX 49.152 · Max output 8.192 · Generati 1.347 token');
    });

    it('è un elenco con etichetta accessibile e non usa barre di avanzamento o percentuali', () => {
      render({ metrics: metrics(), execution: execution() });

      const list = el.querySelector('[data-testid=execution-timing]')!;
      expect(list.getAttribute('aria-label')).toBe('Tempi della richiesta AI');
      expect(list.querySelectorAll('dt').length).toBe(list.querySelectorAll('dd').length);
      expect(el.querySelector('[role=progressbar], progress')).toBeNull();
      expect(list.textContent).not.toContain('%');
    });
  });

  describe('tentativi e impronta del modello (T102)', () => {
    const fingerprint = (over: Partial<ModelFingerprint> = {}): ModelFingerprint => ({
      modelId: 'modello-effettivo',
      contextWindowTokens: calculated(8192),
      hash: '0123456789abcdef',
      ...over,
    });
    const attempt = (over: Partial<AttemptView> = {}): AttemptView => ({
      attempt: 1,
      outcome: 'winning',
      state: 'completed',
      calls: 1,
      activeMs: 5000,
      promptTokens: calculated(100),
      completionTokens: calculated(20),
      finishReason: 'stop',
      model: fingerprint(),
      ...over,
    });
    const row = (n: number) =>
      el
        .querySelector<HTMLElement>(`[data-testid=attempt-${n}]`)
        ?.textContent?.replace(/\s+/g, ' ')
        .trim();

    it('distingue il tentativo fallito da quello vincente dello stesso lavoro', () => {
      render({
        metrics: metrics(),
        attempts: [
          attempt({
            outcome: 'failed',
            state: 'failed',
            activeMs: 3000,
            finishReason: 'error',
            model: fingerprint({ modelId: 'modello-primo', hash: 'fedcba9876543210' }),
          }),
          attempt({ attempt: 2 }),
        ],
        modelFingerprint: fingerprint(),
      });

      expect(row(1)).toBe(
        'Tentativo 1 · Fallito · Tempo attivo 3,0 s · 1 chiamata · Modello modello-primo',
      );
      expect(row(2)).toBe(
        'Tentativo 2 · Vincente · Tempo attivo 5,0 s · 1 chiamata · Modello modello-effettivo',
      );
      expect(el.querySelector('[data-testid=attempt-1]')!.getAttribute('data-outcome')).toBe(
        'failed',
      );
      expect(el.querySelector('[data-testid=attempt-2]')!.getAttribute('data-outcome')).toBe(
        'winning',
      );
      expect(el.querySelector('[data-testid=attempts]')!.getAttribute('aria-label')).toBe(
        'Tentativi della richiesta AI',
      );
    });

    it('un tentativo non registrato o senza durata resta indisponibile, mai zero né modello inventato', () => {
      render({
        metrics: metrics(),
        attempts: [
          attempt({
            outcome: 'unrecorded',
            state: null,
            calls: 0,
            activeMs: null,
            promptTokens: unavailable,
            completionTokens: unavailable,
            finishReason: null,
            model: null,
          }),
          attempt({ attempt: 2, outcome: 'interrupted', state: 'interrupted', activeMs: null }),
          attempt({ attempt: 3 }),
        ],
      });

      expect(row(1)).toBe(
        'Tentativo 1 · Non registrato · Tempo attivo indisponibile · Modello indisponibile',
      );
      expect(row(2)).toBe(
        'Tentativo 2 · Interrotto · Tempo attivo indisponibile · 1 chiamata · Modello modello-effettivo',
      );
      expect(el.querySelector('[data-testid=attempt-1]')!.getAttribute('data-available')).toBe(
        'false',
      );
      expect(el.textContent).not.toMatch(/Tempo attivo 0/);
    });

    it('mostra il modello effettivo della chiamata con la sua impronta e la finestra solo se verificata', () => {
      render({ metrics: metrics(), modelFingerprint: fingerprint() });
      const text = el
        .querySelector('[data-testid=model-fingerprint]')!
        .textContent!.replace(/\s+/g, ' ')
        .trim();
      expect(text).toBe(
        'Modello effettivo modello-effettivo · finestra 8.192 · impronta 0123456789abcdef',
      );

      render({
        metrics: metrics(),
        modelFingerprint: fingerprint({ contextWindowTokens: unavailable }),
      });
      expect(el.querySelector('[data-testid=model-fingerprint]')!.textContent).toContain(
        'finestra n/d',
      );
    });

    it('senza dati dal server (record storico o server precedente) non mostra tentativi né impronta', () => {
      render({ metrics: metrics(), attempts: null, modelFingerprint: null });
      expect(el.querySelector('[data-testid=attempts]')).toBeNull();
      expect(el.querySelector('[data-testid=model-fingerprint]')).toBeNull();
    });

    it('un solo tentativo vincente non produce elenco e mostra solo l impronta', () => {
      render({ metrics: metrics(), attempts: [attempt()], modelFingerprint: fingerprint() });
      expect(el.querySelector('[data-testid=attempts]')).toBeNull();
      expect(el.querySelector('[data-testid=model-fingerprint]')).not.toBeNull();
    });

    it('il tempo totale t0 resta autorevole e distinto dal tempo attivo dei tentativi', () => {
      render({
        metrics: metrics(),
        execution: execution({ elapsedMs: 40000, recoveryMs: 7000 }),
        attempts: [
          attempt({ outcome: 'failed', state: 'failed', activeMs: 3000 }),
          attempt({ attempt: 2 }),
        ],
      });
      expect(
        el.querySelector('[data-testid=timing-elapsed]')!.textContent!.replace(/\s+/g, ' ').trim(),
      ).toBe('Totale trascorso 40,0 s');
      expect(
        el.querySelector('[data-testid=timing-recovery]')!.textContent!.replace(/\s+/g, ' ').trim(),
      ).toBe('Recupero 7,0 s');
      expect(el.textContent).not.toContain('8,0 s'); // nessuna somma dei tempi attivi dei tentativi
    });

    it('non espone ragionamento, prompt o campi non previsti dal contratto', () => {
      const hostile = {
        ...attempt({ outcome: 'failed', state: 'failed' }),
        prompt: 'PROMPT-SEGRETO',
        reasoningContent: 'PENSIERO-SEGRETO',
        endpoint: 'http://dgx.interno',
      } as AttemptView;
      render({ metrics: metrics(), attempts: [hostile, attempt({ attempt: 2 })] });
      expect(el.innerHTML).not.toContain('SEGRETO');
      expect(el.innerHTML).not.toContain('dgx.interno');
    });
  });
});
