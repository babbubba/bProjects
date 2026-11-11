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

## Smoke test embedding e attivazione on-demand (2026-11-10/11)

**Smoke test reale** (`RetrievalEmbeddingSmokeRealTests`, una esecuzione autorizzata, 2,9 s, nessun retry): `bge-m3-Q5` iniziale `ModelUnloaded` (`GET /v1/models` 200, 99 ms) → UNA `POST /v1/embeddings` ordinaria (1 input di 5 caratteri, 200, 1108 ms: il caricamento on-demand) → `GET /v1/models` 200 → sonda funzionale del prodotto 200 (81 ms) → finale `Ready` (artefatto `bge-m3-Q5_K_M.gguf`, dimensione 1024). Nessun errore tipizzato. Conclusione: la causa più probabile del fallimento d'indice del Run 2 è un embedding `sleeping` senza alcuna richiesta di attivazione (non provata direttamente: l'`ErrorCode` del Run 2 non è recuperabile). Il modello può tornare `sleeping` dopo l'inattività: il problema si ripresenta a ogni avvio a freddo.

**Decisione e correzione (2026-11-11, autorizzata dall'utente)**: eccezione esplicita di `contracts/model-readiness.md` §3.1 — solo nel percorso che usa il modello, un modello configurato e identificato `ModelUnloaded` riceve UNA richiesta funzionale minima (single-flight per modello, nessun retry entro la cache, nessuna attivazione per `ModelMissing`/`ArtifactMismatch`/`DimensionMismatch`/`Unverified`/`ModelLoading`/`Unreachable`), poi cache invalidata e riverifica di identità, artefatto e dimensione. Le interrogazioni passive non svegliano mai i modelli. Implementata nel prodotto (`RetrievalModelReadiness`), non nell'harness. Provata DET con router finto (`RetrievalModelActivationTests`: cold start, già pronto, passività, errore di attivazione tipizzato e non ripetuto, modello errato dopo l'attivazione, stati esclusi, concorrenza, cancellazione, ritorno a `sleeping`, riordino indipendente, eccezione imprevista); **non** provata di nuovo su DGX (richiede autorizzazione separata). T040 resta aperto, M4 posticipata; la violazione `items` della riconciliazione non è toccata.

### Verifica reale dell'attivazione on-demand (2026-11-11, `RetrievalActivationSmokeRealTests`, una esecuzione, 4,3 s, nessun retry)

Codice di prodotto del commit `6549e1b` (`Get*SnapshotAsync` passivo, poi `Ensure*ReadyAsync`), client reali di embedding e riordino; rapporto locale `activation-smoke-report-20261110T233819219Z.json` (non versionato).

| Modello | Stato iniziale | Lettura passiva | Percorso per l'uso | Finale | `Last*Activation` |
|---|---|---|---|---|---|
| `bge-m3-Q5` | `ModelUnloaded` (cold start verificato) | solo `GET /v1/models` 200 (95 ms), 0 richieste al modello | `POST /v1/embeddings` 200 (973 ms, attivazione) → `GET /v1/models` 200 → `POST /v1/embeddings` 200 (81 ms, riverifica); 1192 ms totali | `Ready`, `bge-m3-Q5_K_M.gguf`, dimensione 1024 | `Attivato`, 1188 ms, nessun errore |
| `bge-reranker-v2-m3` | `ModelUnloaded` (cold start verificato) | solo `GET /v1/models` 200 (32 ms), 0 richieste al modello | `POST /v1/rerank` 200 (1043 ms, attivazione) → `GET /v1/models` 200 → `POST /v1/rerank` 200 (94 ms, riverifica); 1200 ms totali | `Ready`, `bge-reranker-v2-m3-Q5_K_M.gguf` | `Attivato`, 1199 ms, nessun errore |

**Provato su DGX**: la lettura passiva non attiva; il percorso per l'uso attiva un modello scaricato con UNA richiesta minima, poi riverifica identità/artefatto/dimensione e riconosce `Ready`; `Last*Activation` popolato; embedding e riordino con slot indipendenti. **Solo offline (router finto)**: errore di attivazione tipizzato e non ripetuto, modello errato dopo l'attivazione, stati esclusi, single-flight concorrente, cancellazione, ritorno a `sleeping`, eccezione imprevista; timeout di caricamento oltre `TimeoutSeconds` non provato. Nessun unload né reset del router. T040 resta aperto, M4 posticipata; il Run 3 resta NO-GO per la violazione `items` della riconciliazione (non decisa).

## Correzione della riconciliazione leaf (2026-11-11, OFFLINE; nessuna inferenza DGX, T040 resta aperto)

**Causa (chiamata 026).** Il prompt `document-reconcile-v3` dichiara nello stesso punto «`items`: SOLO nelle foglie della vista A» e la regola di copertura delle foglie A («ogni evidenza del batch deve essere citata da almeno un elemento»); il messaggio del batch dice solo `Tipo di batch: foglia / Vista: B`. Con system prompt identico alla 025 (conforme), il modello ha applicato alla foglia B la copertura della foglia A (139 item per 139 voci). Lo schema JSON era UNICO per tutti i batch (`ReconcileAliasContracts.JsonSchema()`, costruito una volta in `DocumentReconcilePlan` e inviato grezzo come `response_format` da `InternalModelClient`) e permetteva `items` ovunque: la regola valeva solo a posteriori, nel compositore, che ha correttamente rifiutato.

**Alternative valutate.** A — schema per stadio e vista (scelta); B — un retry guidato (meccanismo già presente per bozza/analisi, `IGuidedRetryPlan`, ma cambia il numero di chiamate, è vietato nella campagna e corregge dopo invece di prevenire); C — scarto dei soli item indebiti (accetterebbe una risposta semanticamente sbagliata e potrebbe perdere candidati di conflitto: escluso). A non è applicabile alle definizioni esistenti senza alterarne l'identità: lo schema entra nel `PayloadHash` (Pack ⇒ `batchId`, rimisura ⇒ `inputHash`); applicarlo in v7 avrebbe cambiato richieste e hash di run con la stessa identità, applicarlo solo all'invio avrebbe reso `inputHash` diverso dal payload inviato.

**Decisione del committente (2026-11-11).** A come nuova definizione `project-analysis-documents-v8` = v7 + chiave `reconcileSchemaProfile = reconcile-alias-schema-by-batch-1`; varianti su `items`, `patches` e `focus`; v8 congelata dalle nuove richieste con recupero (v6 senza recupero invariata); manifesto T040 v3 NON modificato (il v4 è da preparare a parte). Contratto: `contracts/candidate-source.md` §4.1; 002 `contracts/ai-workflow.md` §11.

**Compatibilità del provider.** llama.cpp (build b10703, router) traduce lo schema in grammatica (provato nella 001 per pattern/enum e in I3-real); il convertitore attuale applica `maxItems` agli array con schema degli elementi (`max_items = 0` ⇒ solo `[]`) e `{"type":"null"}`. Il supporto sulla build esatta b10703 **non è verificato** sul sorgente né su un run reale: se ignorato, la v8 si comporta come la v7 e il validatore rifiuta comunque. Con `StructuredOutputMode` diverso da `JsonSchema` nessuno schema è inviato (la v8 equivale alla v7).

**Implementazione.** `ReconcileAliasContracts` (varianti `Full`/`NoItems`/`Candidate`, `SchemaVariantOf`, `SchemaProfileHashes`; schema fissato invariato); `DocumentReconcilePlan` (agente per variante, variante registrata in `PrepareAsync`/`ComposeAsync`, `AgentFor` fail-closed `checkpoint_incompatible` senza variante; Pack con lo schema completo; `configHash` e piano v8 con il profilo); `DocumentAnalysisWorkflow.ContractOf` (8 solo con tutte le chiavi v7 e il profilo; la chiave in v4–v7 è mista); instradamento, `SourceSnapshotStore.AnalysisPromptVersionsV8`, `AnalysisEndpoints` (v8 con recupero). Compositore, validatore, prompt, DTO, WorkOutput e replay invariati; nessun retry.

**Test offline (DET, PostgreSQL di test).** `ReconcileSchemaProfileTests` (tabella delle varianti; Full = schema fissato; forma di NoItems/Candidate; coerenza schema⇔compositore per ogni stadio e vista; JSON malformato e alias sconosciuti; leaf B con candidati senza perdite; **regressione sull'output archiviato della 026**: viola la variante NoItems solo su `items` e resta rifiutato dal validatore), `ReconcileDefinitionV8Tests` (matrice `ContractOf`, freeze v7 invariato, scarto delle varianti), `RetrievalGapFlowTests` (schema REALMENTE presente nel corpo HTTP per ogni batch; run congelato v7 con schema unico e piano invariato; risposta «tipo 026» sotto v8 rifiutata senza commit né chiamate successive; crash su una foglia B e ripresa con lo stesso `inputHash`, nessun batch Committed reinviato). Le due asserzioni di `RetrievalGapFlowTests` che attendevano la v7 dal POST sono passate a v8 per la decisione approvata.

**Review.** `ecc:csharp-reviewer`: nessun CRITICAL/HIGH; corretti il commento sul Pack (la variante Candidate è qualche carattere più grande del completo: coperta dalla riserva di 256 token, rimisura fail-closed, test dello scarto) e la spaziatura in `AnalysisEndpoints`. Non applicati (non raggiungibili o marginali): eccezione tipizzata per uno stadio sconosciuto, cache di `SchemaProfileHashes`, test golden del `configHash` v4–v7 (l'invarianza è garantita dal ramo di codice separato e dal test del run v7). `ecc:architect`: GO per preparare il Run 3, nessun difetto bloccante.

**Rischi residui.** Il modello potrebbe spostare contenuti da `items` a candidati spuri nelle foglie B (validati; possibili più espansioni/confronti fino ai tetti, con errore tipizzato): confrontare i candidati per foglia B con la 025 del Run 2 (3). Run v6 senza recupero e run già congelati v7 restano esposti.

**Da versionare per il Run 3 (non fatto, da autorizzare).** (1) Manifesto T040 **v4** nuovo (il v3 `6aad99f8…` resta intatto): `definition = project-analysis-documents-v8`, `freeze.version = 4`, `supersedes` v3, profilo e hash delle varianti, prompt `document-reconcile-v4` con il suo SHA-256 (vedi §«Completamento della v8»); corpus, gold, soglie, domande e politica di valutazione invariati. (2) Harness nello stesso commit: `RetrievalGapCampaign` (definizione e versione del freeze), `RetrievalGapStageMafRealTests.ExpectedDefinition` → v8 (oggi v7: il controllo strutturale fallirebbe solo dopo l'inferenza), `RetrievalGapCampaignTests`. (3) Preflight: `StructuredOutputMode = JsonSchema` sulle `AiOptions` risolte, oltre ai gate esistenti.

**GO/NO-GO.** Riconciliazione: **GO per preparare** il Run 3 (correzione implementata e provata offline); esecuzione del Run 3 **NO-GO** finché i punti (1)–(3) non sono fatti e autorizzati. T040 resta aperto; T041–T043 (M4) posticipati.

### Completamento della v8: prompt `document-reconcile-v4` (2026-11-11, OFFLINE)

**Discrepanza segnalata dal committente.** La decisione approvata era «A v8 + chiarimento prompt»; la prima implementazione (commit `54cee5a`) usava ancora `document-reconcile-v3`. Prima del completamento è stato verificato che nessuna elaborazione v8 fosse avviata (definizione introdotta lo stesso giorno; stack locale fermo da 7 giorni; PostgreSQL di test senza run; nessuna traccia v8 fuori da codice e documenti): il cambio non rende incompatibile alcun run esistente.

**Prompt.** Nuova risorsa `AI/Prompts/document-reconcile-v4.md` (SHA-256 `d3e46528736e856eedfa43e1fa41785b0a87ab7ddba91881f57bb9c09c0da79d`); il v3 resta byte per byte (`0503d17360e21e8f72e357d1fcf0f0e32fbead294d970193af4eb61ff847c9e5`). Tre soli frammenti cambiano: `items` vuoto «comprese le foglie della vista B»; obbligo di copertura «nelle foglie della vista A, e solo in quelle» (negli altri batch non esiste); nelle foglie della vista B il compito è soltanto individuare i candidati, senza citare ogni evidenza né inventare collegamenti (nessuno ⇒ `candidates: []` con `noFindings` motivato). Conflitti, candidati, focus, patch, etichette e formato letterali.

**Dispatch e identità.** v8 ⇒ prompt v4 (`DocumentAnalysisWorkflow.ReconcilePromptOf`, `ReconcileAliasMode.PromptVersion`, `ReconcileAliasContracts.InstructionsFor`: versione ignota ⇒ eccezione, nessun ripiego); v4–v7 ⇒ v3, invariati. Freeze v8: `reconcile = document-reconcile-v4` al posto del v3 (le altre 23 chiavi v7 invariate) più `reconcileSchemaProfile`; `ContractOf` = 8 solo con il v4; v8 con il v3, o v4 in v4–v7, ⇒ `workflow_incompatible`. Il `configHash` v8 contiene nome e SHA delle istruzioni v4; misura del Pack, rimisura (`inputHash`), invio (agenti per variante) e `DerivedBatches.PromptVersion` (confrontata da `SameBatch` alla ripresa) usano lo stesso prompt. Replay degli output `Committed`, DTO, compositore e validatore invariati; nessun retry.

**Test offline.** `ReconcileDefinitionV8Tests` (SHA di v3 e v4, file = risorsa incorporata; il v4 senza i tre frammenti è il v3; regola dei conflitti identica; dispatch per definizione v4–v8; versione ignota rifiutata; combinazioni miste di prompt), `RetrievalGapFlowTests` (prompt di sistema REALMENTE inviato a ogni batch della gerarchia v8 = v4 e stadio Gap invariato; `PromptVersion` persistita = v4; run v7 storico = v3 inviato e persistito; ripresa dopo crash con lo stesso prompt v4; v8 congelata con il v3 ⇒ `workflow_incompatible` a zero chiamate e zero batch). La regressione sull'output archiviato della 026 (`ReconcileSchemaProfileTests`) è rieseguita ed eseguita (non ignorata). Per il manifesto T040 v4 (non preparato) va aggiunta l'identità del prompt v4 al punto (1) sopra.

**Review.** `ecc:csharp-reviewer`: nessun CRITICAL/HIGH/MEDIUM; i sei requisiti (v3 intatto, solo il chiarimento delle foglie, v4–v7 sul v3, chiavi e `ContractOf`, prompt unico per misura/rimisura/invio/ripresa, nessun ripiego né retry) risultano rispettati. Applicato il LOW sul valore predefinito: `ReconcileAliasMode.PromptVersion` è ora obbligatorio (nessun ripiego implicito sul v3 per un chiamante futuro). Lasciato il default v3 di `StagedKeysMatch`/`HierarchyMatches` (corretto per v4–v7; solo il ramo v8 passa il v4). `DocumentGapStageWorkflowTests` (v6, prompt v3) verificato nella suite completa.

## Preparazione offline del Run 3: manifesto T040 v4, harness v8 e preflight (2026-11-11; nessuna inferenza DGX, nessun retry, T040 resta aperto)

Baseline di codice `65cdcc8`. Nessuna modifica a v8, prompt, parser, retrieval, readiness, corpus, gold, domande, attese, soglie, politica, modelli o parametri di estrazione; solo harness di test, manifesto e documenti. Nessun criterio di valutazione cambiato.

**Versioni del manifesto.** v1 `66243134…` (mai usata), v2 `fbc0ecb3…` (Run 1), **v3 `6aad99f8b78e81f421d78a38619b16ec0496ee0b7dc926e24b4a36890f293181`** (Run 2, definizione v7), **v4 `91a1d09676e038b5bbe4bd122638704a4b8831105d8579a5c110aa339338ebaf`** (Run 3, definizione v8; `T040-campaign-manifest.json`).

**Copia immutabile del v3 del Run 2.** `T040-campaign-manifest-v3-run2.json`, copiata dal file versionato (commit `ccb61a3`, quello che ha prodotto l'hash del Run 2) PRIMA di rigenerare il v4: il suo oggetto Git è identico a quello di `ccb61a3` (`fe3dd711…`) e un test ricalcola dal suo contenuto lo SHA-256 `6aad99f8…` (come per la copia del Run 1). L'impronta del manifesto è lo SHA-256 del solo oggetto `manifest` in JSON compatto (campo `frozenSha256`), non dell'intero file.

**Delta esatto v3 → v4** (verificato da un test che confronta foglia per foglia l'archivio v3 con il manifesto generato; nient'altro cambia):

| Percorso | Variazione |
|---|---|
| `definition` | `project-analysis-documents-v7` → `project-analysis-documents-v8` |
| `config.reconcile` | NUOVO: prompt `document-reconcile-v4` con SHA-256 `d3e46528736e856eedfa43e1fa41785b0a87ab7ddba91881f57bb9c09c0da79d` (precedente v3 `0503d173…c9e5`, invariato); profilo `reconcile-alias-schema-by-batch-1` (chiave di freeze `reconcileSchemaProfile`); hash canonici RFC 8785 delle varianti dello schema `Full` `648915d4…5c54` (= schema fissato), `NoItems` `7b0a15de…52b2`, `Candidate` `facb96cb…4f38`; funzionalità del provider richieste (`maxItems:0`, `type:null`) dichiarate **non verificate** su b10703; chiavi di freeze `reconcile` e `reconcileSchemaProfile` |
| `config.structuredOutput` | NUOVO: `JsonSchema` richiesto (globale, vale per la fase Analysis) |
| `freeze.version` | 3 → 4 |
| `freeze.supersedes` | `fbc0ecb3…` (v2) → `6aad99f8…` (v3) |
| `freeze.history[2]` | NUOVO: v3 `6aad99f8…`, usata dal Run 2 (non superato), copia in `T040-campaign-manifest-v3-run2.json` |
| `freeze.codeBaseline` | NUOVO: `65cdcc8` |
| `freeze.reason` | testo del v4 (Run 2, v8) |
| `freeze.configDifferenceFromRun1` → `configDifferenceFromRun2` | sostituita dalla differenza rispetto al Run 2 |

Invariati byte per byte (confronto di stringa JSON): `campaign`, `thresholds`, `seed`, `extension`, `scenarios` (corpus, hash del testo e dei gold, domande, attese) e ogni chiave preesistente di `config` (finestra, politica e hash, embedding, riordino, fattore di estrazione 16, LLM, latenza). Rispetto al manifesto del Run 1 le uniche differenze sono `definition` e le tre chiavi di `config` `extraction`, `reconcile`, `structuredOutput`.

**Harness.** `RetrievalGapCampaign` (identità congelata come letterali: definizione, prompt con SHA-256, profilo, hash delle varianti, `CodeBaseline`; `FrozenManifestSha256` v4, `Run2ManifestSha256`; `FreezeReason` v4), `RetrievalGapStageMafRealTests.ExpectedDefinition` → `RetrievalGapCampaign.DefinitionVersion` (v8; prima v7: il controllo strutturale sarebbe fallito solo a run concluso).

**Preflight fail-closed (tutto PRIMA della prima richiesta al modello).** Ordine in `PreflightAsync`: (1) skip senza opt-in/ambiente completo (`RequireConfigured`, nuovo, senza rete; prima era fuso con `Require`); (2) controlli puri: `ValidateEnvironment` (variabili d'ambiente e hash del manifesto contro `FrozenManifestSha256`), `RejectIdentity` (confronto fra i letterali congelati e ciò che il PRODOTTO realmente produce: definizione congelata da `SourceSnapshotStore.AnalysisDefinitionV8`, definizione attesa dalla prova, chiavi di freeze `reconcile` e `reconcileSchemaProfile` di `AnalysisPromptVersionsV8`, prompt realmente instradato da `ReconcilePromptOf`, SHA-256 della risorsa incorporata, hash canonici delle tre varianti, contratto 8 riconosciuto da `ContractOf`) e `RejectOptions` sulle `AiOptions` RISOLTE dall'applicazione in prova (`StructuredOutputMode == JsonSchema` — anche un valore non definito è rifiutato —, fattore e calibrazione di estrazione, fase Analysis: output 16384, ragionamento spento, modello `Qwen3.6-35B-Smart`); tutti i problemi sono riportati insieme; (3) solo dopo, `Require` (il warm-up è la PRIMA inferenza). Nello scenario, `RejectOptions` precede ora anche `BuildSizedAsync` (profilo e tokenizer), non solo upload e inferenza. Con `ValidatedTextJson` nessuno schema verrebbe inviato e la v8 equivarrebbe silenziosamente alla v7: ora è NO-GO.

**Verifica reale minima del supporto di `maxItems:0` e `type:null` (PREDISPOSTA qui; ESEGUITA il 2026-11-11, esito nella sezione «Esito della verifica reale dello schema» più sotto).** `ReconcileSchemaSupportRealTests` (categoria `MafReal`, `Campaign=SchemaSupport`, collezione seriale dei test reali; senza `BPROJECTS_TEST_DGX_ALLOW=1` è Skipped). Esattamente **tre richieste**, una per variante, nell'ordine `Full` (controllo), `NoItems`, `Candidate`, mai ripetute, con le opzioni di richiesta del prodotto (`InternalModelClient.CreateChatOptions`: `response_format` json_schema grezzo, ragionamento spento, temperatura 0, fase Analysis, 1024 token), prompt sintetico fisso senza contenuto reale; nessun database, documento o campagna. Verdetto per funzionalità: **Honored** solo se il controllo ha indotto il contenuto che il vincolo deve impedire (`items` non vuoto, `focus` oggetto) e la variante lo ha impedito (`items=[]`, `focus=null`); **IgnoredByProvider** se il contenuto compare; **RejectedByProvider** se il provider risponde con errore; **Inconclusive** se il controllo non induce il contenuto, la risposta è troncata o non valutabile; `maxItems:0` su `patches` è **NotExercised** (il controllo non induce patch: supporto inferito dallo stesso meccanismo del convertitore, non provato). Passa solo se almeno una funzionalità è Honored e tutte quelle esercitate lo sono. Rapporto in un file nuovo `%TEMP%\bprojects-t040-campaign\schema-support-report-<UTC>.json` (schema inviato, stato HTTP, risposta, verdetti; nessuna chiave; build llama.cpp non rilevata, riportata come dichiarata). Costo: tre richieste, di cui la prima può includere il caricamento del modello. Provata offline contro provider finti (rispetta, ignora, rifiuta, controllo debole, troncato, set incompleto); lo schema nel corpo HTTP di ciascuna richiesta ha l'hash congelato nel manifesto. **Non dimostra il comportamento di b10703.**

**Test.** `RetrievalGapCampaignTests` (aggiornati e nuovi: archivio v3 e suo hash, delta esatto v3→v4, sezioni invariate, history e freeze storici, identità registrata = identità del prodotto, ogni deriva di identità ⇒ NO-GO, `AiOptions` incompatibili, ordine del preflight, `ExpectedDefinition` v8 in entrambi gli scenari) e `ReconcileSchemaSupportProbeTests` (nuovo).

**Esito dei test (offline, senza `BPROJECTS_TEST_DGX_ALLOW`).** Build: 0 avvisi, 0 errori. Classi mirate `RetrievalGapCampaignTests` + `ReconcileSchemaSupportProbeTests`: 70 su 70. Suite completa con PostgreSQL di test: **4257 test, 0 falliti, 4163 superati, 94 ignorati** (gli ignorati sono i livelli reali/DGX e quelli senza variabili d'ambiente: mai PASS; la verifica reale dello schema è fra questi). Nessun DGX contattato.

**Review.** `ecc:csharp-reviewer` (indipendente, sola lettura): nessun CRITICAL/HIGH; `backend/src` invariato; archivio v3 e v4 con hash ricalcolati concordi. Applicati: (M1) le `RetrievalOptions` RISOLTE dall'applicazione sono ora controllate dal preflight (recupero abilitato, politica, modelli e artefatti; l'identità del prodotto è costruita da esse); (M2) il rapporto della verifica reale riporta `provenForExercisedFeatures` e `notProven` (mai un «proven» generico); (M3) la verifica reale giudica solo se lo schema nel corpo della richiesta ha l'hash congelato (altrimenti non conclusivo); (L4) solo 400/422 sono «rifiutato», altri errori (401, 429, 5xx, timeout) sono non conclusivi; (L5) un timeout del client è un errore della variante, non interrompe le successive; (L8) le asserzioni d'ordine si limitano al corpo del metodo. Non applicati: categoria `MafReal` sulla verifica reale (coerente con le altre prove reali: si seleziona con `Campaign=SchemaSupport` o `--filter-class`); costante storica `FreezeReasonV3` inutilizzata (documentazione del freeze); ordine delle chiavi di `Dictionary` nei test (insertion order senza rimozioni); `FinalizeDiagnosticsAsync` dopo un NO-GO di Multi contatta il DGX per misure non generative (comportamento preesistente: «nessuna inferenza» resta vera, «nessun contatto» no); il controllo su `StructuredOutputMode` protegge da regressioni dell'harness più che da configurazioni esterne, perché le opzioni derivano da `RequireDgxOptions`.

**Da fare prima di un GO reale del Run 3 (nessuno eseguito).**
1. Autorizzazione esplicita del committente e `BPROJECTS_TEST_DGX_ALLOW=1`; ambiente dichiarato: `BPROJECTS_TEST_DGX_MODEL=Qwen3.6-35B-Smart`, endpoint e chiave, parser reale e PostgreSQL di test raggiungibili, nessuna variabile `Ai__*`/`BPROJECTS_TEST_*` che sovrascriva finestra, output, margine, ragionamento, fattore di estrazione o `StructuredOutputMode`.
2. [SODDISFATTO il 2026-11-11, vedi la sezione seguente] Decisione sulla verifica reale dello schema (tre richieste): se `maxItems:0`/`type:null` fossero ignorati o rifiutati da b10703 la v8 si comporterebbe come la v7 (o fallirebbe), e il Run 3 sarebbe un esperimento sul solo prompt v4; la verifica costa tre richieste e converte un'assunzione in un fatto prima di un run da ore.
3. Registrare il commit esatto dal quale si lancia il run (il manifesto registra la baseline `65cdcc8`) e l'albero pulito; i risultati del Run 3 si riferiscono al manifesto v4 `91a1d096…`, non confrontabili «a parità di configurazione» con il Run 2 (definizione, prompt e schemi diversi).
4. Rischi residui dichiarati prima: la v8 non è mai stata eseguita su un modello reale; l'estrazione completa del Run 2 e l'indicizzazione on-demand sono già state viste ma non nel Run 3; il confronto dei candidati per foglia B con la chiamata 025 del Run 2 resta da fare a run concluso.

**GO/NO-GO.** Preparazione offline: **completa** (manifesto v4, harness, preflight, verifica reale predisposta). Esecuzione del Run 3: **NO-GO** finché non sono soddisfatti i punti 1–3. T040 resta aperto; T041–T043 (M4) posticipati.

## Esito della verifica reale dello schema `maxItems:0` / `type:null` (2026-11-11; una esecuzione, tre richieste generative, nessun retry; T040 resta aperto)

**Autorizzazione e modalità.** Autorizzata dal committente per UNA sola esecuzione di `ReconcileSchemaSupportRealTests` (`--filter-trait "Campaign=SchemaSupport"`, `BPROJECTS_TEST_DGX_ALLOW=1` impostato solo nel processo del test), senza modificare codice o parametri per ottenere un PASS. Ambiente: `BPROJECTS_TEST_DGX_MODEL=Qwen3.6-35B-Smart`, endpoint e chiave dalle variabili d'ambiente (la chiave non compare nel rapporto: verificato), nessuna variabile `Ai__*` e nessuna altra `BPROJECTS_TEST_*` di calibrazione impostata. Albero di lavoro: commit `ba44bc4`, nessuna modifica ai file tracciati. Esito del test: superato, 1 su 1, 14 s. Non è stato eseguito il Run 3 né altre richieste generative; il manifesto v4 non è stato toccato.

**Build del provider RILEVATA (non più solo dichiarata).** Due richieste NON generative (GET): `/props` del router riporta `build_info = b10703-0b5be7e4a`, `role = router`, `max_instances = 4`; il campo `system_fingerprint` di tutte e tre le risposte di inferenza riporta lo stesso `b10703-0b5be7e4a`. `/v1/models` mostra `Qwen3.6-35B-Smart` in stato `sleeping` (avviato dallo stesso binario `llama-server` del router, `--jinja`, `--reasoning on`, `--ctx-size 131072`, `--parallel 1`). `/health` è andato in timeout e non è stato ripetuto. (Il testo «NON rilevata» nel campo `provider` del rapporto è una stringa fissa del test, superata da queste sonde.)

**Rapporto diagnostico.** `%TEMP%\bprojects-t040-campaign\schema-support-report-20261111T142829734Z.json` (file locale, non versionato; 30418 byte), **SHA-256 `E1670DDEC3608566B317D0636D9C2EDCD954F4B2703A30A07ED3695FBDE5DC08`**. Contiene schema inviato, stato HTTP, risposta e verdetto per funzionalità, e `provenForExercisedFeatures = true`, `notProven = ["maxItems:0 su patches (NoItems)"]`.

**Le tre richieste** (tutte `POST /v1/chat/completions`, HTTP 200, `finish_reason = stop`, temperatura 0, ragionamento spento, `response_format` json_schema, prompt sintetico fisso; l'hash canonico dello schema nel corpo coincide con quello congelato nel manifesto v4):

| # | Variante | Hash dello schema | Risultato |
|---|---|---|---|
| 1 | `Full` (controllo) | `648915d4…5c54` | `items` con 1 elemento, `candidates` con 1, `focus` oggetto, `patches: []` |
| 2 | `NoItems` | `7b0a15de…52b2` | `items` vuoto, `candidates` con 1, `focus: null`, `patches: []` |
| 3 | `Candidate` | `facb96cb…4f38` | `items` vuoto, `candidates` con 1, `focus` oggetto, `patches: []` |

**Esito per funzionalità.**

| Funzionalità | Esito | Evidenza |
|---|---|---|
| `maxItems:0` su `items` (NoItems) | **Honored** | 0 elementi (controllo: 1) |
| `type:null` su `focus` (NoItems) | **Honored** | `null` (controllo: oggetto) |
| `maxItems:0` su `items` (Candidate) | **Honored** | 0 elementi (controllo: 1) |
| `maxItems:0` su `patches` (NoItems) | **NotExercised** | il controllo non induce patch: supporto inferito dallo stesso meccanismo del convertitore di `maxItems` su `items`, **NON provato** |

Nessun esito `IgnoredByProvider`, `RejectedByProvider` o `Inconclusive`: nessuno stato 400/422, nessuna troncatura, controllo valido in tutte le richieste.

**Decisione del committente (2026-11-11).** `patches.maxItems=0` è accettato come inferenza tecnica, senza ulteriori prove reali dedicate; la verifica dello schema è conclusa positivamente per le sole funzionalità esercitate. In questo documento e nel rapporto `patches` resta esplicitamente `NotExercised`: non è dichiarato provato.

**Osservazione.** Nelle varianti 2 e 3 la grammatica di llama.cpp emette per l'array vuoto `"items": [\n    \t],` (un carattere di tabulazione fra le parentesi): è JSON valido e il parser del test lo legge come array vuoto. Il comportamento del parser del prodotto su questa forma non è stato misurato qui (nessuna modifica al parser senza evidenza di un difetto).

**Limiti.** Campione di tre richieste con temperatura 0 e prompt sintetico: dimostra che b10703 applica `maxItems:0` su `items` e `type:null` su `focus` con questo schema, non che la v8 produca candidati corretti sui documenti reali. Il Run 3 resta da eseguire e non è stato eseguito. Il punto 2 dell'elenco «Da fare prima di un GO reale» è soddisfatto; restano i punti 1, 3 e 4.

## Esito del Run 3 (2026-11-11; manifesto T040 v4, definizione v8; una esecuzione, nessun retry; T040 resta aperto)

**Esecuzione.**
- Commit `b445871`, avvio alle 16:03:09 UTC, modello `Qwen3.6-35B-Smart` su llama.cpp b10703.
- `T040-single` **FAIL** (40 PASS / 1 FAIL); `T040-multi` non eseguito (arresto su blocco, nessun rilancio).
- Fallimento: run di analisi `Failed work_output_invalid` — «Output del batch 17911abd…e2a9 non valido: descriptor: obbligatorio nei batch che costruiscono nodi.»
- Chiamate al modello: estrazione 15 (1386 s), Chat 5 (5 s), Analysis 5 (344 s); 0 `finish=length`.
- Estrazione completa (8/8 porzioni, 16/16 fatti gold nel testo). Indice di recupero: 3 preparazioni `Ready` in 9,7 s (embedding reali, modelli riattivati on-demand).

**Le cinque chiamate della gerarchia** (archivio locale `%TEMP%\bprojects-diag\20261111-160309-…RetrievalGapStageMafRealTests_Cam-48872147\`):

| # | Batch | Schema inviato (v8) | Output | Esito |
|---|---|---|---|---|
| 022 | foglia A | `Full` | descriptor di 252 caratteri, 140 items, 2 candidati | ok |
| 023 | foglia A | `Full` | descriptor di 275 caratteri, 142 items, 3 candidati | ok |
| 024 | riduzione A | `NoItems` | descriptor di 153 caratteri, 1 candidato | ok |
| 025 | foglia B | `NoItems` | descriptor di 132 caratteri, 3 candidati | ok |
| **026** | **foglia B** | **`NoItems`** | **descriptor `null`**, `noFindings` null, items `[]`, 5 candidati `DependsOn`/`Inferred` verso E225, focus null, patches `[]` | **rifiutato** |

- **Dati della chiamata 026:** 13921 token di prompt, 528 di completamento, `finish=stop`, 14,4 s.
- **SHA-256 dei file locali (non versionati):**
  - `026-assistant.txt` (1553 byte): `367067BE…F50A`;
  - `026-request.json`: `00F747F1…13BE`;
  - riepilogo `T040-single-164956.txt`: `C1CAE75E…105F`;
  - `diagnostic-report-20261111-160307-T040-single.json`: `7DAA70C3…3BF0`.
- L'output 026 è versionato come fixture dei test (`ReconcileSchemaProfileV9Tests.Run3Call026`).

**Diagnosi (offline, nessuna inferenza).**
- La correzione v8 ha funzionato su ciò che vincolava: in tutte le foglie B `items = []`, contro i 139 items del Run 2.
- Il fallimento è una **discrepanza fra schema e validatore**: in ogni variante v8 lo schema dichiarava `descriptor` come `["string","null"]`, mentre il validatore della gerarchia (`ReconciliationHierarchy.InvokeAsync`) esige un descriptor non nullo nei batch che costruiscono nodi.
- Il prompt v4 lo dichiara già («obbligatorio nelle foglie e nelle riduzioni»): non è un problema di prompt.
- La matrice completa dei vincoli ha trovato altre discrepanze dello stesso tipo:
  - focus non nullo e patch ammessi nella foglia A;
  - estremi `N` ammessi nelle foglie, `E` nella riduzione e nel cross;
  - esiti di `focus` non distinti fra espansione e confronto;
  - descriptor valorizzato in cross, espansione e confronto.
- Ci sono inoltre regole condizionali o semantiche (D9–D12) che la grammatica non esprime in modo semplice.
- **Osservazione di qualità, fuori perimetro:** i 5 candidati della 026 sono collegamenti `Inferred` deboli («potrebbe…») tutti verso la stessa evidenza. Il confronto con la chiamata 025 del Run 2 resta da fare.

## Correzione dopo il Run 3: definizione `project-analysis-documents-v9` (2026-11-11; OFFLINE, nessuna inferenza DGX; T040 resta aperto)

**Decisione del committente.** Livelli 1 e 2 della proposta: profilo `reconcile-alias-schema-by-batch-2` con sei varianti per stadio e vista, `descriptor` come `type: string` con `minLength: 1` senza `maxLength`, prompt `document-reconcile-v4` invariato, DTO, compositore e validatore invariati, compatibilità integrale v4–v8. Il livello 3 (D9–D12) è rinviato. Contratto: `contracts/candidate-source.md` §4.2; 002 `contracts/ai-workflow.md` §11.

**Supporto del provider.**
- Verificato sul sorgente di llama.cpp b10703 (`common/json-schema-to-grammar.cpp`):
  - supportati `type` (anche come array e `null`), `enum`, `const`, `pattern` ancorato, `minItems`/`maxItems` (solo con `items`), `minLength`/`maxLength` (solo con `type: "string"` esplicito, e ignorati se c'è un `pattern`), `anyOf`/`oneOf`;
  - **non** supportati `\d \w \s \S \b`: un pattern che li contiene diventa «qualunque stringa» con un solo avviso;
  - ignorati in silenzio `uniqueItems`, `if`/`then`/`else`, `not`.
- La v9 usa solo costrutti già esercitati realmente (rapporto `schema-support`), più `type: "string"` non nullabile e `minLength: 1`. Quest'ultimo è **non esercitato realmente**: supporto inferito dal sorgente.

**Versionamento e compatibilità.**
- La v8 non è stata modificata. Prima del refactoring sono stati scritti e committati test golden:
  - hash delle varianti `NoItems` `7b0a15de…52b2` e `Candidate` `facb96cb…4f38`;
  - stringa `SchemaProfileHashes`;
  - `configHash` del manifest di una run v8 = `4248ba71d408bcc425d61a7e1c2d54b867a56110fc876480cd70f56c38b2f3d1`.
  Restano verdi dopo il refactoring (`ReconcileAliasMode.SchemaProfile` al posto del booleano).
- Golden del profilo v9: `ReconcileSchemaProfileV9Tests.PinnedV9ProfileHashes`.
- Le nuove richieste con recupero congelano la v9 attraverso un'unica funzione (`SourceSnapshotStore.DocumentaryAnalysisFreeze`), usata anche dall'harness T040.
  - Effetto: il preflight del manifesto T040 v4, che è legato alla v8 e **non modificato**, risulta ora NO-GO prima di ogni upload e inferenza. Prima l'harness costruiva l'identità del prodotto con costanti v8 e la discrepanza sarebbe emersa solo al controllo strutturale a fine run.
  - Un eventuale Run 4 richiede un manifesto nuovo: **non preparato**.

**Test (offline).**
- `ReconcileSchemaProfileV9Tests` — tabella stadio/vista e dispatch, forma di ogni variante, elenco chiuso di keyword compatibili con llama.cpp, golden v9. Inoltre:
  - equivalenza fra schema e compositore per 6 varianti × 8 violazioni strutturali;
  - regola del descriptor contro il vero validatore della gerarchia (`ReconciliationHierarchy.BuildAsync`), con i residui spazi e >512 caratteri rifiutati dal solo validatore;
  - D9–D12 ammessi dalla grammatica e rifiutati dal validatore;
  - regressione sulla 026 del Run 3: ammessa dallo schema v8, rifiutata dallo schema v9 solo per il descriptor, rifiutata dal validatore con lo stesso messaggio;
  - conservazione dei 5 candidati (stesse identità canoniche e motivazioni) con un descriptor valorizzato.
- `ReconcileDefinitionV9Tests` — contratto 9, freeze uguale alla v8 salvo il profilo, nessuna reinterpretazione fra v8 e v9.
- `RetrievalGapFlowTests` (PostgreSQL) — teorie v8/v9:
  - schema realmente inviato a ogni batch e profilo nel manifest;
  - `configHash` della v9 diverso dal golden della v8;
  - risposta di forma 026 rifiutata senza commit né chiamate successive;
  - crash e ripresa su una foglia B con la variante del profilo congelato;
  - foglia B con items rifiutata.
- `RetrievalGapCampaignTests` — identità v8 coerente con il manifesto v4, identità del prodotto v9 NO-GO.

**Rischi residui.**
- D9–D12, spazi e lunghezza del descriptor, `noFindings` con output vuoto: affidati al solo validatore, che li rifiuta con `work_output_invalid` permanente (nessun retry).
- `minLength` non esercitato realmente.
- Costrutti ignorati in silenzio dal provider: mitigati dal test sull'elenco chiuso di keyword, non da una prova reale.
- La v9 non è mai stata eseguita su un modello reale.
