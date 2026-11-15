# Evidenze M5 (2026-11-15) — verifiche reali della v11 sul DGX

Dati grezzi in [m5-evidence/](m5-evidence/). Fixture sintetiche, nessun segreto né materiale aziendale. La v11 **non è il default** e non è stata modificata.

## Ambiente
- Provider: llama.cpp **b10703-0b5be7e4a** in modalità router, endpoint interno del DGX (non riportato).
- Modello: **Qwen3.6-35B-A3B UD-Q4_K_XL** (alias `Qwen3.6-35B-Smart`), `--ctx-size 131072`, cache K/V q8_0, flash-attn, `--parallel 1`, `--n-predict 32768`, ragionamento spento dalle richieste (EnableThinking=false). Il campionamento di preset (temperatura 1.0) è sovrascritto dalle richieste: temperatura 0 nella sonda, 0,1 nei batch (come il prodotto).
- Richieste: istruzioni `document-reconcile-v6`, schema `reconcile-alias-schema-by-stage-1` della variante, `StageContract reconcile-stage-contract-1`, validazione V2.

## RH-050-A — sonda della grammatica (4 chiamate, 0 retry): **superata**
Controllo con schema completo e stesso prompt, poi schema `Compare`.

| Funzionalità | Controllo (schema completo) | Confronto v11 | Esito |
|---|---|---|---|
| `candidates.maxItems = 0` | 2 candidati | 0 candidati | Honored |
| `anyOf`: Open ⇒ `reason` non vuoto | Open con `reason` null | Open con `reason` | Honored |
| enum `focus.outcome` = {Compared, Open} | `Unresolved` | `Open` | Honored |

Hash dello schema inviato = hash congelato in tutte e 4 le chiamate. **Limite semantico:** quando il prompt induce «nessun motivo», la grammatica forza una stringa non vuota ma priva di senso (`"]}s"`): il vincolo è strutturale, non garantisce un motivo utile.

## RH-050-B — replay di 10 batch reali (10 chiamate, 0 retry): **superata**
Tutti: HTTP 200, `finish=stop`, 0 violazioni di schema, compositore e motore V2 accettano.

| Batch | Stadio | Token in/out | ms | Esito semantico |
|---|---|---|---|---|
| run5-030 | confronto | 2166/151 | 3790 | `Open` con motivo specifico, 0 candidati (in v10 era rifiutato) |
| run5-026 | foglia B | 14295/271 | 9697 | 2 candidati (v5: 16), plausibili |
| run5-027 | foglia B | 14290/181 | 8307 | 1 candidato (v5: 16) |
| run5-022 | foglia A | 14272/271 | 9779 | `items: []` dal modello; 138 item composti dal server |
| run4-034 | espansione | 14508/1794 | 35368 | 16 candidati = tetto (satura); molti **poco plausibili** |
| run4-035 | espansione | 14471/178 | 8363 | 1 candidato plausibile |
| run5-028 | riduzione B | 1956/232 | 3978 | 1 candidato Alias fra nodi, debole |
| run5-029 | cross | 1945/158 | 2751 | 1 candidato Alias, debole |
| run4-030 | confronto | 2173/165 | 2943 | `Open`, motivo corretto (30 vs 60 giorni) |
| run4-029 | confronto | 2161/187 | 3277 | `Compared` con motivo plausibile |

Criterio O2 (contaminazione fra stadi): le foglie B producono 2 e 1 candidati contro 16+16 della v5: **non osservata**. Nessuna violazione residua (criterio O1 non attivato). L'espansione con blocchi grandi produce candidati di bassa precisione fino al tetto: è fail-closed (copertura non dichiarata) ma costa token.

## RH-051 — prova integrata reale: **NON COMPLETATA (bloccata prima della v11)**
Una sola esecuzione, tetto di 200 chiamate generative, parser Docling reale (container locale di test), PostgreSQL di test.
- Configurazione (adeguamento esplicito del test, contratti invariati): W=40960, O=8192, M=1024 ⇒ B=31744. Con W=32768, O=16384 lo stadio Gap del ledger da ~170 evidenze (21.810 token misurati in M4) non entra nei 15.360 token disponibili; con O=8192 la gerarchia resta instradata (soglia ≈ 68 evidenze).
- Esito: i 3 turni PM sono passati. `af-integrazione-edi.md` è stato parsato (133 blocchi) ma la preparazione è **Failed** (`work_output_invalid`): un'unità di estrazione reale ha generato fino al limite (finish=length, 16.384 token, 280 s). Le altre unità producono 4.648–10.478 token di output per 2.300–4.500 di input.
- Mai raggiunti: controllo del budget Gap sul ledger reale, Analizza, gerarchia v11, stadio Gap. **Nessun dato v11 end-to-end e nessun confronto col gold.**
- Chiamate generative: 11 (3 chat, 8 estrazione), ~14 min 44 s; 0 bloccate dal tetto.
- Classe del difetto: **ambientale/modello nello stadio di estrazione** (output troncato), preesistente e indipendente dalla v11.
- Non rilanciata (mandato: una sola run).

## Limiti
La v11 è provata sulla grammatica e su singoli batch reali, non sulla catena completa. Il gold di RH-051 è fissato nel commit del test e non è stato valutato.
