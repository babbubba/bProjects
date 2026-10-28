import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { ContextUsageStore } from './context-usage.store';
import { InferenceMetricsComponent } from './inference-metrics.component';
import { ContextUsageKind } from './project-api.service';
import { formatCount, formatPercent, formatToken, hasValue, sourceLabel } from './token-format';

/** Soglie di occupazione del contesto: da qui in poi il pannello avvisa (non blocca nulla). */
export const WARNING_PERCENT = 80;
export const CRITICAL_PERCENT = 95;

const timeFormat = new Intl.DateTimeFormat('it-IT', { dateStyle: 'short', timeStyle: 'short' });

const kindLabels: Record<ContextUsageKind, string> = {
  chat: 'Chat',
  analysis: 'Analisi',
  document: 'Documento',
};

const runtimeLabels = {
  online: 'in linea',
  offline: 'non raggiungibile',
  unverified: 'non verificato',
};

interface Segment {
  id: 'prompt' | 'reserved' | 'margin' | 'remaining';
  label: string;
  /** Token del segmento; null se non disponibile. */
  tokens: number | null;
  text: string;
  /** Larghezza (% del contesto) nella barra, limitata al 100% cumulativo. */
  width: number;
}

/**
 * Monitor del contesto del progetto: occupazione dell'ultima richiesta AI, composizione del prompt, storico originale
 * e compattazione. Sotto xl è compatto: il riepilogo resta sempre visibile, i dettagli si espandono.
 */
@Component({
  selector: 'app-context-usage-panel',
  imports: [InferenceMetricsComponent],
  templateUrl: './context-usage-panel.component.html',
  styleUrl: './context-usage-panel.component.scss',
})
export class ContextUsagePanelComponent {
  private readonly store = inject(ContextUsageStore);

  readonly projectId = input.required<string>();

  protected readonly usage = this.store.usage;
  protected readonly failed = this.store.failed;
  protected readonly detailsOpen = signal(false);
  protected readonly compositionOpen = signal(false);
  protected readonly warningPercent = WARNING_PERCENT;
  protected readonly criticalPercent = CRITICAL_PERCENT;

  protected readonly summary = computed(() => {
    const usage = this.usage();
    const last = usage?.last;
    if (!usage || !last) return null;
    const prompt = last.metrics.promptTokens;
    // La capacità è quella della richiesta misurata; se manca si usa quella del runtime verificato.
    const ctxToken = hasValue(last.metrics.contextWindowTokens)
      ? last.metrics.contextWindowTokens
      : usage.runtime.contextWindowTokens;
    const ctx = hasValue(ctxToken) ? ctxToken.value : null;
    const percent = last.usedPercent;
    const percentText =
      percent === null ? 'n/d' : `${prompt.source === 'estimated' ? '≈ ' : ''}${formatPercent(percent)}`;
    const raw: Pick<Segment, 'id' | 'label' | 'tokens'>[] = [
      { id: 'prompt', label: 'Prompt effettivo', tokens: hasValue(prompt) ? prompt.value : null },
      { id: 'reserved', label: 'Output riservato', tokens: last.outputReservedTokens },
      { id: 'margin', label: 'Margine tecnico', tokens: last.safetyMarginTokens },
      {
        id: 'remaining',
        label: 'Residuo',
        tokens: hasValue(last.remainingTokens) ? last.remainingTokens.value : null,
      },
    ];
    let used = 0;
    const segments: Segment[] = raw.map((s) => {
      const share = ctx && s.tokens ? (s.tokens / ctx) * 100 : 0;
      const width = Math.min(share, Math.max(0, 100 - used));
      used += width;
      return { ...s, text: s.tokens === null ? 'n/d' : formatCount(s.tokens), width };
    });
    const level =
      percent === null || percent < WARNING_PERCENT
        ? 'ok'
        : percent >= CRITICAL_PERCENT
          ? 'critical'
          : 'warning';
    const parts = segments.map((s) => `${s.label.toLowerCase()} ${s.text}`).join(', ');
    return {
      prompt: formatToken(prompt),
      promptSource: sourceLabel(prompt),
      ctx: ctx === null ? 'n/d' : formatCount(ctx),
      percentText,
      percentNow: percent === null ? null : Math.min(100, Math.round(percent * 10) / 10),
      level,
      segments,
      valueText: `Contesto occupato ${percentText} (prompt ${sourceLabel(prompt)}): ${parts} token`,
      kind: kindLabels[last.kind],
      at: timeFormat.format(new Date(last.at)),
      metrics: last.metrics,
      stale: last.staleReason === 'newer_messages',
    };
  });

  /** Se non null, i valori mostrati sono le ultime metriche persistite e non lo stato corrente del server. */
  protected readonly snapshot = computed(() => {
    const runtime = this.usage()?.runtime;
    if (!runtime) return null;
    if (runtime.status === 'offline') return 'Il server AI non è raggiungibile.';
    if (runtime.status === 'unverified') return 'Il server AI non è stato verificato.';
    if (runtime.windowMismatch) {
      return `La finestra di contesto del server (${formatToken(runtime.contextWindowTokens)}) non coincide con quella configurata (${formatCount(runtime.configuredContextWindowTokens)}).`;
    }
    return null;
  });

  protected readonly runtime = computed(() => {
    const runtime = this.usage()?.runtime;
    if (!runtime) return null;
    return {
      model: runtime.modelId,
      status: runtimeLabels[runtime.status],
      window: formatToken(runtime.contextWindowTokens),
      configured: formatCount(runtime.configuredContextWindowTokens),
      checkedAt: runtime.checkedAt ? timeFormat.format(new Date(runtime.checkedAt)) : null,
    };
  });

  protected readonly composition = computed(() => {
    const composition = this.usage()?.composition;
    if (!composition) return null;
    const row = (token: Parameters<typeof formatToken>[0]) => ({
      text: formatToken(token),
      source: sourceLabel(token),
    });
    return {
      items: composition.items.map((item) => ({
        key: item.key,
        label: item.label,
        messages: item.messageCount,
        ...row(item.tokens),
      })),
      unattributed: row(composition.unattributedTokens),
      total: row(composition.promptTokens),
    };
  });

  protected readonly history = computed(() => {
    const history = this.usage()?.history;
    if (!history) return null;
    return {
      count: formatCount(history.messageCount),
      tokens: formatToken(history.originalTokens),
      source: sourceLabel(history.originalTokens),
      hasTokens: hasValue(history.originalTokens),
    };
  });

  protected readonly compaction = computed(() => {
    const compaction = this.usage()?.compaction;
    if (!compaction) return null;
    // Prima/dopo/risparmiati solo se il valore esiste: mai cifre inventate per quelli non disponibili.
    const parts = [
      { label: 'Prima', token: compaction.tokensBefore },
      { label: 'Dopo', token: compaction.tokensAfter },
      { label: 'Risparmiati', token: compaction.tokensSaved },
    ]
      .filter((p) => hasValue(p.token))
      .map((p) => ({ label: p.label, text: formatToken(p.token), source: sourceLabel(p.token) }));
    return {
      at: timeFormat.format(new Date(compaction.at)),
      messages: formatCount(compaction.messagesSummarized),
      parts,
    };
  });

  constructor() {
    // Lo stato appartiene al progetto aperto: cambio progetto o distruzione chiudono e ripartono da zero.
    effect((onCleanup) => {
      const id = this.projectId();
      untracked(() => {
        this.detailsOpen.set(false);
        this.compositionOpen.set(false);
        onCleanup(this.store.watch(id));
      });
    });
  }

  protected toggleDetails(): void {
    this.detailsOpen.update((open) => !open);
  }

  protected toggleComposition(): void {
    this.compositionOpen.update((open) => !open);
  }
}
