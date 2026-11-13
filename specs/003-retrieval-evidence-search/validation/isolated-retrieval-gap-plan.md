# Piano — verifica isolata Retrieval → Gap Stage (003)

Stato: **PIANO approvato e IMPLEMENTATO OFFLINE (2026-11-13).** Redatto dopo il Run 5 di T040 (NON superato). Nessuna inferenza DGX eseguita; nessuna modifica a `backend/src`, riconciliazione, manifesti T040, prompt o schemi. La prova reale (DGX) **non è stata eseguita** e richiede una autorizzazione separata.

Implementazione (solo test): `RetrievalIsolatedFixture.cs` (ledger controllato e manifesto), `StageOnlyModelRouter.cs` (instradamento fail-closed), `RetrievalGapIsolatedTests.cs` (seminagione, verificatore, replay/crash, report; classi `Offline`, `RealSingle`, `RealMulti` indipendenti), `RetrievalIsolatedFixtureTests.cs`, manifesto `isolated-retrieval-gap-manifest.json`. Scostamenti dal piano: l'instradamento ammette anche `/slots` (sola lettura, osservata nel client di produzione); lo stadio Gap offline vede un ledger di 283 evidenze (una per frase) contro la finestra di 6000 token del modello finto; uso e `finish_reason` della chiamata dello stadio sono letti dal router (il workflow manuale non scrive `InferenceCalls`).

**Cosa è e cosa non è.** Questo piano descrive un test *complementare* a T040. Un suo eventuale esito favorevole **non** è un PASS di T040, **non** chiude M3, **non** sostituisce la pipeline end-to-end e **non** riduce alcuna soglia. T040 resta APERTO; soglie e gold congelati (manifesto v6, `871e884c…0599`) non sono toccati.

---

## 0. Analisi preliminare (fatti verificati nel codice e negli artefatti)

### 0.1 Il punto più basso in cui si può introdurre un ledger noto

Percorso di produzione, dal basso verso l'alto (letto nel codice, non supposto):

| Livello | Componente reale | Introducibile con ledger noto? |
|---|---|---|
| Blocchi + evidenze nel database | `DocumentBlocks`, `EvidenceItems`, `EvidenceExtractionVersions`, `EvidenceDocumentStates` | **Sì**: lo fa già `RetrievalGapFlowTests.NewRetrievalDocument` (seminagione diretta con `session_replication_role = replica` per le FK del solo parsing) |
| Indice derivato | `RetrievalIndexBuilder.BuildAsync` / `RetrievalIndexMaintenance.RebuildAsync` | **Sì**, con embedding reali: stessa chiamata della campagna T040 |
| Snapshot e congelamento del run | `POST /api/projects/{id}/analyses` → `EvidenceLedgerStore.FreezeDocumentSelectionAsync` | **Sì**: endpoint reale, già usato da `ArrangeAsync` |
| Recupero | `RetrievalPipeline.ExecuteAsync` (ricerca ibrida PostgreSQL + RRF + riordino + `CandidateRegistry.Build`) | **Solo dentro il workflow** (vedi sotto) |
| Stadio Gap | `GapStageBuilder.BuildFromRegistry` + chiamata LLM + `GapStageContracts.Validate` | **Solo dentro il workflow** |

**Vincolo strutturale verificato.** `PrepareRetrievalAsync` e `PrepareGapAsync` (`AI/DocumentReconcilePlan.cs`) sono eseguiti *dopo* la gerarchia e usano `GapState(replay) ?? throw CheckpointIncompatible`: lo stadio Gap richiede i riferimenti (`GapStageReferences`: domande e conflitti Open, mappa Gap→domanda) prodotti da una gerarchia completa. Non esiste un punto di ingresso pubblico che chiami retrieval e stadio Gap senza gerarchia. **Non lo si inventa**: lo si aggira senza toccare il prodotto, con il modello della gerarchia *deterministico* e quello dello stadio *reale* (§1).

### 0.2 Dati del Run 5 ancora disponibili

Originali in `%TEMP%\bprojects-t040-campaign` e `%TEMP%\bprojects-diag\20261113-093355-…`; copia con `SHA256SUMS.txt` (131 file, `f9a0daa6…fabfa`) nella cartella di sessione `run5-evidence`.

| Disponibile | Contenuto |
|---|---|
| `corpus-single/capitolato-aurora.txt` | testo del corpus Single, SHA-256 `fb2fafe4…777b`, 21 708 byte |
| `maf-diag-session/NNN-request/response/assistant/result` (125 file, 30 chiamate) | richieste HTTP e risposte grezze del modello: 1 sonda, 5 chat, 15 estrazione, 9 riconciliazione (022–030) |
| `events.jsonl` (5 766 eventi) | sequenza HTTP, 3 315 `/tokenize` |
| `diagnostic-report-…-T040-single.json` | readiness, indicizzazione (117 + 2 + 2 blocchi, 10,5 s), `analysisCalls`, `launch` |
| `db-final.json` / `db-snapshot.json` | **solo** `InferenceCalls`, `ProcessingRuns`, `ChatTurns`, `ConversationMessages`, `AnalysisRevisions`, `Drafts` |

**Esiste un ledger canonico recuperabile? No.** Le tabelle `DocumentBlocks`, `EvidenceItems`, `DocumentPreparations` e l'indice appartenevano al database di campagna (`bprojects_test_<guid>`), eliminato a fine test; l'esportazione non le includeva. Restano le risposte grezze dell'estrazione, ma il ledger canonico (alias → blocco, citazioni risolte, ordinali) è il risultato della composizione del prodotto, non è stato salvato.

**Ricostruzione dello snapshot da fixture.** Necessaria. Identità e provenienza si garantiscono con un manifesto proprio del test (§2.3), non riusando né modificando quello di T040. La rigiocabilità del ledger del Run 5 dalle risposte registrate (livello B, §2.2) è possibile in linea di principio ma **non** è nel perimetro minimo.

### 0.3 Cosa serve solo all'harness e cosa (eventualmente) al prodotto

- **Solo harness (test):** seminagione del ledger; handler HTTP che instrada la chiamata dello stadio al modello reale e risponde alla gerarchia in modo deterministico; classe di test; verificatore dei criteri; manifesto della fixture. Nessun cambio a `backend/src`.
- **Prodotto:** **nessuna modifica necessaria** per il perimetro minimo. Le uniche limitazioni (gerarchia obbligatoria, tokenizzazione reale sequenziale) sono registrate come rischi, non come lavori da fare.
- **Da non fare:** bypass del requisito di gerarchia nel prodotto, nuove definizioni di workflow, nuovo punto di estensione. Qualunque di queste richiederebbe una nuova autorizzazione.

---

## 1. Architettura del test isolato e confini reali

```
 fixture (corpus T082/T083 + gold congelati, ledger noto)
        │ seminagione diretta (come RetrievalGapFlowTests)
        ▼
 PostgreSQL REALE (db nuovo bprojects_test_<guid>)  ──►  RetrievalIndexMaintenance.RebuildAsync
        │                                                   └─ EmbeddingClient REALE (bge-m3-Q5)
        ▼
 POST /api/projects/{id}/analyses  (endpoint REALE → congela v10 + snapshot + PromptVersions)
        ▼
 DocumentAnalysisWorkflow.ExecuteAsync  (prodotto REALE, MAF 1.26.0, checkpoint PostgreSQL)
   ├─ gerarchia (foglie, riduzioni, cross, confronto) ──► modello DETERMINISTICO (stub)  ← unico componente non reale a monte
   ├─ batch `retrieval`: RetrievalPipeline REALE ─ ricerca ibrida PG ─ reranker REALE (bge-reranker-v2-m3-Q5)
   │        └─ registro congelato in WorkOutput `retrieval-output-1`
   └─ batch `gap`: GapStageBuilder.BuildFromRegistry ─► LLM REALE (Qwen3.6-35B-Smart, UNA chiamata)
            └─ GapStageContracts.Validate ─ commit WorkOutput `gap-stage-alias-1` ─ coverage-3 / API
```

**Reali:** PostgreSQL, endpoint, workflow, checkpoint/replay, indicizzazione, embedding, ricerca ibrida, reranker, composizione del registro, messaggio dello stadio, **LLM dello stadio Gap**, validatore, coverage/API, tokenizzatore del DGX (`/tokenize`, `/props`: non generativi).

**Non reale (dichiarato in ogni rapporto):** l'estrazione (il ledger è una fixture) e le **risposte generative della gerarchia** (stub deterministico: un'unica voce per evidenza nella foglia A, i Gap come `openQuestions`, nessun candidato, `focus` di confronto `Compared`; stessa tecnica di `RetrievalGapFlowTests.Hierarchy`). Lo stub *non* chiama il DGX.

**Instradamento (solo harness).** Un `DelegatingHandler` passato a `InternalModelClient` (stesso punto dove oggi `LargeDocumentRealFlow` inietta `MafDiagnostics.Wrap()`):

- `POST /v1/chat/completions` il cui testo contiene `Formato: gap-stage-message-1` (la stessa discriminante di `DocumentGapStageWorkflowTests.IsStage`) → modello reale, con `MafDiagnostics` per la registrazione grezza;
- `POST /v1/chat/completions` di gerarchia → risposta dello stub, **mai** inoltrata;
- `/tokenize`, `/props`, `/apply-template`, `/v1/models` → inoltro reale (necessari: il budget dello stadio è misurato sul tokenizzatore reale);
- **fail-closed**: seconda chiamata di stadio, richiesta di chat non riconoscibile, o qualunque altro percorso ⇒ errore del test (nessun retry, nessuna chiamata generativa fuori dallo stadio).

---

## 2. Dati di input, fixture, gold e provenienza

### 2.1 Gold e attese (invariati)
Si riusano **per riferimento** `RetrievalGapCampaign.Single` / `Multi` (cinque attese ciascuno: present, partial, absent, conflict, excluded), `ExtensionDocuments`, `LargeDocumentCorpus.Single(0, 260)` / `.Multi(0, 90)`, le soglie e `AllowedLostConflicts`. Nessuna copia dei valori, nessuna attesa nuova o adattata.

### 2.2 Ledger
- **Livello A (perimetro minimo, raccomandato).** Ledger *idealizzato*: per ogni blocco del corpus una evidenza con enunciato = testo del blocco (stessa regola di `NewRetrievalDocument`), le cinque domande come evidenze `Gap`, il documento d'estensione parziale incluso e quello escluso dal PM (PUT `source-selection`, `If-Match` corrente). Dimensione tarata sul **Run 5**: 117 blocchi (Single) + 2 + 2; per Multi i blocchi del corpus T083.
- **Livello B (facoltativo, solo se serve realismo).** Rigioco delle risposte di estrazione registrate nel Run 5 tramite `FakeChatModel` indicizzato per hash del corpo della richiesta; ammesso solo se **tutti** gli hash delle richieste ricostruite coincidono con quelli registrati, altrimenti il livello B è scartato (nessun adattamento). Non incluso nelle stime né nel GO.

**Limite dichiarato:** il livello A non misura la qualità dell'estrazione né la dimensione reale del ledger estratto. Il ledger deve comunque **superare il budget di input dello stadio** (B = 32768 − 16384 − 1024 = 15360 token) altrimenti il recupero non interviene (FR-002, sorgente esaustiva): la taratura si verifica con una misura non generativa (`/tokenize`) *prima* di qualunque chiamata dello stadio; se il ledger entra, il test è NO-GO per quello scenario.

### 2.3 Provenienza e identità
Un manifesto nuovo, `validation/isolated-retrieval-gap-manifest.json` (hash canonico RFC 8785 + SHA-256 del file, stesso schema di prova di `T040-campaign-manifest.json`), che registra: SHA-256 del corpus e dei gold (quelli già nel manifesto v6), SHA-256 canonico del ledger seminato (ordine, enunciati, tipi, blocco-sorgente), conteggi per documento, politica `retrieval-policy-1` + hash, modelli e artefatti attesi, finestra/budget, definizione `project-analysis-documents-v10` e `commitBaseline = d63e7cb`, e l'elenco dei limiti (§1). Un test offline lo fissa; qualunque deriva è NO-GO. **Non** è una "v7" di T040 e non porta il nome T040.

---

## 3. Sequenza completa

1. **Preflight** (nessun DGX generativo): HEAD, albero tracciato pulito, `backend/src` e `global.json` identici al baseline, manifesto isolato invariato, `BPROJECTS_TEST_DGX_ALLOW=1` solo nel processo, filtro = un solo test, readiness embedding/reranker (sonda del prodotto), parser **non** necessario.
2. **Database nuovo** `bprojects_test_<guid>` (PostgresDatabaseFixture); controllo che non esistano righe `DocumentPolicies` residue.
3. **Seminagione** documenti, blocchi, evidenze, stati (Single). Verifica di conteggi e hash del ledger contro il manifesto.
4. **Indice**: `RetrievalIndexMaintenance.RebuildAsync` con embedding reali; richiesto Ready=Failed 0 e blocchi = attesi.
5. **Esclusione del PM** (documento d'estensione), poi `POST /analyses` (202), claim del run.
6. **Esecuzione del workflow** con l'handler di §1: gerarchia stub → batch `retrieval` → misura costi (`/tokenize`) → batch `gap` (UNA chiamata) → validazione → commit.
7. **Verifiche** (§4) su `WorkOutputs`, `DerivedBatches`, `InferenceCalls`, coverage-3, `GET retrieval-registry`.
8. **Replay/checkpoint** (§4.3): secondo `RunAsync` sullo stesso run con un modello che *fallisce se invocato*; poi un crash simulato fra recupero e stadio solo offline (§6).
9. **Esportazione diagnostica** prima di `DROP DATABASE` (come `OnFinalizingAsync` della campagna).
10. Ripetere 3–9 per Multi **solo se** Single è PASS (arresto al primo blocco, ordine fisso Single → Multi).

---

## 4. Criteri di T040: riproducibili isolatamente vs. end-to-end

Le tabelle seguono i criteri di `tasks.md` T040 e i controlli di `VerifyExtraAsync`.

### 4.1 Riproducibili in isolamento
| Criterio T040 / controllo | Come si verifica | Nota |
|---|---|---|
| nessun `gap_ledger_exceeds_input` | esito del run e messaggio | richiede ledger > B (§2.2) |
| 1 batch `retrieval` + 1 `gap`, il recupero precede lo stadio, nessuna chiamata LLM per il recupero, UNA chiamata dello stadio senza retry | `DerivedBatches`, `WorkOutputs`, `InferenceCalls` | chiamate generative DGX = 1 |
| registro `retrieval-registry-1`: sorgente `retrieval`, `Package`, non degradato, politica e hash, modelli osservati = fissati | `RetrievalOutput.Read` | |
| un'entrata per Gap annotato, pacchetto non vuoto, **pacchetto = sottoinsieme proprio** del ledger non-Gap | registro | |
| il documento escluso non compare mai nel registro (candidati, pacchetto, sempre-inclusi) e **è** nell'indice Ready | registro + SQL indice | prova che l'esclusione è dello snapshot |
| lo stadio ha ricevuto SOLO il pacchetto (candidati = pacchetto, `searchBasis: retrieval`, `retrievalComplete` come nel registro) | messaggio registrato (`MafDiagnostics`) | |
| 100% dei Gap con record finale valido; esito atteso per ogni Gap; motivazione e snapshot hash | coverage-3 `gapAssessments` | **solo i Gap presenti nel ledger fixture** |
| **0 falsi `AnsweredInSnapshot`**, nessuno fuori dalle attese | coverage-3 | |
| supporti = evidenze documentali del pacchetto (mai Gap, mai fuori pacchetto, mai documento escluso); citazione esatta nel blocco; citazioni invalide ≤ 2 su 10 | confronto con i blocchi | proporzione come nel harness |
| assenza mai provata (`absenceProven != true`) | coverage-3 | |
| conflitti persi per esclusione a monte = 0: **entrambi i lati** del gold sono nel pacchetto | registro vs. gold | è la parte *recupero* di SC-003 |
| latenza del recupero per Gap (ricerca + riordino, fasi condivise a parte) | `RetrievalTimingLog`, `RetrievalCallRecorder` | |
| nessuna chiamata embedding/riordino non-2xx (nessun fallback silenzioso) | recorder | |
| chiamata dello stadio non troncata e prompt ≤ B | `InferenceCalls` | |
| checkpoint/replay senza duplicazioni | §4.3 | |

### 4.2 Che richiedono l'end-to-end (NON dimostrabili qui)
| Criterio | Perché |
|---|---|
| «la domanda è stata estratta come UNA evidenza Gap» | dipende dall'estrazione LLM, assente nel livello A |
| 100% dei Gap con record valido **a partire da un ledger estratto e da una gerarchia reale** | la gerarchia è stub |
| **`openConflict` valorizzato e conflitto `Open` nella revisione** (attesa `ExpectOpenConflict`) | lo stadio può citare solo i conflitti offerti da `GapStageReferences`; lo stub non genera conflitti. Nessuno stub esistente ne produce. Da verificare con uno spike offline di 30 min se un'azione di patch esistente lo consente; se no, il caso `conflict` è giudicato su `openItem` e **contrassegnato come non equivalente** all'attesa T040 |
| `gap_ledger_exceeds_input` con ledger reale e dimensioni reali | ledger fixture |
| espansione v10 (blocchi, figli, saturazioni, `expansion_budget_exceeded`), rischio Cross, limite 512 chiamate | mai esercitati (Run 5 ha fallito prima) |
| durata complessiva, chiamate e token per fase del percorso reale | |
| SC-001 nel suo insieme | richiede estrazione + riconciliazione + recupero + stadio |

### 4.3 Checkpoint e replay
Dopo il commit: rieseguire il workflow sul **medesimo** run con un modello che solleva eccezione a qualunque chat; atteso esito invariato, 0 nuove chiamate (modello, embedding, riordino), stesso `registry` (hash), un solo WorkOutput per schema, nessun duplicato in `DerivedBatches`. È già coperto offline da `RetrievalGapFlowTests.Replay_and_takeover…` e `A_crash_between_the_retrieval_and_the_stage…`: l'esecuzione reale verifica solo che nulla cambi con modelli reali.

---

## 5. PostgreSQL, modelli, readiness, cleanup

- **PostgreSQL**: container `bprojects-test-pg` (porta 15432), un database per classe di test, creato e eliminato da `PostgresDatabaseFixture`. Nel Run 5 il server conteneva **7 database orfani** (11–12 novembre) che non influiscono sul run; la loro eliminazione è distruttiva e richiede una conferma separata, non fa parte del piano.
- **Modelli**: l'embedding `bge-m3-Q5` e il reranker `bge-reranker-v2-m3-Q5` risultavano `ModelUnloaded` prima dell'indicizzazione e sono caricati dal router alla prima richiesta (attivazione on-demand del prodotto: `EnsureEmbeddingReadyAsync` / `EnsureRerankerReadyAsync`). Nessun comando manuale di load/unload. SHA-256 dei pesi: `NotVerified` finché T046 non è eseguito dall'amministratore (registrato nel rapporto).
- **Readiness**: sonda di prodotto prima dell'indicizzazione e stato finale; qualunque stato ≠ Ready dopo l'attivazione ⇒ NO-GO senza chiamate allo stadio.
- **Cleanup**: esportazione di `InferenceCalls`, `ProcessingRuns`, `WorkOutputs`, `DerivedBatches` e delle righe del registro **prima** del drop; il drop resta automatico.
- **Servizi non necessari**: parser Docling (il ledger è seminato). `BPROJECTS_TEST_PARSER_*` non richiesto.

---

## 6. Test offline necessari prima di qualunque chiamata DGX

Tutti con `FakeEmbeddingServer`/`FakeRerankServer`/`FakeChatModel` come in `RetrievalGapFlowTests`, PostgreSQL reale ma nessun DGX:

1. **Manifesto**: hash canonico e del file; deriva ⇒ rosso (modello: `RetrievalGapCampaignTests`).
2. **Ledger**: la seminagione produce i conteggi e l'hash attesi; identica ai gold (nessuna attesa nuova); il documento escluso produce evidenze.
3. **Taratura**: con il tokenizzatore finto il ledger non è dimensionabile; test di sola logica che la taratura reale rifiuti (NO-GO) un ledger che entra in B.
4. **Percorso completo con modelli finti**: stesso harness di §3 con `FakeChatModel` per lo stadio; tutti i controlli di §4.1 devono risultare PASS.
5. **Verificatore**: casi negativi che *devono* fallire — falso `AnsweredInSnapshot`, citazione non esatta, lato del conflitto perso, documento escluso nel pacchetto, supporto fuori pacchetto, record mancante.
6. **Handler di instradamento**: una sola richiesta di stadio inoltrata al "reale" (contatore); gerarchia mai inoltrata; seconda chiamata di stadio e percorso ignoto ⇒ errore; `/tokenize` inoltrato.
7. **Replay/crash**: rigioco senza chiamate; crash fra recupero e stadio riprende senza ripetere il recupero (già coperto, richiamato nel nuovo harness).
8. **Guardia DGX**: senza `BPROJECTS_TEST_DGX_ALLOW=1` il test è ignorato con messaggio (`DgxGuardTests`).
9. **Regressione**: l'intera suite esistente, in particolare `RetrievalGapCampaignTests` e `RetrievalGapFlowTests`, resta verde e il manifesto v6 di T040 è invariato.

---

## 7. Stima di chiamate, token e tempi

Basi osservate nel Run 5: indicizzazione 121 blocchi in **10,5 s**; `/tokenize` ≈ **0,32 s/chiamata** in sequenza (2 414 chiamate tra 10:00:50 e 10:13:41); chiamate di riconciliazione 3–154 s a seconda dell'output.

| Voce | Per scenario | Note |
|---|---|---|
| Chiamate generative al DGX | **1** (stadio Gap) | la gerarchia è stub; 0 nell'estrazione; 0 nel recupero |
| Token dello stadio | input ≤ 15 360, output atteso ~1–3 k (5 Gap), massimo 16 384 | non troncata richiesta |
| `/tokenize` (non generativo) | ~2 000–2 500 per un ledger di taglia Run 5 | **dominante nel tempo**, vedi rischio R2 |
| Embedding: indice | = blocchi (117–~400 se Multi) | 10–40 s |
| Embedding/riordino per Gap | 5 + 5 | secondi |
| Tempo Single | **~15–20 min** (≈13 min di tokenizzazione + indice < 1 min + recupero < 1 min + stadio 1–3 min) | |
| Tempo Multi | analogo, ~15–25 min | |
| Totale (Single → Multi) | **~30–45 min** | contro 47 min del Run 5 che si è fermato a metà del solo Single |
| Chiamate cumulate rispetto al limite 512 | irrilevante (stub ≈ 9 batch) | |

Le stime sono ordini di grandezza da dati del Run 5, non garanzie.

**Effort di implementazione:** circa 3 file nuovi nei test (fixture+seeder ~250 righe, handler ~80, classe di test e verificatore ~350) più i test di §6 e il manifesto; **~1–1,5 giorni**, **0 righe in `backend/src`**.

---

## 8. Rischi residui e condizioni GO/NO-GO

### Rischi
- **R1 — Gerarchia stub:** i riferimenti dello stadio (domande, conflitti) sono sintetici; non si dimostra nulla sulla qualità della gerarchia né sul caso `openConflict` (§4.2).
- **R2 — Anomalia prestazionale della tokenizzazione:** il percorso reale misura il costo con migliaia di probe `/tokenize`; l'isolamento *non* la elimina (serve il tokenizzatore reale per B). Non è un blocco ma domina i tempi.
- **R3 — Ledger idealizzato:** un test superato non dice nulla sull'estrazione.
- **R4 — Difetto della chiamata 030** (Appendice A) può riapparire se la gerarchia stub fosse sostituita da una reale: nel piano non si usa il confronto reale, quindi non viene esercitato né corretto.
- **R5 — Modelli scaricati:** il primo uso può richiedere minuti; nessun warm-up manuale.
- **R6 — Tentazione di sostituzione:** il rapporto deve riportare «NON equivale a T040» in intestazione, in coda e nel nome del test.
- **R7 — Lo stadio Gap con recupero non è mai stato raggiunto in un run reale:** non esistono misure reali; è la ragione del piano, ma un primo esito sfavorevole sarà informativo, non un'anomalia del test.

### GO (tutte vere)
1. Piano approvato; test offline di §6 verdi; nessuna modifica a `backend/src`; albero pulito.
2. Manifesto isolato invariato; HEAD e baseline coerenti.
3. Embedding e reranker Ready dopo attivazione di prodotto, con gli artefatti attesi.
4. Taratura: ledger misurato > B per lo scenario.
5. Autorizzazione esplicita di **una** esecuzione (Single, poi Multi solo se Single PASS), `BPROJECTS_TEST_DGX_ALLOW=1` solo nel processo.

### NO-GO (una sola basta)
Qualunque controllo di §3.1 fallito; indice non completo; modello non Ready; ledger che entra in B; seconda richiesta di stadio; richiesta generativa fuori dallo stadio; hash del manifesto diverso. Nessun retry, nessuna correzione dell'ambiente per forzare il test.

---

## 9. Dipendenze reali dei task T041–T050

(da `tasks.md`: dipendenza dichiarata e natura della verifica)

| Task | Dip. dichiarata | Natura | Dipende davvero da T040? | Offline sotto deroga? |
|---|---|---|---|---|
| T041 padri selettivi | T040 (o deroga), T025 | DET+PG+IMPL | formalmente sì (rimedio: deroga); richiede **approvazione esplicita** di `gap-stage-message-2` (rischio 9 del piano) | sì, **dopo** quell'approvazione |
| T042 misura padri | T041 | DGX | sì (usa T082/T083 e DGX) | **no** |
| T043 decisione M4 | T042 | GATE | sì | **no** |
| T044 cancello regressione CI | T040, T043 | DET+PG (+DGX opt-in) | la modalità simulata no; il confronto reale e l'attivazione del cancello sì | modalità simulata sì; nulla di "attivato" |
| T045 latenza e costo | T040 | DGX | sì (indice pronto per Gap); la misura isolata di §4.1 ne fornisce un **ingresso parziale**, non lo sostituisce | **no** |
| T046 SHA-256 dei pesi | T008, T021 | GATE | **no**; operazione dell'amministratore DGX | indipendente |
| T047 operations.md | T040 | DOC | solo per contenuti sull'esito; lo stato attuale (T040 non superato) è documentabile | sì |
| T048 valutazione PM | T044 | PM+DGX | dipende da persone e dati | **no** |
| T049 rapporto di accettazione | T044, T045, T046, T048 | GATE | indiretta | **no**, tranne la bozza «accettata con riserva» |
| T050 conformità finale | T049 | GATE | indiretta | **no** |

---

## 10. Proposta di deroga (M4/M5 offline) — T040 resta APERTO

Testo proposto, sul modello della deroga del 2026-11-10 già in `tasks.md`:

> **Deroga del committente (data da fissare) — sviluppo OFFLINE di M4/M5 non dipendente dal gate.** T040 è **aperto e non superato** (Run 5 del 2026-11-13). Il committente autorizza lo **sviluppo offline** di: (a) T041 solo dopo l'approvazione esplicita del formato `gap-stage-message-2` e con la politica `retrieval-policy-2` **non** promossa a default; (b) la modalità simulata di T044 e solo quella; (c) T047 con lo stato reale di T040; (d) i test offline del piano di verifica isolata. La deroga **non** dichiara superato T040, **non** chiude M3, **non** riduce soglie, **non** autorizza DGX, benchmark reali, T042, T043, T045, T048, T049 con esito "accettata", T050, merge su `main` né rilasci. «Implementato/verificato offline» ≠ «accettato».

Vincoli: nessuna modifica ai contratti della 002, ai gold o ai manifesti T040; M4 si considera *iniziato* solo con quell'autorizzazione scritta; T046 prosegue per conto dell'amministratore del DGX.

**Aggiornamenti documentali (eseguiti con l'implementazione):** `tasks.md` T040 e `validation/M3-real.md` §«Esito del Run 5» riportano l'esito reale del Run 5; il difetto della 030 e l'osservazione sui `/tokenize` sono nel backlog tecnico della 002 (`docs/roadmap/slice-002-technical-closure.md` §17). I fatti sono in Appendice A, B e C.

---

## Appendice A — Difetto della chiamata 030 (registrato, nessuna correzione)

**Fatti (batch `51571354d62e…b519`, vista A, stadio `compare`, 2 voci: E165 ed E262).** Risposta del modello (`030-assistant.txt`):

```json
{"contract":"reconcile-alias-1","descriptor":null,"noFindings":null,"items":[],"candidates":[],
 "focus":{"outcome":"Open","reason":"Evidenze E165 e E262 definiscono finestre di manutenzione programmata incompatibili … Non è possibile determinare quale sia corretta senza verifica esterna."},
 "patches":[]}
```

Esito: `work_output_invalid`, primo tentativo (`ReconciliationContracts.cs:699`): *«noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch.»*

**Verifica per strato.**

| Strato | Posizione | Cosa dice sul caso `focus=Open`, `noFindings=null`, `items/candidates/patches` vuoti |
|---|---|---|
| Schema del confronto | `ReconcileAliasContracts.cs` (`SchemaNoItems`; `focus.outcome` ∈ {Compared, Open}; `noFindings` = testo nullable) | **Ammette** la risposta: nessun vincolo condizionale fra `noFindings` e gli altri campi |
| Prompt `document-reconcile-v5` | riga 18: «`noFindings`: una motivazione non vuota se `items`, `candidates` e `patches` sono tutti vuoti, altrimenti `null`» | **Prescrive** `noFindings` valorizzato: il modello ha disatteso l'istruzione (non-conformità del modello; l'istruzione non menziona `focus`) |
| Compositore | `ReconcileAliasComposer.Focus` | **Accetta** e produce una `CandidateDisposition` con stato `Open` e la motivazione (`Open` se `outcome == Open` e `reason` non vuoto) |
| Validatore del risultato | `ReconciliationContracts.cs:699` | **Rifiuta**: la clausola conta `ItemProposals`, `Candidates`, `Patches`, **non** `Dispositions` |

**Conclusione tecnica.** Esiste un'incoerenza **reale** fra contratto di output e validatore, su due punti distinti, entrambi verificati nel codice: (1) lo schema ammette ciò che il validatore rifiuta; (2) il compositore trasforma lo stesso input in una disposizione sostanziale (`Open` con motivo, destinata al project manager), mentre il validatore la considera «nessun risultato». Il prompt coincide con il validatore sul testo, quindi la risposta è anche non conforme al prompt. Quale dei tre strati debba cambiare è una decisione di contratto, **non presa qui**. Nessuna correzione, nessuna v11. Non è noto se altri batch di confronto del Run 4 o 5 avrebbero incontrato lo stesso caso.

## Appendice B — Anomalia prestazionale (~13 minuti prima della prima chiamata di riconciliazione)

Non è un blocco del piano; da approfondire separatamente.

**Fatti.** Ultima chiamata di estrazione 021 conclusa alle 10:00:37; `POST Analizza` 202 alle 10:00:49 (`RequestedAt`), run `StartedAt` 10:00:50; **prima** chiamata generativa del run (022) inviata alle **10:13:42**: ≈ 12 min 52 s senza richieste generative *dopo* l'avvio del run (il ritardo non è quindi fra estrazione e Analizza, ma dentro il run). In quella finestra `events.jsonl` registra **2 414 richieste `/tokenize`** al DGX (~3 al secondo, ~0,32 s ciascuna in sequenza; 3 315 nell'intero run), circa 17 per ciascuna delle ~140 voci della foglia A. Sono richieste non generative: nessuna inferenza, ma tempo e carico del DGX.

**Interpretazione (ipotesi supportata dai dati, non verificata nel codice):** il tempo è speso nella pianificazione e misurazione dei batch della gerarchia (conteggio dei token con il tokenizzatore remoto), prima della prima chiamata. L'analisi del percorso (`DocumentReconcilePlan`, `PayloadPreflight.MeasureAsync`, packing) e l'eventuale riduzione delle probe sono lavoro separato, **non** in questo piano e **non** autorizzato. Effetto sul piano: vedi R2 e §7.

## Appendice C — Stato del Run 5 (riepilogo)

Una esecuzione, commit `07c1110…`, manifesto v6 `871e884c…0599`, 46 min 57 s, uscita 2. Single: 40 PASS / 1 FAIL (`work_output_invalid` alla chiamata 030). Multi non eseguito. Recupero e Gap Stage non raggiunti; metriche T040 NotVerified; espansione v10 mai esercitata; verifica della policy congelata (`MaxAiCallsPerRun=512`) NotVerified (l'osservatore non ha prodotto output; la configurazione risolta era 512). SHA-256: `run5.log` `93e40b0910f9…74af`, rapporto diagnostico `6305bdcd6f27…7494`, `SHA256SUMS.txt` `f9a0daa6…fabfa`.
