# I4 reale, campagna diagnostica T067 — FASE A (Qwen3.6-35B-Smart, W=131072, Analysis reasoning OFF)

Esecuzione: 2026-11-03, serializzata (una sola inferenza DGX per volta). **Esito FASE A: gate NON soddisfatto** (N1 PASS, N2 FAIL, N3 FAIL). T067 resta aperto; nulla spuntato in `tasks.md`. Nessuna asserzione, gold, contratto, validatore, prompt o limite temporale modificato. Dati grezzi (prompt/risposte) solo nella cartella diagnostica locale non versionata; qui solo conteggi e classificazioni. Non integra né sostituisce `I4-real.md`.

## 1. Verifica preliminare del DGX (HTTP; SSH non disponibile)
| Voce | Evidenza |
| --- | --- |
| llama.cpp | `build_info` b10703-0b5be7e4a, router mode (`/props` senza modello: `role=router`, `max_instances=4`, `models_autoload=true`) |
| Accesso SSH | **unavailable** (publickey/password, nessuna credenziale configurata): `models.ini` non letto direttamente; letti gli argomenti effettivi e il preset da `/v1/models` |
| Nomi profilo | case-sensitive, confermati: `Qwen3.6-35B-Smart`, `Qwen3.8-Flash-Next-Smart`. Il valore della shell `BPROJECTS_TEST_DGX_MODEL` era `Qwen3.6-35B` (profilo baseline 32768): per la FASE A impostato `Qwen3.6-35B-Smart` solo nel processo di test |
| GGUF `Qwen3.6-35B-Smart` | `/home/italcom/models/Qwen3.6-35B-A3B-GGUF/Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf` (`/props`: `model_ftype` Q4_K - Medium) = atteso |
| Argomenti effettivi Smart | `--ctx-size 131072`, `--parallel 1`, `--flash-attn on`, `--cache-type-k/v q8_0`, `--batch-size 2048`, `--ubatch-size 1024`, `--n-predict 32768`, `--n-gpu-layers all`, `--no-mmap`, `--jinja`, `--reasoning on`, `--reasoning-budget 12288`, `--reasoning-format deepseek`, `--sleep-idle-seconds 900`; campionamento di default del profilo: `temperature 1.0`, `top-k 20`, `top-p 0.95`, `min-p 0.0`, **`presence-penalty 1.5`**, `repeat-penalty 1.0`. Tutto = atteso |
| Contesto effettivo | `/props?model=...`: `default_generation_settings.n_ctx = 131072`, `total_slots 1`; `/slots?model=...`: 1 slot, `n_ctx 131072`. **W=131072 verificato** (nessuna riduzione di contesto osservata) |
| Offloading CPU | `--n-gpu-layers all` (nessun offload dichiarato); occupazione reale **unavailable** (no nvidia-smi, `/metrics` risponde 501 «start with --metrics») |
| Memoria libera/usata, KV cache | **unavailable** (nessun accesso al host, nessun endpoint). Stima teorica non misurata, non dichiarata come evidenza |
| reasoning-budget | Presente negli argomenti (12288); la template Qwen3.6 supporta `enable_thinking` (`false` => blocco `<think></think>` vuoto). **Applicazione del budget NON provata** (richiede una generazione con ragionamento che lo superi: non eseguita) |
| reasoning separato | Prova minima con `enable_thinking=true` (387 token, 6,5 s): risposta con `reasoning_content` separato (963 caratteri) e `content` di 3 caratteri => il ragionamento NON entra nel `content` consumato da MAF. Nota: `/props` mostra `reasoning_format "none"` nei default di generazione, ma il comportamento osservato e quello con campo separato |
| `Qwen3.8-Flash-Next-Smart` | solo ispezione, non caricato: `ctx-size 131072`, **KV f16** (non q8_0), `--fit off`, `--n-predict 32768`, `--reasoning on`, `--reasoning-budget 12288`, `--reasoning-format deepseek`, `presence-penalty 0.0`, `seed 4242`, batch 2048/ubatch 512, GGUF UD-IQ4_XS in 3 shard. Stato `unloaded` |
| Warm-up | `props?model=` ha caricato il profilo (durata di caricamento non misurata: la chiamata non era cronometrata). Warm-up del test (16 token, thinking OFF): 0,16-0,23 s, `stop`, in tutte le esecuzioni (profilo gia caldo). Stato router al termine: Smart `loaded`; baseline `Qwen3.6-35B` e `Qwen3.5-35B` `sleeping` |
| Config modificata sul server | **nessuna** (nessun intervento su models.ini o servizi) |

## 2. Configurazione effettiva client MAF (FASE A)
- Modello `Qwen3.6-35B-Smart` per tutte le fasi; `ContextWindowTokens=131072`; `SafetyMarginTokens=1024`.
- Analysis: `MaxOutputTokens=16384`, ragionamento OFF (`chat_template_kwargs.enable_thinking=false`), timeout richiesta 900 s, input budget 113664, deadline run 1200 s. Chat/Document/Compaction: 4096 token, OFF; Extraction: 16384 token, OFF (come compose/baseline).
- Schema/prompt/corpus/validator invariati; parser Docling reale (127.0.0.1:58300), PG reale (55432), MAF 1.26.0, dispatcher/worker in background come `Program.cs`.
- Sampler: il client invia solo `temperature` (0,1 in analisi); `presence_penalty`, `top_k`, `top_p` NON sono inviati, quindi vale il default del server Smart (`presence-penalty 1.5`, top-k 20, top-p 0,95).
- **Parametrizzazione del test (consentita, dichiarata)**: in `backend/tests/bProjects.IntegrationTests/DocumentSmallFlowMafRealTests.cs`, `RealOptions()` legge `BPROJECTS_TEST_CTX_WINDOW`, `BPROJECTS_TEST_ANALYSIS_MAX_OUTPUT`, `BPROJECTS_TEST_ANALYSIS_THINKING` (1 = ON), `BPROJECTS_TEST_SAFETY_MARGIN`. Default invariati (32768 / 16384 / OFF / 1024 = baseline). Nessuna asserzione toccata. Non committato.

## 3. Risultati per test (una esecuzione ciascuno)
Contesto occupato = token di input della chiamata di analisi / 131072. Memoria/KV: unavailable.

| Test | Esito | Analisi: input / output (visibili) / finish | Durata analisi | Occupazione contesto | Validazione e classificazione errore |
| --- | --- | --- | --- | --- | --- |
| N1 PDF+DOCX | **PASS** (tutte le asserzioni del test: Ready, chat con fonti, analisi 1.1.0 con 100% citazioni, correzione PM, conferma, bozza Proposed/NotApproved, classificazione) | 6287 / 7317 / `stop` (nessun `length`) | 126 s | 4,8% (+ 7317 out = 10,4% della finestra) | Validazione strutturale e semantica del test: PASS. Bozza: 4795 in / 1219 out / 21,6 s. Chat 2,8 s e 0,9 s |
| N2 MD+TXT | **FAIL** | 6332 / 8887 / `stop` | 154,7 s | 4,8% (+ out 11,6%) | `work_output_invalid`, 25 violazioni, **tutte dello stesso tipo: id/riferimenti**. Vedi sotto |
| N3 conflitto doc-doc + tabella PDF | **FAIL** | 5267 / 6442 / `stop` | 107,5 s | 4,0% (+ out 8,9%) | `work_output_invalid`, 1 violazione: **strutturale/id** (UUID non valido). Vedi sotto |

Altre fasi, tutte `stop` e non troncate: estrazione 2-3 chiamate (1-1,4 k token out, 18-22 s) e chat (2-5 s) in tutti e tre i test; tutti i documenti Ready, chat con fonti reali nei tre test.

### Classificazione errori (per tipo)
- **N2 — id/riferimenti (fonte invece di evidenza)**: il JSON e strutturalmente valido e completo (3 `results`, 15 sezioni di `analysis`, 9 requisiti, 7 vincoli, 9 fonti, 25 voci di `evidenceMap`). Tutte e 25 le voci `evidenceMap[].evidenceIds` contengono l'id di una FONTE (9 id distinti, tutti presenti nel prompt con etichetta «fonte») invece dell'id dell'EVIDENZA (9 id con etichetta «evidenza»); anche `evidenceRefs` degli item (16 su 16) usano id di fonte. Il validatore risponde «evidenza ... fuori snapshot» per 25/25. Stessa famiglia gia vista in I4-real.md (punto 4: «id di FONTE al posto di id di EVIDENZA»). Non e un errore di copia di cifre: gli id sono corretti e presenti, ma di tipo sbagliato. Nessun errore di contesto/output/runtime/reasoning.
- **N3 — strutturale/id (UUID non canonico)**: 14 id di item generati dal modello non sono UUID formato D lowercase (etichette di 14-23 caratteri in `requirements[].id`, `constraints[].id`, `assumptions[].id`, `openQuestions[].id`, `conflicts[].id`, `conflicts[].alternatives[].id` e nelle relative `evidenceMap[].itemId`). Il codec rifiuta alla prima violazione (1 sola riportata): **la correttezza semantica (conflitto 30/60 giorni, tabella R-01..R-05 con R-05 esclusa, citazioni) NON e stata valutata** perche l'analisi e scartata prima. Nessun errore di contesto/output/runtime/reasoning. Stessa famiglia di I4-real.md punto 3/4 («id nuovo ambiguo», item con `id` non UUID).
- Per entrambi: `finish_reason=stop`, nessuna ripetizione/ciclo, nessun `length`, nessun errore di preflight/`runtime_unverified`/modello scaricato (warm-up ok), il reasoning non e stato usato (OFF).

### Correttezza semantica (evidenceRefs / citazioni / conflitti)
- N1: verificata dal test e PASS (citazioni 100% = id evidenza + blocco + citazione esatta; attese semantiche e negazioni; Declared/Documented coerenti). Questa e la prima analisi documentale reale che attraversa conferma + Genera con il modello in tutte le campagne T067.
- N2, N3: **NOT VERIFIED** (analisi rifiutata dalla validazione strutturale/referenziale prima del controllo semantico).

## 4. Osservazioni diagnostiche (ipotesi non provate, utili alla fase successiva)
1. Rispetto al baseline W=32768 (1/25 analisi valide) la FASE A ottiene 1/3: segnale migliorativo ma non campione sufficiente; gli errori residui sono ancora di tipo id/riferimenti, mai di contesto o lunghezza.
2. Il profilo Smart impone `presence-penalty 1.5` e `top-k 20/top-p 0,95` lato server; il client non li sovrascrive. Una penalita di presenza alta scoraggia la riproduzione letterale di token gia visti (UUID/hash copiati dal prompt) ed e un candidato per gli errori di tipo id. **Ipotesi, non misurata**: da isolare in FASE B con una variabile alla volta (richiede invio di `presence_penalty` dal client o preset temporaneo ripristinabile; nessuno dei due e stato applicato qui).
3. Il ragionamento ON non e stato provato in questa fase (da decisione, OFF).

## 5. Metriche unavailable
Memoria libera/usata e occupazione KV cache (nessuna via di misura), durata di caricamento del modello, conteggio token di ragionamento (con OFF non osservato; il harness non registra `reasoning_content`), applicazione del `--reasoning-budget`, valutazione semantica N2/N3, `models.ini` letto da file (solo preset/argomenti via API).

## 6. Note di esecuzione
- Il filtro `--filter-method "*N1_*"` / `"*N3_*"` ha incluso anche `EvidenceExtractorMafTests.N1_*` / `N3_*` (stesso DGX, in sequenza, non in parallelo): 2 test per run; entrambi PASS, fuori dal perimetro della campagna. Per N2 filtro `*N2_markdown*` (1 test).
- Nessun commit, push, modifica di `tasks.md` o di `docs/acceptance-trace/`.
