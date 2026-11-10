# M3 — prima campagna reale T040 (retrieval → stadio Gap)

- Data: 2026-11-10, una sola esecuzione autorizzata (solo T040, `BPROJECTS_TEST_DGX_ALLOW=1`); nessun retry, nessuna modifica di configurazione, gold, soglie, attese, prompt o schemi.
- Commit di partenza `ce2c66e` (+ correzione asserzione del rischio A, nello stesso commit di questo rapporto). Manifesto `T040-campaign-manifest.json` v2, hash congelato `fbc0ecb348c8568bf8f799a0c1c4b79161087e95c2f2f2621fb2f713da79eefb` (verificato dai 44 test offline `RetrievalGapCampaignTests`, superati prima del run).
- Comando: `dotnet test --solution bProjects.sln --no-build -- --filter-class "*RetrievalGapStageMafRealTests*"` (selezionato un solo test: `RetrievalGapStageMafRealTests+Campaign.T040_…`). Durata 5 min 46 s.

## Configurazione effettiva e prerequisiti
- Definizione v7, politica `retrieval-policy-1`, LLM `Qwen3.6-35B-Smart`, embedding `bge-m3-Q5`, reranker `bge-reranker-v2-m3` (tutti e tre presenti nel router, stato `sleeping`). Profilo misurato: W=32768, O=16384, M=1024, thinking=off ⇒ B=15360 (compatibile, `RejectProfile` superato).
- PostgreSQL di test (`bprojects-test-pg`, porta 15432) e parser reale (`bprojects-t055-parser`, riavviato per il run, health 200) disponibili. Sonde non generative: solo `GET /models` del router.

## Verifiche preliminari dei due rischi
- **A (Gap senza candidati)**: `CandidateReranker.OrderAsync` non invia nulla se non ci sono fusioni ⇒ un Gap senza candidati legittimamente non chiama il reranker. L'asserzione per Gap pretendeva sempre ≥1 chiamata di riordino: corretta (`GapLatency.IsMeasuredCompletely(candidati)`: riordino obbligatorio solo con candidati) con test offline mirato. Motore invariato, manifesto invariato.
- **B (ledger oltre budget)**: in `DocumentAnalysisWorkflow.RunStagedAsync` la modalità retrieval si attiva solo se `GapStageBudget.Evaluate` dice che il ledger integrale NON entra in B (`gap_ledger_exceeds_input`); se entra, il run resta esaustivo e non esiste alcun batch `retrieval`. Il controllo (1) del test richiede esattamente un batch `retrieval`, quindi un instradamento esaustivo sarebbe FAIL, mai PASS. Nessuna modifica. **Riserva**: il corpus Single congelato ha 5570 token di testo (0,36×B) con riempitivo zero; non è dimostrato prima del run che il ledger che ne deriva superi B. Da verificare quando la fase di estrazione sarà superata.

## Risultati
| Scenario | Esito | Dettaglio |
|---|---|---|
| Single | **Blocked** (13 verifiche PASS / 1 FAIL) | preparazione di `capitolato-aurora.txt` (117 blocchi, parser reale OK) fallita: un'unità `ExtractUnit` ha prodotto `finish=length` con 16384 token di completamento (prompt 4388, 282 s) ⇒ `work_output_invalid`, altre unità `parent_failed`. Altre 3 chiamate di estrazione dello stesso documento terminate `finish=stop` (257–310 token). |
| Multi | **NotRun** | non eseguito per regola del sequencer (blocco COMPLETAMENTO di Single). |

Superate prima del blocco: configurazione/profilo, corpus generato (hash e testo = manifesto), upload 201, 5 dichiarazioni del PM.
**Non raggiunti, quindi NOT VERIFIED**: indicizzazione (tempo), routing verso retrieval, candidati per Gap, esclusioni, dimensione pacchetto, stadio Gap, citazioni, conflitti, falsi `AnsweredInSnapshot`, latenze per Gap e condivise, token dello stadio. Nessuna misura di retrieval è stata prodotta.

## Classificazione e limiti
- Il fallimento è nella fase di estrazione del documento (precedente al retrieval), non una violazione dei criteri T040 nel merito. Sintomo coerente con un output non terminante del modello (generazione fino al limite O su un input di 4,4k token) — **causa non diagnosticata**: non è dimostrato se sia comportamento del modello, del prompt/schema di estrazione o del router; senza nuove inferenze (non autorizzate) non si attribuisce. Classe: FAIL non attribuito (semantico/applicativo da accertare); non infrastrutturale (servizi raggiungibili, nessun timeout/5xx).
- Limite del run: la preparazione non ha un retry nel harness (per istruzione); lo stato del sequencer è persistito.

## Artefatti
- `T040-run1-summary.txt` (esito e verifiche), `T040-run1-state.json` (stato sequencer); log completo (12,9k righe di EF SQL, nessun segreto) in locale: `%TEMP%\claude\…\scratchpad\t040-run.log`; corpus e hash: `%TEMP%\bprojects-i5-real\T040-single-20261110203427\`.

## Verdetto
**T040: NOT PASS (non verificato nel merito).** Single bloccato in estrazione, Multi non eseguito, nessun criterio di retrieval misurato. T040 resta aperto in `tasks.md`.

## Diagnosi offline del Run 1 (2026-11-10, nessuna inferenza, nessuna ripetizione)

Fonti: `diag/20261110-203418-68a02077/{summary.json,calls.jsonl,texts/0001-…-finish-length.txt}` (locali, `%TEMP%\bprojects-i5-real`), `T040-run1-*`; il DB di test non è stato usato. Il testo del prompt non è conservato (solo hash `4ddd4810…`, 18775 caratteri); non ricostruito. Revisione `ecc:csharp-reviewer` limitata al percorso di estrazione.

**Fatti osservati**
- Unità fallita: `ExtractUnit` job `a38ea178…` (unità `1559e77d…`), batch `e081fdae…` ord. 0 stage `extract`, porzioni [0,1], 32 range tutti Primary (`c1:1–19`, `c2:1–13`), `OwnedTokens`=1430 (5464 car.), payload misurato 5202, prompt riportato 4388. Le altre 3 unità `parent_failed` (0 tentativi).
- Schema `document_evidence_extraction` (`evidence-1.1.0`, hash `9eea16a6…`); nome/hash del prompt non ricavabili oltre l'hash. Limite richiesto O=16384, W=32768, B=15360; thinking off.
- Risposta: `finish=length`, 16384 token, 53359 car., 282 s, JSON non parsabile, tagliato a metà dell'evidenza e58. 58 evidenze con chiave `e1…e58` e citazioni **tutte distinte**, ordinate per range (`c1:3 … c2:9`; i range `c2:10–13` non raggiunti), 57 `Requirement` + 1 `Constraint`, 0 relazioni, 0 `Other`, nessun ciclo né duplicato. ~920 car. (~282 token) per evidenza: JSON indentato con campi `null`, una evidenza per frase.
- Le "tre estrazioni regolari" del riepilogo (prompt 2,3–2,5k, completion 257–310) non sono unità documento: sono le estrazioni delle 5 dichiarazioni PM in chat; non confrontabili. Nessuna chiamata di unità documento completata nel Run 1.
- Pianificatore: stima ceil(1430×8)=11440 ≤ O → unità non divisa (nessun difetto di calcolo: `Fits`, `OwnedTokens` solo Primary, B=W−O−M coerenti; ExtractionDiagnostics usa lo stesso 8.0). Generato/previsto ≥ 1,43; ≥ 11,46 token per token posseduto (limite inferiore: l'output è troncato).
- Stesso schema del run T082 (`I5-single-real.md`, ≥12,6 token/token, 16384 troncati), ma lì le evidenze erano `Other` di riempitivo; **qui sono tutte regole reali**, quindi la causa «estrazione del non pertinente» di quel run non si applica.
- Nessun indizio di anomalia del modello/router: modello richiesto = risposto (`Qwen3.6-35B-Smart`), fingerprint stabile, nessun errore HTTP, output ben formato fino al taglio.

**Causa (ordinata per probabilità; ipotesi, non dimostrate)**
1. (alta) `OutputExpansion`=8.0 non calibrato per testo denso di regole: il corpus T040 ha una regola per frase; ~72 frasi nell'unità × ~282 token ≈ 20k > O. Il fattore necessario sarebbe ≈ 14 (stima da conteggio, non misura). Coerente con il «~14–20» già annotato in I5-single-real e con la calibrazione I3 (2,65 su testo narrativo, ≥12,8 su unità piccole).
2. (media) Nessun recupero dopo `finish=length`: `BatchSender.SendAsync` lancia `work_output_invalid` e la partizione è calcolata una sola volta (`DocumentEvidenceAgent.cs:142`); un fattore errato fallisce l'intera unità.
3. (bassa) Verbosità del formato (indentazione, campi `null`) che gonfia i token per evidenza: non verificabile senza prompt e senza nuova inferenza.

**Verifica del budget del ledger (offline)**: il testo grezzo è 5570 token = 0,36×B; il vincolo «testo > 2×B» non è né richiesto da T040 né realizzabile senza alterare corpus/gold. L'asserzione `[STRUTTURALE] … supera 2x` con `MinimumRatio=0` (`x > 0`, sempre vera) era un PASS privo di significato: ora emette solo una nota `[MISURA]`; per T082/T083 (2,0) resta un controllo reale. Offline dimostrato (3 nuovi test in `RetrievalGapCampaignTests`, 47/47 verdi): testo < 2×B (stima per eccesso) e ledger stimato per difetto > B in entrambi i corpus. **Non** dimostrato offline: numero/dimensione reali delle evidenze, che l'estrazione completi, che il recupero venga attivato dal ledger reale.

**Difetto deterministico**: nessuno dimostrato nel codice; nessuna modifica a prompt, schema, parametri, planner, retry, corpus, gold o retrieval.

**Proposte (richiedono decisione, non applicate)**: (a) ricalibrare `OutputExpansion` a ≥14 (più batch, più chiamate) o ricavarlo da misura; (b) retry/divisione dell'unità dopo `finish=length` (nuovo perimetro, contratto §2.3); (c) per rendere T040 eseguibile a W=32768: unità più piccole via (a), oppure estrazione con output compatto. Regressione: caratterizzazione `Characterization_run1_…` + test del planner a fattore calibrato. T040 resta **aperto**, nessuna seconda campagna.

## Calibrazione preventiva del planner di estrazione (decisione del committente, 2026-11-10; nessuna inferenza)

**Scelta**: ricalibrazione preventiva (non retry dopo `finish=length`). `OutputExpansion` per l'estrazione dei documenti = **16, valore CANDIDATO, non validato**.

**Implementazione** (esplicita, configurabile, versionata): `Ai:ExtractionOutputExpansion` (`AI_EXTRACTION_OUTPUT_EXPANSION`, compose + `.env.example`), default `PlannerLimits.ExtractionCandidateOutputExpansion`=16, validato 1–64; `DocumentEvidenceAgent` lo passa al planner (prima: default 8 nel codice). Il fattore e l'etichetta `PlannerLimits.ExtractionCalibrationVersion` (`extraction-output-expansion-2026-11-10`) entrano nell'hash di configurazione del manifesto (il fattore era già nel piano). `PlannerLimits.DefaultOutputExpansion`=8 resta per gli altri chiamanti (stadi con fattore proprio: bozza 1,5, chat lunga/riconciliazione 1,0).

**Impatto sui contratti/compatibilità (verificato dal codice)**: i contratti 002 non fissano il valore 8. I documenti già `Ready` non sono toccati: l'aggregazione controlla solo PolicyHash/DefinitionVersion/AlgorithmVersion (`DocumentEvidenceAggregate.cs:96`); `AlgorithmVersion` (`portion-planner-v1`) **non** è stato incrementato (lo avrebbe reso incompatibile con tutti i documenti preparati). Un'unità già avviata con manifesto pubblicato col vecchio fattore, riavviata dopo il cambio, fallisce in modo esplicito con `IntegrityConflict` (`DocumentWorkStore.PublishManifestAsync`): nessun riuso silenzioso di un piano diverso. Anche con il fattore riportato a 8, le unità con manifesto precedente a questo rilascio falliscono (l'etichetta di calibrazione entra comunque nell'hash): evento una tantum, da gestire drenando/ripetendo le preparazioni in corso. Il fattore non è nel `planJson`, solo nell'hash di configurazione. Le unità nuove usano il nuovo fattore. Prompt, schema, gold, corpus e retrieval invariati.

**Verifica offline** (`ExtractionOutputExpansionTests`, 4 test): con misuratore finto ricavato dal Run 1 (3,82 car/token) l'unità da ~1430 token posseduti è 1 batch a 8 e ≥2 batch a 16; ogni batch ha stima ≤ O, tutti gli intervalli compaiono esattamente una volta nell'ordine originale, nessun carattere perso (`VerifyExhaustive`); piano deterministico; validazione dell'opzione. Limite: le unità sono ricostruite dal corpus, non dalle porzioni reali del parser, e non provano che 16 basti al modello reale.
**Regressioni 002**: 368/368 test deterministici verdi con PostgreSQL di test locale (0 ignorati) sulle classi del percorso di estrazione/pianificazione/budget/recupero/dispatch (`DocumentEvidenceAgentTests`, `DocumentPortionPlannerTests`, `DocumentBatchPackingTests`, `DocumentBatchRecoveryTests`, `DocumentWorkflowTests`, `EvidenceExtractionDispatchTests`, `RetrievalGapCampaignTests` e altre 14). Le fixture di `DocumentEvidenceAgentTests` (dimensionate sul fattore 8: 5 test fallivano col nuovo default) fissano `ExtractionOutputExpansion = 8`: verificano meccaniche (tetto chiamate, retry), non la calibrazione. Suite completa non eseguita.

**Impatto atteso su batch e tempi (STIME, non misure)**: T040-single ≈ 5570 token posseduti in 4 unità da ~1430. A 8: 1 batch per unità (≈4 chiamate; comportamento del Run 1). A 16: ≥2 batch per unità (≥6 chiamate minimo teorico sul totale, ≈8 per le 4 unità), ogni chiamata ≲16384/16 ≈ 1000 token posseduti. Token generati totali invariati (stima 64–78k a 11,5–14 token/token posseduto), a ~58 token/s osservati nel Run 1 (16384 in 282 s) ≈ 19–22 min di sola generazione per documento in serie (parallel=1), contro un fallimento certo a 8; costo aggiuntivo: ~8 prefill ripetuti da ~3k token di istruzioni/schema (secondi ciascuno). Misure reali: nessuna oltre il Run 1.

T040 resta **aperto**; la prossima esecuzione DGX richiede nuova autorizzazione.

## Manifesto v3 e preflight per il Run 2 (2026-11-10; nessuna inferenza)

**Versioni del manifesto**: v1 `66243134…` (mai usata), **v2 `fbc0ecb3…` = quella del Run 1** (risultati sopra invariati; copia conservata in `T040-campaign-manifest-v2-run1.json`, verificata da un test che ne ricalcola l'hash), **v3 `6aad99f8b78e81f421d78a38619b16ec0496ee0b7dc926e24b4a36890f293181`** = congelata per il Run 2 (`T040-campaign-manifest.json`, con `freeze.history` e `freeze.supersedes` = v2).

**Differenza di configurazione Run 1 → Run 2 (unica)**: `ExtractionOutputExpansion` 8,0 → **16,0** (etichetta di calibrazione `extraction-output-expansion-2026-11-10`; valore candidato, non validato). Il manifesto la registra in `config.extraction`. I risultati del Run 1 si riferiscono alla configurazione con 8,0 e non sono confrontabili «a parità di configurazione» con il Run 2.

**Invarianti verificati da test** (confronto con la v2 archiviata): campagna, definizione, soglie, seed, documenti d'estensione e scenari (corpus, gold, domande, attese) identici byte per byte; ogni chiave preesistente di `config` identica; l'unica chiave aggiunta è `extraction`. Prompt e schema non fanno parte del manifesto e non sono stati toccati.

**Preflight**: `RetrievalGapCampaign.RejectOptions` controlla le `AiOptions` RISOLTE dall'applicazione in prova (`App.Services`, le stesse che legge `DocumentEvidenceAgent`) prima di profilo, upload e inferenza: fattore ≠ 16 o etichetta diversa ⇒ NO-GO (`[STRUTTURALE] … NO-GO: nessun upload, nessuna inferenza`). `ValidateEnvironment` rifiuta `BPROJECTS_TEST_EXTRACTION_OUTPUT_EXPANSION` diversa da 16 (confronto numerico: «16» e «16.0» ok). Verificato offline per 8, 12, 16,5, 24; non è stato possibile provare il percorso reale (richiede DGX).

**GO/NO-GO per il Run 2**: configurazione e harness: **GO** (offline). Il lancio richiede comunque una nuova autorizzazione esplicita e `BPROJECTS_TEST_DGX_ALLOW=1`. T040 resta aperto.

## Run 2 — ExtractionOutputExpansion 16

- Data: 2026-11-10, 22:41:50 → 23:24:13 (42 min 18 s), una sola esecuzione autorizzata, `BPROJECTS_TEST_DGX_ALLOW=1` solo per il processo. Nessun retry, nessuna modifica a configurazione, gold, soglie, attese, prompt, schema o planner.
- Commit di partenza `ccb61a3`; manifesto v3, hash congelato `6aad99f8b78e81f421d78a38619b16ec0496ee0b7dc926e24b4a36890f293181` (calcolato da `BuildManifestJson()`, non dal file; verificato dai test offline). Copia v2 del Run 1 preservata (`T040-campaign-manifest-v2-run1.json`).
- Comando: `dotnet test --solution bProjects.sln --no-build -- --filter-class "*RetrievalGapStageMafRealTests*"`; `--list-tests` con lo stesso filtro: 1 solo test (`Campaign.T040_…`).

### Preflight (senza inferenza) — GO
Build 0 errori/0 avvisi; 55 test offline verdi (`RetrievalGapCampaignTests`, `ExtractionOutputExpansionTests`; includono hash manifesto, confronto con v2, `RejectOptions`); router: `Qwen3.6-35B-Smart`, `bge-m3-Q5`, `bge-reranker-v2-m3` presenti (sonde `GET /models`, nessuna generazione); PostgreSQL di test (porta 15432) e parser reale (health 200) pronti; nessun altro processo T040. Le `AiOptions` risolte (fattore 16, etichetta `extraction-output-expansion-2026-11-10`) sono verificate dal preflight interno prima di profilo/upload: superato (il run è proseguito); profilo misurato W=32768, O=16384, M=1024, B=15360, thinking off (`RejectProfile` superato).

### Risultati
| Scenario | Esito | Dettaglio |
|---|---|---|
| Single | **Blocked** (39 PASS / 2 FAIL) | estrazione completata; indicizzazione fallita; run di analisi `Failed work_output_invalid` |
| Multi | **NotRun** | per regola del sequencer (blocco in Single) |

**Estrazione (obiettivo primario del Run 2): PASS.** `capitolato-aurora.txt` (117 blocchi, 5570 token = 0,36×B) → preparazione `Ready`, 8/8 porzioni `Completed`, copertura testo 21506/21505 car., 16/16 fatti gold presenti nel testo estratto, **0 chiamate `finish=length`**. Chiamate sul documento principale (8, tutte `finish=stop`, JSON parsabile; fonte `diag/20261110-214153-91da530f/summary.json`):

| # | prompt tok | completion tok | durata s | evidenze |
|---|---|---|---|---|
| 1 | 3669 | 13439 | 227,8 | 46 |
| 2 | 2488 | 3792 | 62,6 | 13 |
| 3 | 3477 | 12757 | 216,7 | 45 |
| 4 | 3081 | 8631 | 145,2 | 29 |
| 5 | 3612 | 13993 | 239,8 | 48 |
| 6 | 2853 | 7116 | 118,0 | 25 |
| 7 | 3614 | 12466 | 209,5 | 44 |
| 8 | 2915 | 7625 | 129,6 | 26 |
| tot | 25709 | 79819 | 1349 | 276 |

Massimo completamento 13993/16384 (85,4% di O): margine ridotto, il valore 16 basta in questa misura ma non con ampio margine (campione: 1 documento). Due documenti d'estensione: 1 chiamata ciascuna (356 e 306 token, 1 evidenza), `Ready`. Totale chiamate: Extraction 15 (1388 s, includono 5 estrazioni delle dichiarazioni del PM), Chat 5 (5 s), Analysis 5 (521 s); nessuna troncata. Il planner non persiste la stima per batch (`OwnedTokens` non persistito): la stima di output per batch è **NOT VERIFIED** (non ricostruita).

**Indicizzazione: FAIL.** `RetrievalIndexMaintenance.RebuildAsync`: preparazioni=3, Ready=0, **Failed=3** in 0,3 s. Causa **non dimostrata**: il codice di errore tipizzato (`RetrievalException.Code`) è nello stato `RetrievalIndexStates` del database di test, eliminato a fine run, e i parametri EF non sono nel log; la durata di 0,3 s per tre preparazioni esclude un'embedding di 117+ blocchi completata. Classe: applicativa o infrastrutturale (readiness/embedding del router `sleeping`) — da accertare, non attribuita al modello. Nota: la verifica `[T040/PREPARAZIONE]` non ha interrotto il run, che è proseguito con indice vuoto.

**Run di analisi: FAIL.** `Failed work_output_invalid`: «Output del batch `250fab41…` non valido (1 violazioni): items: ammessi solo nelle foglie della vista A (stadio leaf, vista B); le altre viste e gli altri stadi non propongono item». 5 chiamate Analysis, 521 s, nessuna `finish=length`. Non si sa se lo stadio fosse recupero, Gap o riconciliazione né se l'indice vuoto abbia determinato l'instradamento: le chiamate Analysis non sono nel recorder diagnostico del Run 2 (copre solo l'estrazione). Classe: output del modello non conforme al contratto (semantico) **oppure** conseguenza dell'indice fallito (applicativo): non distinguibile con le evidenze conservate.

### NOT VERIFIED
Routing verso retrieval, candidati per Gap, fasi di embedding/ricerca/fusione/reranking, Package Evidence e budget, esclusioni, latenze per Gap e condivise, esito dei cinque tipi di Gap, citazioni, conflitti preservati/persi, falsi `AnsweredInSnapshot`, Gap non valutati, token dello stadio, `coverage-3`, tutto lo scenario Multi.

### Confronto con il Run 1 (unica differenza: fattore 8 → 16)
Run 1: estrazione bloccata (`finish=length`, 16384 token, 282 s, 4 unità, 3 `parent_failed`). Run 2: 8 chiamate di estrazione, tutte `stop`, 1349 s, 79,8k token generati (stima pre-run 64–78k: leggermente sopra), preparazione `Ready`. La ricalibrazione a 16 ha rimosso il blocco di estrazione. Il Run 2 è però il primo a raggiungere indicizzazione e analisi, quindi i due blocchi successivi sono nuovi e non attribuibili al fattore.

### Artefatti
`T040-run2-summary.txt`, `T040-run2-state.json` (versionati, senza segreti); locali: log completo (49,7k righe EF SQL) in `%TEMP%\claude\…\scratchpad\t040-run2.log`, diagnostica `%TEMP%\bprojects-i5-real\diag\20261110-214153-91da530f\`, corpus `%TEMP%\bprojects-i5-real\T040-single-20261110214202\`.

### Verdetto del gate
**T040: FAIL/BLOCKED (non PASS).** Estrazione PASS; indicizzazione FAIL; analisi FAIL; Multi NotRun; nessun criterio di retrieval/Gap misurato. T040 resta `[ ]`. Nessuna seconda campagna avviata.


## Diagnosi offline del Run 2 e strumentazione diagnostica (2026-11-10/11; nessuna inferenza DGX, nessuna ripetizione, T040 resta aperto)

Fonti: codice (`RetrievalIndexBuilder`, `RetrievalIndexMaintenance`, `RetrievalModelReadiness`, `EmbeddingClient`, `ReconcileAliasComposer`, prompt `document-reconcile-v3`), archivio grezzo locale delle chiamate del Run 2 (`%TEMP%\bprojects-diag\20261110-214153-…-6e615ff2\`, 26 chiamate), `diag/20261110-214153-91da530f`, `M2-real.md`, `reranker-not-ready.txt`, `GET /models` del router (non generativo). Le evidenze originali del Run 2 sono invariate.

### 1. Indicizzazione 3/3 `Failed` in 0,3 s — cosa è dimostrato e quali sono i limiti delle evidenze
**Dimostrato**
- Il builder cattura solo `RetrievalException`, scrive `Failed` con `ex.Code` e prosegue; il messaggio tipizzato (che contiene lo stato del modello) non veniva salvato; `RebuildAsync` scartava i `RetrievalIndexBuildResult` e riportava solo i conteggi.
- Router (prima e dopo il run): `bge-m3-Q5` e `bge-reranker-v2-m3` `sleeping` (`--sleep-idle-seconds 900`), `Qwen3.6-35B-Smart` `loaded`.
- `RetrievalModelReadiness.ProbeAsync` mappa ogni stato diverso da `loaded` su `ModelUnloaded`; `EnsureEmbeddingReadyAsync` (usato da builder, `HybridSearch`, `CandidateReranker`) non attende e non invia richieste, quindi lancia `embedding_unavailable`.
- Riproduzione offline con server finto `sleeping` (PostgreSQL locale): tutte le preparazioni `Failed`, `ErrorCode = embedding_unavailable`, messaggio con `ModelUnloaded`, 0 richieste di embedding, esito immediato.
- Il gate non fermava la campagna: il check d'indice aveva il tag `[T040/PREPARAZIONE]` (non bloccante per `CampaignSequencer`) e `PrepareBeforeAnalyzeAsync` proseguiva; l'analisi LLM è partita a indice vuoto (circa 16 min di DGX) e Multi sarebbe partito se fosse fallito solo l'indice.

**`sleeping` non è un errore del router**: il router carica il modello alla prima richiesta ordinaria (M2: riordino scaricato caricato dalla prima richiesta minima; precedente T019). Il contratto (D-3) vieta caricamenti forzati e cambi di modello e classifica `ModelUnloaded` come transitorio, ma la readiness *sonda soltanto*: nel percorso del recupero nessuno invia mai la prima richiesta. Se sia un difetto di prodotto o una scelta voluta è una **decisione di contratto aperta**, non un bug dimostrato.

**Non recuperabile**: il codice effettivamente scritto in `RetrievalIndexStates` nel Run 2 (database eliminato), il registratore HTTP (solo memoria), il log del router. Più stati danno lo stesso codice (`ModelUnloaded`, `ModelLoading`, `Unreachable`, `Unverified`, per esempio con 401): `ModelUnloaded` è l'ipotesi più probabile e coerente, **non provata sul run**.

### 2. `work_output_invalid` (items) — dimostrato dall'archivio delle chiamate
Batch `250fab41…` = chiamata 026: stadio `leaf`, vista B, 139 voci; `finish=stop`, 10587 token, JSON valido, **139 `items`** (133 `requirements`, 4 `openQuestions`, 2 `constraints`), 0 candidati. Il prompt dice «`items`: SOLO nelle foglie della vista A» e il system prompt è identico byte per byte alle chiamate 023 (foglia A) e 025 (foglia B, conforme: 0 item, 3 candidati). Non dipende dall'indice: le foglie operano sul solo ledger e precedono recupero e stadio Gap. Classe: semantica (modello non conforme) con un fattore applicativo (schema unico, regola solo a posteriori, nessun retry guidato nel percorso di riconciliazione). **Riprodotta offline** con l'output archiviato (`ReconcileAliasComposerTests`, saltato esplicitamente se l'archivio manca): rifiuto con la violazione «items: ammessi solo nelle foglie della vista A (stadio leaf, vista B)…»; la stessa risposta come foglia A non incontra la regola di stadio. Proposte non applicate (cambiano contratto o schema): retry guidato, vincolo di schema per stadio e vista, scarto dei soli item indebiti.

### 3. Strumentazione introdotta (solo osservazione; nessun warm-up, retry, cambio di prompt, schema o policy)
- Produzione, additivo: `RetrievalRebuildSummary.Results` e `RetrievalIndexBuildResult.ErrorMessage` (messaggio tipizzato del recupero, senza testi né segreti).
- Gate bloccante: check `[COMPLETAMENTO]` (`RetrievalGapStageMafRealTests.IndexCheckLabel`) e ritorno immediato di `PrepareBeforeAnalyzeAsync`: indice incompleto, nessuna analisi LLM e nessun secondo corpus. `IsComplete` rifiuta indicizzazioni vuote, parziali o senza dettaglio.
- `RetrievalIndexGate` e `IndexDiagnostics`: snapshot di readiness (stato, endpoint logico host:porta/percorso, artefatto atteso e osservato, dimensione) prima dell'indicizzazione (sonda del prodotto) e ultimo stato noto dopo; esito per preparazione (stato, `ErrorCode`, messaggio, conteggi, id); chiamate HTTP del recupero (percorso, fase, codice HTTP, durata, tipo di eccezione; dei testi solo numero e lunghezza); eccezioni (solo il tipo per quelle non tipizzate); righe del database (stati d'indice, run, job per stato e falliti, batch derivati) lette con connessione e token propri prima dell'eliminazione, una query per volta.
- `AnalysisCallReport`: per ogni chiamata al modello, anche di Analysis, stadio, vista, hash di system prompt e schema, token, finish, durata, parsabilità, conteggi, regola «items fuori dalla foglia A» e nomi dei file grezzi; limitato alle chiamate dello scenario.
- Scrittura atomica e best-effort (non altera mai l'esito), un file per scenario e per esecuzione: `%TEMP%\bprojects-t040-campaign\diagnostic-report-<UTC>-<scenario>.json`. Gli output grezzi restano nell'archivio locale `%TEMP%\bprojects-diag`; nulla di tutto ciò è versionato.
- `ExtractionDiagnosticsRecorder`: il fattore di stima non è più una costante globale; è letto dalle `AiOptions` risolte dal runtime (`UseResolvedOptions`, chiamato a fine scenario) e dichiara la fonte (`plannerOutputExpansionSource`); senza lettura dichiara che è il default di produzione. Il test usa aspettative scritte a mano (30 token × 8/16/12,5/1 = 240/480/375/30), non la formula.
- Review `ecc:csharp-reviewer`: nessun problema critico né fuga di segreti. Corretti: Persist non atomico e non protetto, DB letto con token annullato, query in un unico `try`, file sovrascritto fra run, segmenti non protetti, chiamate di Single incluse nel report di Multi, campi liberi (vista in whitelist, messaggi solo per eccezioni tipizzate), test fragili (etichetta reale del check, tempo, root dell'archivio). Non applicati: pulizia dei file temporanei dei test e copertura del caso «sonda funzionale con modello caricato».

### 4. Test
`RetrievalIndexGateTests` (readiness sleeping, modello assente, 401 e irraggiungibile con codici HTTP e nessun segreto nel file; fallimenti di embedding 400, 500 e risposta malformata con `ErrorCode` e messaggio per preparazione; eccezione durante la ricostruzione; gate; rapporto delle chiamate con violazione degli items e output troncato), replay dell'output 026, `ExtractionDiagnosticsCaptureTests`. Regressione mirata: 262 superati, 1 ignorato (lo smoke test reale, senza opt-in), 0 falliti. Suite completa non eseguita. Il test rimasto rosso dopo `6acb8db` (`atteso=240`, fattore 8 fisso) è corretto.

### 5. Verifica reale minima e isolata dell'embedding (proposta, NON eseguita: richiede autorizzazione separata)
`RetrievalEmbeddingSmokeRealTests`: nessun database, LLM, documento o campagna. Sequenza: (1) snapshot di readiness del prodotto (`GET /v1/models`, `/props` se serve e, solo se già caricato, la richiesta funzionale minima del prodotto); (2) solo se `ModelUnloaded`, UNA richiesta di embedding ordinaria di un testo («probe»); (3) nuovo snapshot. PASS solo se alla fine l'embedding è `Ready`. Evidenze: stato iniziale e finale con endpoint logico e artefatto, codici HTTP e durate (il tempo del primo `POST /v1/embeddings` misura il caricamento on-demand), eventuale `ErrorCode`, in `%TEMP%\bprojects-t040-campaign\embedding-smoke-report.json`. Costo atteso: al più 4 richieste HTTP leggere e un caricamento di bge-m3 (alcuni secondi; nessuna generazione, nessun LLM). Senza `BPROJECTS_TEST_DGX_ALLOW=1` il test risulta Skipped.

### 6. GO/NO-GO per un eventuale Run 3
- Strumentazione: **GO** (il prossimo fallimento sarà ricostruibile senza database).
- Indicizzazione: **NO-GO** finché non è deciso il contratto sul risveglio dei modelli `sleeping` e non è eseguito lo smoke test isolato; senza, il Run 3 si fermerebbe di nuovo subito (a basso costo, grazie al gate).
- Riconciliazione: **NO-GO** finché non si decide su retry guidato, vincolo di schema o scarto per la violazione `items` (1 foglia B su 2 nel Run 2).
- T040 resta aperto; M4 posticipata.
