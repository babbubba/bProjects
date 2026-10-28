import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  calculated,
  contextUsage,
  estimated,
  metrics,
  unavailable,
} from './context-usage.fixtures';
import { ContextUsageStore } from './context-usage.store';
import { InferenceMetrics, InferencePhase } from './project-api.service';
import { InferenceMetricsComponent } from './inference-metrics.component';

describe('InferenceMetricsComponent', () => {
  let fixture: ComponentFixture<InferenceMetricsComponent>;
  let el: HTMLElement;

  function render(
    inputs: {
      metrics?: InferenceMetrics | null;
      running?: boolean;
      phase?: InferencePhase;
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
});
