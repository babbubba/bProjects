# Prestazioni (solo la prosecuzione, 22:54:19Z → 23:10:45Z)

| Fase | Tempo | Quota | Note |
|---|---|---|---|
| Totale | 985,1 s | 100 % | `formal.total` 982,9 s |
| Claim del dispatcher | 3 s | 0,3 % | dalla riapertura al `Running` (fence 2) |
| Recovery di `expand 30` (`port.persist` n. 1) | 257,1 s | 26 % | 0 chiamate al modello; **non scomposto** (vedi sotto) |
| Inferenza DGX (HTTP, 43 chiamate) | 591,9 s | 60 % | `expand` 16 × 32,1 s medi (max 35,4 s); `compare` 27 × 2,9 s (max 4,1 s) |
| Persistenza/commit dei 43 batch (`port.persist` n. 2–44) | 39,5 s | 4 % | media 0,9 s |
| `port.lookup` (86) | 24,4 s | 2,5 % | medio 283 ms |
| `port.prepare` (43) | 23,4 s | 2,4 % | medio 545 ms |
| `checkpoint.create` (215) | 2,5 s | 0,3 % | |
| `port.reserveCall` / `callTimeout` | 0,4 s / 0,1 s | trascurabile | |

Le percentuali non sommano a 100 perché alcuni intervalli si sovrappongono o non sono strumentati; il residuo (≈ 2 %) è **non attribuito**. Tempo in coda, preflight e Gap: non misurati (Gap non raggiunto; la coda prima del claim è di 3 s). Il tokenizer e il tempo di validazione/composizione non hanno uno span dedicato: la validazione è dentro `port.persist`; il tokenizer dentro `port.prepare`/replay (tempo non separabile con la strumentazione esistente).

## Token (solo le 43 nuove chiamate)
| Stadio | Chiamate | Prompt | Completion | Tempo HTTP |
|---|---|---|---|---|
| compare | 27 | 58.641 | 4.336 | 78,9 s |
| expand | 16 | 106.167 | 29.137 | 512,9 s |
| **Totale** | **43** | **164.808** (75.726 in cache) | **33.473** | **591,9 s** |

Velocità di generazione `expand` ≈ 60 token/s (esempio `timings`: 1.746 token in 28,9 s), prefill ≈ 2.500 token/s. Limite di output richiesto 8.192 per tutte; mai raggiunto (massimo osservato ≈ 2.000).

## Osservazioni
- Il collo di bottiglia del lavoro generativo è l'`expand` (≈ 32 s per chiamata); i `compare` sono ~11 volte più rapidi.
- Il costo fisso di ripresa (≈ 257 s senza chiamate) è comparabile a 8 chiamate `expand`: va attribuito prima di ripetere riprese frequenti.
- Dati completi in `performance.json` e `timeline.jsonl` (530 span, 77 transizioni di stato, 43 coppie `call_start`/`call_end`).
