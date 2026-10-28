import { ContextUsage, InferenceMetrics, TokenValue } from './project-api.service';

/** Fixture conformi al contratto HTTP «Metriche di inferenza e monitor del contesto» (solo per i test). */
export const measured = (value: number): TokenValue => ({ value, source: 'measured' });
export const calculated = (value: number): TokenValue => ({ value, source: 'calculated' });
export const estimated = (value: number): TokenValue => ({ value, source: 'estimated' });
export const unavailable: TokenValue = { value: null, source: 'unavailable' };

export function metrics(over: Partial<InferenceMetrics> = {}): InferenceMetrics {
  return {
    state: 'completed',
    phase: 'chat',
    modelId: 'qwen3-32b',
    startedAt: '2030-01-01T10:00:00Z',
    durationMs: 4200,
    contextWindowTokens: measured(49152),
    maxOutputTokens: { value: 8192, confirmed: true },
    serverDefaultNPredict: -1,
    reasoning: { requested: true, effective: 'on' },
    promptTokens: measured(21256),
    completionTokens: measured(1347),
    totalTokens: calculated(22603),
    cachedTokens: measured(0),
    finishReason: 'stop',
    calls: 1,
    compaction: null,
    ...over,
  };
}

export function contextUsage(over: Partial<ContextUsage> = {}): ContextUsage {
  return {
    generatedAt: '2030-01-01T10:00:05Z',
    runtime: {
      status: 'online',
      checkedAt: '2030-01-01T10:00:04Z',
      modelId: 'qwen3-32b',
      contextWindowTokens: measured(32768),
      configuredContextWindowTokens: 32768,
      serverDefaultNPredict: -1,
      windowMismatch: false,
    },
    limits: {
      safetyMarginTokens: 512,
      phases: {
        chat: { maxOutputTokens: 8192, enableThinking: true, inputBudgetTokens: 24064 },
        analysis: { maxOutputTokens: 8192, enableThinking: false, inputBudgetTokens: 24064 },
        document: { maxOutputTokens: 8192, enableThinking: false, inputBudgetTokens: 24064 },
        compaction: { maxOutputTokens: 2048, enableThinking: false, inputBudgetTokens: 30208 },
      },
    },
    last: {
      kind: 'chat',
      refId: 'm-2',
      at: '2030-01-01T10:00:04Z',
      metrics: metrics({ contextWindowTokens: measured(32768) }),
      usedPercent: 64.9,
      outputReservedTokens: 8192,
      safetyMarginTokens: 512,
      remainingTokens: calculated(2808),
      staleReason: null,
    },
    composition: {
      promptTokens: measured(21256),
      items: [
        { key: 'system_instructions', label: 'Istruzioni di sistema', tokens: measured(1200), messageCount: null },
        { key: 'user_messages', label: 'Messaggi del PM', tokens: measured(9800), messageCount: 12 },
        { key: 'assistant_messages', label: "Risposte dell'assistente", tokens: measured(8000), messageCount: 11 },
        { key: 'derived_summary', label: 'Riassunto derivato', tokens: estimated(1500), messageCount: 40 },
      ],
      attributedTokens: 20500,
      unattributedTokens: calculated(756),
    },
    history: { messageCount: 342, originalTokens: measured(180000) },
    compaction: {
      at: '2030-01-01T09:00:00Z',
      tokensBefore: measured(30000),
      tokensAfter: measured(12000),
      tokensSaved: calculated(18000),
      messagesSummarized: 40,
    },
    ...over,
  };
}
