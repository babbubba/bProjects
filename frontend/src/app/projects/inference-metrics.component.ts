import { Component, computed, inject, input } from '@angular/core';
import { ContextUsageStore } from './context-usage.store';
import { InferenceMetrics, InferencePhase, TokenSource, TokenValue } from './project-api.service';
import { formatCount, formatToken, hasValue } from './token-format';

interface Figure {
  id: 'ctx' | 'max' | 'gen';
  /** Etichetta breve visibile; la frase completa per gli screen reader è in `sr`. */
  label: string;
  text: string;
  suffix: string;
  tip: string;
  sr: string;
  source: TokenSource;
}

type MetricsView =
  | { kind: 'unavailable' }
  | { kind: 'failed' }
  | { kind: 'running' | 'completed'; figures: Figure[]; truncated: boolean };

const provenance: Record<TokenSource, string> = {
  measured: 'misurato',
  calculated: 'calcolato',
  estimated: 'stima',
  unavailable: 'non disponibile',
};

/**
 * Riga discreta di metriche di inferenza: CTX (capacità contestuale reale dello slot), Max output (massimo richiesto
 * per la chiamata) e Generati (token effettivi, ragionamento incluso; il suo contenuto non è mai mostrato).
 * `metrics` assente o null = record storico; `running` senza metriche = risposta in corso, con la configurazione nota.
 */
@Component({
  selector: 'app-inference-metrics',
  templateUrl: './inference-metrics.component.html',
  styleUrl: './inference-metrics.component.scss',
})
export class InferenceMetricsComponent {
  private readonly usage = inject(ContextUsageStore);

  readonly metrics = input<InferenceMetrics | null | undefined>(null);
  /** Risposta in corso senza metriche ancora disponibili (messaggio in streaming). */
  readonly running = input(false);
  /** Fase della richiesta, per leggere il limite di output noto dal monitor del contesto. */
  readonly phase = input<InferencePhase>('chat');

  protected readonly view = computed<MetricsView>(() => {
    const metrics = this.metrics();
    if (metrics?.state === 'failed' || metrics?.state === 'interrupted') return { kind: 'failed' };
    if (!metrics && !this.running()) return { kind: 'unavailable' };
    const running = this.running() || metrics?.state === 'running';
    const known = this.usage.usage();
    const ctx = metrics?.contextWindowTokens ?? known?.runtime.contextWindowTokens;
    const max =
      metrics?.maxOutputTokens ??
      (known
        ? { value: known.limits.phases[this.phase()].maxOutputTokens, confirmed: false }
        : null);
    const figures = [this.ctx(ctx), this.max(max)];
    if (!running) figures.push(this.generated(metrics!.completionTokens));
    return {
      kind: running ? 'running' : 'completed',
      figures,
      truncated: !running && metrics!.finishReason === 'length',
    };
  });

  private ctx(token: TokenValue | undefined): Figure {
    return this.tokenFigure(
      'ctx',
      'CTX',
      'Capacità contestuale',
      'Capacità contestuale reale dello slot del modello',
      token,
    );
  }

  private generated(token: TokenValue): Figure {
    return this.tokenFigure(
      'gen',
      'Generati',
      'Token generati',
      'Token effettivamente generati, inclusi quelli consumati dal ragionamento',
      token,
    );
  }

  private max(max: { value: number; confirmed: boolean } | null | undefined): Figure {
    const text = max ? formatCount(max.value) : 'n/d';
    const requested = !!max && !max.confirmed;
    return {
      id: 'max',
      label: 'Max output',
      text,
      suffix: requested ? ' (limite richiesto)' : '',
      tip: requested
        ? 'Limite richiesto per la chiamata, non verificato sul server'
        : 'Massimo di token richiesto per la chiamata',
      sr: max
        ? `Massimo di output${requested ? ' (limite richiesto)' : ''}: ${text} token`
        : 'Massimo di output: non disponibile',
      source: max ? (requested ? 'calculated' : 'measured') : 'unavailable',
    };
  }

  private tokenFigure(
    id: Figure['id'],
    label: string,
    full: string,
    tip: string,
    token: TokenValue | undefined,
  ): Figure {
    const text = formatToken(token);
    const source: TokenSource = hasValue(token) ? token.source : 'unavailable';
    return {
      id,
      label,
      text,
      suffix: id === 'gen' && hasValue(token) ? ' token' : '',
      tip: `${tip} (${provenance[source]})`,
      sr: `${full}: ${hasValue(token) ? `${text} token` : 'non disponibile'}${source === 'estimated' ? ' (stima)' : ''}`,
      source,
    };
  }
}
