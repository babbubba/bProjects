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

## Diagnosi del blocco di estrazione (2026-11-15, solo dati esistenti, 0 nuove inferenze)
Fonte: telemetria `bprojects-diag` del run (richieste, risposte, `usage`, `timings`) e `/tokenize` non generativo.

| Unità | Primary (OwnedTokens) | Input totale | Output | Output/Owned | Evidenze | finish |
|---|---|---|---|---|---|---|
| 008 | 780 | 3937 | 9689 | 12,4 | 34 | stop |
| 009 | 1005 | 4527 | 10478 | 10,4 | 37 | stop |
| 010 | 406 | 3076 | 4648 | 11,4 | 17 | stop |
| **011 (fallita)** | **916** | 3420 | **16384** | **17,9 (troncata)** | 47 (a metà della 3ª tabella) | **length** |

- Limite richiesto: `max_completion_tokens` 16384 (Extraction); `--n-predict` del server 32768, quindi il limite applicato è quello della richiesta. Prompt di sistema+schema ≈ 2060 token (prefisso in cache per ~2065 token).
- `ExtractionOutputExpansion` effettivo = 16 (default, candidato T040). `Fits`: 916 × 16 = 14.656 ≤ 16.384, quindi il planner ha ammesso l'unità.
- **Thinking: spento, verificato.** Il template della build b10703 rispetta `chat_template_kwargs.enable_thinking=false` (emette `<think>` vuoto); nessun `reasoning_content`, nessun tag `<think>`; i token di completamento coincidono con il contenuto (≈3,2–3,4 caratteri per token in tutte le chiamate). Nessun consumo di ragionamento.
- L'unità 011 contiene **tre tabelle di 10 righe** (ORDERS, DESADV, INVOIC). Il modello scompone ogni riga in **2 evidenze** (tipo/lunghezza/obbligatorio e nota), ciascuna con fonte di riga + intestazione `Context`: ≈ 1.130 caratteri ≈ 350 token per evidenza. Le 47 evidenze emesse sono fedeli, senza ripetizioni (2 enunciati duplicati su 47, ciascuno riferito a una riga diversa), con citazioni letterali (le 23 citazioni ripetute sono l'intestazione `Context`). Fabbisogno stimato: ≈ 60 evidenze ≈ 21.000 token ≈ 23× OwnedTokens.
- **Causa: A + D, non C.** Estrazione corretta e densa; il fattore 16 del planner, calibrato su testo discorsivo (T040), sottostima le unità composte solo da tabelle (≈ 23×). Non è sovraestrazione ripetitiva (B) né thinking (C). Il fallimento è terminale (`work_output_invalid`, nessun retry automatico) e interrompe l'intera preparazione.
- Mancante: il testo dell'output è recuperabile fino al troncamento; la stima di 60 evidenze/21.000 token è un'estrapolazione, non misurata.

**Raccomandazione.** Modifica minima per ripetere RH-051: solo configurazione del test, `Ai:ExtractionOutputExpansion` = **24** (ammesso 1–64; il banco lo legge da `BPROJECTS_TEST_EXTRACTION_OUTPUT_EXPANSION`). Con 24 le unità con Owned > 682 token si dividono (la 011 con Owned 916 non passa più; le tre tabelle sono intervalli distinti). Nessuna modifica di `MaxOutputTokens`, finestra, prompt o contratti. Stima d'impatto: +1–3 chiamate di estrazione e +3–6 minuti. Rischio sulle evidenze: nessuno sulla fedeltà (intestazioni `Context` replicate per intervallo); cambia però la dimensione del ledger, che con ≈ 2 evidenze per riga di tabella sarà verosimilmente 250–300 (non 160–190), quindi lo stadio Gap supera B=31.744: per RH-051 serve W=65536 (B=56.320), O=8192, M=1024, oppure il controllo preventivo di budget fermerà la run a zero invii. Il valore 16 predefinito del prodotto resta invariato: rivederne la calibrazione per le tabelle è una decisione separata (nuova versione di calibrazione, non decisa qui).

## RH-051 — seconda ed ultima esecuzione (2026-11-15): **di nuovo bloccata nell'estrazione, prima della v11**
Dati in [m5-evidence/run2/](m5-evidence/run2/). Configurazione: W=65536, O=8192, M=1024 (B=56320), `ExtractionOutputExpansion=24` (verificato prima dell'upload da `RejectOptions` e nel piano congelato), Extraction `MaxOutputTokens=16384`, thinking spento. Tetto 200 chiamate generative; usate 12 (3 chat, 9 estrazione), ~14 min. Non rilanciata.
- Il fattore 24 ha funzionato: l'unità a tre tabelle è stata divisa e **nessuna chiamata è stata troncata** (tutte `finish=stop`; la più lunga 13.488 token, 229 s).
- **Nuovo difetto:** la preparazione di `af-integrazione-edi.md` è `Failed` (`work_output_invalid`, 34 violazioni): *«fonti elencate fuori ordine di part (ricevute [1,0])»*. In **tutte e 34** le evidenze della tabella il modello elenca prima la riga (`part` 1, `Primary`) e poi l'intestazione (`part` 0, `Context`); la validazione server (`DocumentEvidenceValidation`) richiede l'ordine crescente di `part`. È lo stesso schema già visibile nell'output troncato della prima run. Il validatore rifiuta l'intera unità e l'estrazione non ha un retry guidato (esiste solo per l'analisi), quindi la preparazione del documento fallisce.
- Classe: output non conforme del modello rispetto a una regola che lo schema JSON non esprime (ordine degli elementi di `sources`). **Non** è un difetto di v11, grammatica, budget o thinking. Contenuto dell'output: fedele (una evidenza per riga o nota, citazioni letterali), solo l'ordine è invertito.
- Non raggiunti, di nuovo: controllo budget Gap sul ledger reale, Analizza, gerarchia v11, stadio Gap, confronto col gold. **Nessun dato v11 end-to-end.**
- Opzioni (non applicate, richiedono decisione): (a) normalizzare l'ordine delle fonti nel server prima della validazione (modifica del prodotto, evidenze invariate); (b) retry guidato dell'estrazione; (c) istruire il prompt (modifica del prompt congelato). La più piccola e meno rischiosa è (a), perché l'ordine non cambia il significato e la regola è deterministica.

## Limiti
La v11 è provata sulla grammatica e su singoli batch reali, non sulla catena completa. Il gold di RH-051 è fissato nel commit del test e non è stato valutato.
