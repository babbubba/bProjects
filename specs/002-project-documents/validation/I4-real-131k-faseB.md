# I4 reale, campagna diagnostica T067 — FASE B (Qwen3.6-35B-Smart, W=131072, Analysis reasoning ON, budget 12288, output 32768)

Esecuzione: 2026-11-03, serializzata (una sola inferenza DGX per volta), una ripetizione per test. **Esito FASE B: gate N1-N3 NON soddisfatto** (N1 PASS, N2 FAIL, N3 FAIL). T067 resta aperto; nulla spuntato in `tasks.md`. Nessuna asserzione, gold, contratto, validatore, prompt, schema o limite temporale modificato; nessuna modifica al codice rispetto alla Fase A. Dati grezzi solo nella cartella diagnostica locale non versionata (fuori dal repository); qui solo conteggi e classificazioni, nessun contenuto di reasoning (non registrato dall'harness, non letto). Fattore cambiato rispetto alla Fase A: **solo** Analysis reasoning OFF -> ON, con output massimo di Analysis 16384 -> 32768 (necessario: con reasoning ON l'output comprende il ragionamento; invariati W, profilo, sampler, parser, PG, corpus, prompt, margine 1024).

## 1. Verifica del DGX e del budget
| Voce | Evidenza |
| --- | --- |
| Profilo | `Qwen3.6-35B-Smart` (stato `loaded`), GGUF `Qwen3.6-35B-A3B-UD-Q4_K_XL`, llama.cpp b10703; argomenti effettivi (da `/v1/models`) identici alla Fase A: `--ctx-size 131072`, `--parallel 1`, `--reasoning on`, `--reasoning-budget 12288`, `--reasoning-format deepseek`, `--n-predict 32768`, `presence-penalty 1.5`, `top-k 20`, `top-p 0.95`, KV q8_0 |
| Contesto | `/props?model=`: `n_ctx 131072`, `total_slots 1`; `/slots?model=`: 1 slot, `n_ctx 131072` (riverificato prima delle prove) |
| Budget di reasoning | **Applicazione PROVATA sperimentalmente**: chiamata minima con quesito combinatorio (86 token di input, `enable_thinking=true`, `max_tokens 30000`): `completion_tokens` = 12291 con `content` di 1 carattere e `reasoning_content` separato, finish `stop` (205 s). Il ragionamento si e interrotto a ~12288 token (= budget) e la risposta e stata forzata. Conferma nei test: in N2 e N3 il reasoning stimato e 12293 e 12292 token (stesso tetto), mentre in N1 e 7865 (sotto budget, chiusura naturale). Limite della prova: non e stato eseguito un controfattuale senza budget; «il budget limita a ~12288» e provato, «il budget cambia l'esito» no |
| Semantica output | `max_tokens` (client: `max_completion_tokens` 32768) copre reasoning + risposta visibile: `completion_tokens` = reasoning + visibile (12291 = ~12288 reasoning + 3 token). `--n-predict 32768` del profilo coincide col tetto del client. Riserva per il JSON finale: 32768 - 12288 = 20480 token, a fronte di 3-6 mila token visibili osservati |
| reasoning separato | `reasoning_content` separato dal `content` (come in Fase A): MAF consuma solo `content`; il reasoning non e mai finito nel JSON |
| Accesso SSH, memoria, KV, offload | **unavailable** (come in Fase A) |
| Config modificata sul server | nessuna |

## 2. Configurazione effettiva del client (FASE B)
- Variabili solo nel processo di test: `BPROJECTS_TEST_DGX_MODEL=Qwen3.6-35B-Smart`, `BPROJECTS_TEST_CTX_WINDOW=131072`, `BPROJECTS_TEST_ANALYSIS_MAX_OUTPUT=32768`, `BPROJECTS_TEST_ANALYSIS_THINKING=1`; `SafetyMargin` default 1024 (input budget di Analysis = 131072 - 1024 - 32768 = 97280). La variabile di shell (`Qwen3.6-35B`, profilo 32k) non e stata modificata.
- Richieste di analisi osservate (`request.json`): `temperature 0.1`, `max_completion_tokens 32768`, `chat_template_kwargs.enable_thinking=true`; le altre fasi (estrazione 16384, chat/bozza 4096) con `enable_thinking=false`, come in Fase A. Il client non invia `presence_penalty`/`top_k`/`top_p` (vale il default del profilo).
- Parser Docling reale, PostgreSQL reale (55432), MAF 1.26.0, dispatcher/worker in background, timeout richiesta analisi 900 s, deadline run 1200 s (invariati).
- Filtri verificati con `--list-tests` prima dell'esecuzione: `--filter-class "*DocumentSmallFlowMafRealTests"` + `--filter-method "*N1_pdf_and_docx*"` / `"*N2_markdown*"` / `"*N3_document_vs*"` = 1 test ciascuno; **EvidenceExtractorMafTests non lanciato**.

## 3. Risultati per test (una esecuzione ciascuno)
Reasoning = `completion_tokens` meno token visibili (tokenizzazione `/tokenize` del `content`); precisione di pochi token. Memoria/KV: unavailable.

| Test | Esito | Analisi: input / output totali / visibili / reasoning (stimato) | finish | Durata analisi | Contesto occupato (in+out)/131072 | Validazione |
| --- | --- | --- | --- | --- | --- | --- |
| N1 PDF+DOCX | **PASS** (tutte le asserzioni: Ready, chat con fonti, analisi 1.1.0 con citazioni 100%, correzione PM, conferma, bozza, classificazione) | 6268 / 13968 / 6103 / ~7865 | `stop` | 236,4 s | 15,4% | analisi valida; bozza 4825 in / 1365 out / 24,1 s |
| N2 MD+TXT | **FAIL** `work_output_invalid` | 6284 / 18393 / 6100 / ~12293 (= budget) | `stop` | 319,6 s | 18,8% | 1 violazione riportata: strutturale JSON |
| N3 conflitto doc-doc + tabella PDF | **FAIL** `work_output_invalid` | 5245 / 17271 / 4979 / ~12292 (= budget) | `stop` | 301,1 s | 17,2% | 1 violazione riportata: id/riferimento (UUID) |

Durata test: N1 5 m 33 s, N2 6 m 27 s, N3 6 m 02 s. Altre fasi (tutte `stop`, mai troncate): estrazione 2-3 chiamate (294-1318 token out, 5-22 s), chat 2-4 s; documenti Ready, chat con fonti nei tre test.

## 4. Classificazione degli errori
- **N2 — strutturale JSON (campo nullo)**: il JSON e sintatticamente valido e completo (3 chiavi di primo livello, 9 voci di `evidenceMap`, 15 sezioni di `analysis`), ma `analysis.contextAndScope.overview` e `null` invece di un elenco. Il codec rifiuta alla prima violazione («`overview` ... doesn't allow setting null values»). **Il difetto di Fase A (id di FONTE al posto di EVIDENZA) NON si ripresenta**: controllo mio a posteriori, tutti i 9 `evidenceMap[].evidenceIds` sono id con etichetta «evidenza» nel prompt. Altri tipi di errore: **NOT VERIFIED** (il codec non valuta oltre il primo; la mia ispezione non sostituisce il validatore).
- **N3 — id/riferimenti (UUID troncato)**: un solo `analysis.sources[].artifactId` (fonte `DocumentPassage`) e un UUID di 35 caratteri (una cifra persa nella copia); gli altri 6 sono canonici, gli id degli item sono tutti UUID formato D (0 su 7 non canonici, contro il difetto di Fase A: etichette al posto di UUID), gli `evidenceIds` sono id di evidenza. Errore di copia di un identificativo, non di tipo. Correttezza semantica (conflitto 30/60 giorni, tabella R-01..R-05 con R-05 esclusa, citazioni): **NOT VERIFIED** (analisi scartata prima).
- Per entrambi: `finish_reason=stop`, nessun `length`, nessun loop/ripetizione, JSON non troncato, output totale 17-18 k token (< 32768), nessun errore di preflight/`runtime_unverified`/modello scaricato/contesto. Il reasoning ha raggiunto il budget in N2 e N3 (non in N1): non satura l'output e non e un fallimento per se, ma la coincidenza con i due FAIL e un'osservazione su n=2, non una causa dimostrata.

## 5. Confronto A / B (una esecuzione per cella, nessuna significativita statistica)
| Test | Fase A (reasoning OFF, out 16384) | Fase B (reasoning ON, budget 12288, out 32768) |
| --- | --- | --- |
| N1 | PASS: 6287 in / 7317 out / 126 s | PASS: 6268 in / 13968 out (6103 visibili) / 236 s |
| N2 | FAIL: id di FONTE al posto di EVIDENZA (25/25 voci), id/riferimenti | FAIL: `overview` nullo (strutturale); id di evidenza corretti |
| N3 | FAIL: id di item non UUID (14), 1 violazione | FAIL: 1 UUID di fonte troncato (35 caratteri); id di item corretti |
| Gate N1-N3 | non soddisfatto | non soddisfatto |

Lettura (osservazioni, non conclusioni): con reasoning ON il tipo di errore cambia in entrambi i casi che falliscono (da id di tipo sbagliato / id non UUID a campo nullo / UUID con una cifra persa), ma il numero di analisi valide resta 1/3. Costo: output visibile invariato (~5-6 k) piu 8-12 k token di reasoning, durata 2-2,5 volte (236-320 s contro 107-155 s). Non si attribuisce al budget alcun effetto sull'esito (nessun controfattuale senza budget).

## 6. Metriche unavailable / NOT VERIFIED
Memoria libera/usata e KV cache; occupazione CPU/offload; durata di caricamento; conteggio esatto dei token di reasoning (stimato per differenza, il server non lo riporta separatamente); contenuto del reasoning (non letto); valutazione semantica e di eventuali ulteriori errori strutturali/id oltre la prima violazione in N2 e N3 (codec fail-fast); effetto controfattuale del budget; ripetizioni (n=1 per test, nessuna stima di varianza).

## 7. Note di esecuzione
Nessun commit, push, modifica di `tasks.md` o di `docs/acceptance-trace/`. Nessuna modifica al codice in Fase B (parametrizzazione ambientale gia presente dalla Fase A). Dati grezzi nella cartella diagnostica locale non versionata (scratchpad della sessione).
