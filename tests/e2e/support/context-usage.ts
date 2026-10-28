import type { Locator, Page } from '@playwright/test';

// Dati e letture per le prove del monitor del contesto e delle metriche di inferenza.
// I builder riproducono la forma del contratto (specs/001-project-draft/contracts/http-api.md, «Metriche di inferenza
// e monitor del contesto»); le letture trasformano cio' che la UI mostra in numeri confrontabili con le risposte dell'API.

type Json = Record<string, unknown>;
type Source = 'measured' | 'calculated' | 'estimated' | 'unavailable';

export const tv = (value: number | null, source: Source = 'measured'): Json => ({ value, source });

export interface MetricsOptions {
  phase?: 'chat' | 'analysis' | 'document' | 'compaction';
  state?: 'running' | 'completed' | 'failed' | 'interrupted';
  ctx?: number;
  prompt?: number | null;
  completion?: number | null;
  maxOutput?: number;
  maxConfirmed?: boolean;
  finishReason?: string | null;
  calls?: number;
  compaction?: Json | null;
}

export function metricsOf(o: MetricsOptions = {}): Json {
  const prompt = o.prompt === undefined ? 1200 : o.prompt;
  const completion = o.completion === undefined ? 340 : o.completion;
  const total = prompt !== null && completion !== null;
  return {
    state: o.state ?? 'completed',
    phase: o.phase ?? 'chat',
    modelId: 'Qwen3.6-35B',
    startedAt: '2030-01-01T10:00:00Z',
    durationMs: 1800,
    contextWindowTokens: tv(o.ctx ?? 32768),
    maxOutputTokens: { value: o.maxOutput ?? 4096, confirmed: o.maxConfirmed ?? false },
    serverDefaultNPredict: null,
    reasoning: { requested: false, effective: 'off' },
    promptTokens: tv(prompt, prompt === null ? 'unavailable' : 'measured'),
    completionTokens: tv(completion, completion === null ? 'unavailable' : 'measured'),
    totalTokens: tv(total ? prompt + completion : null, total ? 'calculated' : 'unavailable'),
    cachedTokens: tv(0),
    finishReason: o.finishReason === undefined ? 'stop' : o.finishReason,
    calls: o.calls ?? 1,
    compaction: o.compaction ?? null,
  };
}

export interface UsageOptions {
  kind?: 'chat' | 'analysis' | 'document';
  refId?: string;
  metrics?: Json;
  messageCount?: number;
  compaction?: Json | null;
  items?: Json[];
  stale?: boolean;
  ctx?: number;
}

const num = (token: unknown): number => ((token as Json)['value'] as number | null) ?? 0;

/** ContextUsage coerente: percentuale e residuo ricavati dai valori misurati con la stessa formula del contratto. */
export function usageOf(o: UsageOptions = {}): Json {
  const ctx = o.ctx ?? 32768;
  const metrics = o.metrics ?? metricsOf({ ctx });
  const prompt = num(metrics['promptTokens']);
  const reserved = 4096;
  const margin = 1024;
  const items = o.items ?? [
    { key: 'system_instructions', label: 'Istruzioni di sistema', tokens: tv(Math.round(prompt * 0.3)), messageCount: null },
    { key: 'user_messages', label: 'Messaggi del PM', tokens: tv(Math.round(prompt * 0.4)), messageCount: 3 },
    { key: 'assistant_messages', label: "Risposte dell'assistente", tokens: tv(Math.round(prompt * 0.25)), messageCount: 3 },
  ];
  const attributed = items.reduce((s, i) => s + num(i['tokens']), 0);
  return {
    generatedAt: '2030-01-01T10:05:00Z',
    runtime: {
      status: 'online',
      checkedAt: '2030-01-01T10:04:00Z',
      modelId: 'Qwen3.6-35B',
      contextWindowTokens: tv(ctx),
      configuredContextWindowTokens: ctx,
      serverDefaultNPredict: null,
      windowMismatch: false,
    },
    limits: {
      safetyMarginTokens: margin,
      phases: Object.fromEntries(
        ['chat', 'analysis', 'document', 'compaction'].map((p) => [
          p,
          { maxOutputTokens: reserved, enableThinking: false, inputBudgetTokens: ctx - reserved - margin },
        ]),
      ),
    },
    last: {
      kind: o.kind ?? 'chat',
      refId: o.refId ?? 'm-2',
      at: '2030-01-01T10:04:30Z',
      metrics,
      usedPercent: Math.round((prompt / ctx) * 1000) / 10,
      outputReservedTokens: reserved,
      safetyMarginTokens: margin,
      remainingTokens: tv(ctx - prompt - reserved - margin, 'calculated'),
      staleReason: o.stale ? 'newer_messages' : null,
    },
    composition: { promptTokens: tv(prompt), items, attributedTokens: attributed, unattributedTokens: tv(prompt - attributed, 'calculated') },
    history: { messageCount: o.messageCount ?? 6, originalTokens: tv(prompt, 'calculated') },
    compaction: o.compaction ?? null,
  };
}

export const emptyUsage = (): Json => ({
  ...usageOf(),
  last: null,
  composition: null,
  history: { messageCount: 0, originalTokens: tv(null, 'unavailable') },
  compaction: null,
});

// --- Lettura di cio' che la UI mostra.

/** «32.768» (formato it-IT) → 32768; «n/d» → null. */
export function parseCount(text: string): number | null {
  const digits = text.replace(/[^0-9]/g, '');
  return digits ? Number(digits) : null;
}

export interface MetricsRow {
  ctx: number | null;
  max: number | null;
  gen: number | null;
  /** Testo visibile della riga (senza il testo per screen reader). */
  visible: string;
  running: boolean;
  truncated: boolean;
  unavailable: boolean;
}

/** Legge la riga `app-inference-metrics` contenuta nel locator (la prima, se ce n'e' piu' d'una). */
export async function readMetricsRow(scope: Locator): Promise<MetricsRow> {
  const row = scope.locator('[data-testid=inference-metrics]').first();
  const part = async (id: string, label: string): Promise<number | null> => {
    const el = row.locator(`[data-testid=metric-${id}] [aria-hidden=true]`);
    if (!(await el.count())) return null;
    return parseCount(((await el.first().textContent()) ?? '').replace(label, ''));
  };
  const running = (await row.locator('[data-testid=metrics-running]').count()) > 0;
  const unavailable = (await row.locator('[data-testid=metrics-unavailable]').count()) > 0;
  const hidden = unavailable ? 'Metriche non disponibili' : (await row.locator('[aria-hidden=true]').allTextContents()).join(' ');
  return {
    ctx: await part('ctx', 'CTX'),
    max: await part('max', 'Max output'),
    gen: await part('gen', 'Generati'),
    visible: (hidden + (running ? ' generazione in corso…' : '')).replace(/\s+/g, ' ').trim(),
    running,
    truncated: (await row.locator('[data-testid=metrics-truncated]').count()) > 0,
    unavailable,
  };
}

export interface PanelReading {
  headline: string;
  prompt: number | null;
  ctx: number | null;
  /** Percentuale mostrata (es. «64,9%» → 64.9), null se «n/d». */
  percent: number | null;
  estimated: boolean;
}

/** Legge il titolo del pannello: «Contesto: 21.256 / 32.768 token — 64,9%». */
export async function readPanel(page: Page): Promise<PanelReading> {
  const headline = ((await page.getByTestId('usage-headline').textContent()) ?? '').replace(/\s+/g, ' ').trim();
  const m = /Contesto: (≈ )?([0-9.]+|n\/d) \/ ([0-9.]+|n\/d) token — (?:≈ )?([0-9.,]+%|n\/d)/.exec(headline);
  if (!m) throw new Error(`titolo del pannello non riconosciuto: ${headline}`);
  return {
    headline,
    prompt: parseCount(m[2]),
    ctx: parseCount(m[3]),
    percent: m[4] === 'n/d' ? null : Number(m[4].replace('%', '').replace(',', '.')),
    estimated: !!m[1],
  };
}

/** Righe della composizione: chiave → token mostrati e natura. */
export async function readComposition(page: Page): Promise<Record<string, { tokens: number | null; nature: string }>> {
  const rows = page.locator('[data-testid=composition] tr[data-key]');
  const out: Record<string, { tokens: number | null; nature: string }> = {};
  for (let i = 0; i < (await rows.count()); i++) {
    const row = rows.nth(i);
    const cells = row.locator('td');
    out[(await row.getAttribute('data-key'))!] = {
      tokens: parseCount((await cells.nth(0).textContent()) ?? ''),
      nature: ((await cells.nth(1).textContent()) ?? '').trim(),
    };
  }
  return out;
}

/** Cartella degli screenshot (ignorata da Git). */
export const SCREENSHOT_DIR = process.env['E2E_SCREENSHOT_DIR'] ?? 'test-results/screens';
