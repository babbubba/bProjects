# Implementation Plan: Documenti di progetto

**Branch**: `feature/002-project-documents` | **Date**: 2026-11-01 | **Spec**: [spec.md](spec.md)
**Input**: `specs/002-project-documents/spec.md`; chiarimenti funzionali riesaminati senza questioni bloccanti.
**Stato**: correzione finale documentale B1 per verifica indipendente mirata; lifecycle B2 e riconciliazione B3 preservati. Nessuna implementazione, migrazione o prova runtime eseguita; nessuna checklist rivalidata. Le verifiche indicate sono gate futuri, non PASS.

## Summary

**Revisione T130/N4 approvata 2026-11-07, solo pianificazione**: [AI §13](contracts/ai-workflow.md#13-revisione-approvata-t130n4--a--c-minima-2026-11-07) governa il delta. Codice a `65a9204`: Analizza crea v4; il riferimento T125 a v3 per le nuove richieste è storico. Dopo integrazione (T141: single-call completo T137 + gerarchia sicura T143, **decisione C + D 2026-11-07**, AI §13.6) i nuovi run saranno v5; v2/v3/v4 restano compatibili. Branch di questa revisione `feature/002-implementation`; intestazione originale conservata. Nessuna implementazione/verifica nuova attestata.

Estendere il monolite esistente con documenti/versioni immutabili su volume privato, parsing Docling isolato e provenienza documentale nel medesimo Evidence ledger. Nessun RAG o indice di ricerca (chiarimento 2026-11-09: lo stadio Gap su ledger completo della definizione v6, AI §13.7, **non è RAG né indice di ricerca**: nessun ranking, top-k, indice o embedding, ma una chiamata sull’intero ledger congelato). Elaborare tutte le porzioni selezionate con estrazioni bounded, mantenere evidenze e risultati intermedi nel dominio e usare workflow MAF checkpointati per consolidamento e generazione multipasso. Non condensare tutta la conoscenza in una sintesi o in un unico output limitato.

Fonti, esclusioni e versioni sono congelate alla richiesta di chat/Analizza. Generazione usa esclusivamente la revisione confermata. Upload e preparazione non avviano analisi. Gli artefatti della 001 restano immutati; la sua accettazione con riserve e T052/T053 aperti restano tali.

## Technical Context

**Language/Version**: C#/.NET 10, Angular 22/Bootstrap 5.3.8 esistenti; Python 3.12 per parser.
**Primary Dependencies**: MAF core/OpenAI/Workflows esattamente 1.26.0, EF Core 10, Npgsql 10.0.3 esistenti. Docling 2.129.0 come baseline riproducibile da validare e bloccare con transitive/hash e artefatti modelli; non scelta di latest. Motivazioni/fonti in [research](research.md).
**Storage**: PostgreSQL 18 già presente; volume filesystem privato persistente per originali e artefatti voluminosi. Nessun object store remoto, broker, pgvector o servizio di ricerca (invariato dallo stadio Gap v6: nessun indice, JSONB esistenti).
**Testing**: xUnit v3, Vitest, Playwright esistenti; test parser nel container Python, fixture autorizzate/golden. PostgreSQL, Docling, MAF .NET e DGX reali provati separatamente e insieme.
**Target Platform**: Linux Compose, server applicativo distinto dal DGX; parser CPU con modelli locali preinstallati, rete isolata senza egress.
**Project Type**: web monorepo brownfield, non nuovo prodotto.
**Performance Goals**: SC-001–SC-012: 10 min attivi PM, 180 s per il profilo nominale ordinario; regime esteso automatico fino a 3600 s dal comando, con cambio controllato. Preparazione documentale separata fino a 3600 s. Due prove oltre 2× budget, mai aumento della finestra per farle passare.
**Constraints**: uno slot inferenza globale; tokenizer e finestra effettivi per modello/fase, output/reasoning e margine riservati; originali non sostituiti; niente OCR, KB condivisa, RAG, stime AI o fallback cloud (lo stadio Gap su ledger completo v6, AI §13.7, non è RAG).
**Scale/Scope**: allegati del solo progetto; limiti operativi iniziali configurabili in §Configurazione, non capacità illimitata promessa.

## Constitution Check

Gate pre-ricerca e riesame post-design: conforme nel disegno, nessuna deroga. Conformità runtime ancora da provare.

| Principio | Verifica del disegno |
| --- | --- |
| I KISS/YAGNI | Un modulo Documents, un parser stateless già previsto da D07, stessi DB/dispatcher/ledger; nessun nuovo engine o indice |
| II MAF | WorkflowBuilder, executor nativi, AIAgent, checkpoint e resume; raccordo per sessione isolata/cache in AI §5, niente coordinatore agentico parallelo |
| III Native prima di custom | API 1.26.0 riscontrate nei pacchetti locali e test esistenti; nuovo ciclo da provare. Nessuna assunzione di reset/resume atomico LLM+DB |
| IV Dominio | Originali, evidenze, porzioni, manifesti e revisioni indipendenti dalle sessioni MAF |
| V LLM interni | Parser offline, richieste AI solo al runtime autorizzato, nessun modello Qwen obbligatorio |
| VI Slice completa | Upload → chat documentata → analisi → edit/conferma → Markdown, anche oltre contesto |
| VII Autorizzazione | Nessun fetch/tool/pubblicazione; conferma e generazione restano comandi distinti |
| VIII Integrazioni reali | Matrice e gate in quickstart; nessun PASS dedotto da fake o lettura codice |
| IX ADR proporzionati | Confine parser stateless approvato D3, formalizzato in [ADR-0001](../../docs/adr/0001-parser-docling-stateless.md); nessun altro ADR introdotto |

Un limite di capacità che fa fallire i nominali oltre contesto blocca l'accettazione: non è una deroga costituzionale né un completamento della slice. Se le prove richiedono nuove infrastrutture o cambi di scope si riesamina il piano prima di procedere.

## Project Structure

### Documentation (this feature)

`spec.md`, `checklists/requirements.md`, `plan.md`, `research.md`, `data-model.md`, `contracts/http-api.md`, `contracts/ai-workflow.md`, `contracts/parser.md`, `quickstart.md`. Nessun tasks.md generato.

### Source Code (repository root)

Destinazioni future, non create da questo piano:

```text
backend/src/bProjects.App/
  Documents/                  nuovo: upload, storage, selezioni, parsing adapter e manifesti
  Evidence/                   estensione del ledger, non un secondo registro
  AI/                         evoluzione agenti, piani bounded, budget e checkpoint
  Analysis/                   version dispatch 1.0/1.1, revisioni e generazione esistenti
  Persistence/                mapping e migrazioni esplicite
backend/tests/bProjects.IntegrationTests/  test nuovi e regressioni esistenti
parser/                       servizio Python Docling, lock e test parser
frontend/src/app/projects/    vista documenti e aggiornamento fonti/Evidence/monitor
infra/                       Compose parser, storage privato, limiti proxy
tests/e2e/                   percorsi PM reali e offline separati
```

## Riuso verificato nel codice

Tutti i percorsi qui sotto sono relativi a `backend/src/bProjects.App/`, salvo indicazione.

| Esistente letto | Riuso e delta necessario |
| --- | --- |
| `Persistence/AppDbContext.cs`, `Identity/`, `Projects/`, `Conversations/` | Stessi accessi PM, FK di progetto, sequenza e lock conversazione; aggiungere lock di selezione progetto condiviso con upload/ready/analisi/chat |
| `Evidence/EvidenceEntities.cs`, `EvidenceExtraction.cs`, `EvidenceLedgerStore.cs` | Stessi EvidenceItem/Relation/Version, validazione span e commit fenced. Oggi MessageId è obbligatorio: introdurre sorgente discriminata e owner documentale, nessun messaggio fittizio |
| `Evidence/EvidenceViews.cs`, `EvidenceCoverage.cs`, `EvidenceEndpoints.cs` | Diff/promozione, mappe e report riusati; matching documentale solo all'interno dello stesso originale/parse, mai per sole parole o fra versioni di file diverse |
| `AI/EvidenceExtractor.cs` | MAF Extraction e validatore; estrazione oggi singolo messaggio, contesto precedente eliminabile con diagnostica: non adatto direttamente a una copertura documentale completa |
| `AI/AiDispatchService.cs`, `Persistence/ExecutionLeaseStore.cs`, `AI/ExecutionOptions.cs` | Stesso advisory lock, heartbeat, fencing, sweep e retry; `MaxConcurrentRuns=1` reale. Aggiungere lavori documentali e rendimento fra unità; niente secondo scheduler AI |
| `AI/PostgresCheckpointStore.cs` | Store oggi vincolato a ProcessingRun; estenderlo con owner documentale/turno discriminato, stessi payload opachi nativi, ordinamento e fencing |
| `AI/AnalysisConsolidation.cs`, `AnalysisWorkflow.cs` | Riusare grafo e validazione ma oltre il limite usare pacchetti di elementi persistiti, non intera analisi accumulata a ogni passo |
| `Analysis/AnalysisContent.cs`, `AnalysisContentValidator.cs` | JSON stretto, converter const 1.0.0, fonti Message/PmRevision: dispatch esplicito fra 1.0.0 e 1.1.0, non aggiungere campi sotto il vecchio schema |
| `AI/DocumentAgent.cs`, `DocumentWorkflow.cs` | Oggi intero snapshot in una chiamata; nuovo grafo documentale per unità editoriali confermate, assemblaggio finale senza nuova sintesi |
| `AI/ConversationalAgent.cs`, `PostgresChatHistoryProvider.cs` | Conservare FIFO e streaming; ramo documentale multi-pass con citazioni validate e invalidazione della sessione su cambio selezione |
| `AI/ContextBudget.cs`, `AgentFactory.cs`, `ModelRuntimeMonitor.cs`, `InferenceCalls.cs` | Stessi guard, metriche e opzioni per fase. Oggi probe/tokenizer globale anche con override modello: estendere chiave runtime per modello prima di usare tokenizer diversi |

## Disegno e componenti nuovi necessari

### Acquisizione, storage e parser

`Documents` gestisce stream limitato, hash SHA-256, riconoscimento formato, quota sotto lock e upload idempotente. Storage con chiavi generate dal server fuori webroot, permessi minimi, nessun path client, niente URL pubbliche. Scrittura temporanea e flush/rename atomico sullo stesso volume prima del commit di versione+job; risposta di acquisizione solo dopo entrambi. Il DB non è transazionale col filesystem: UploadReceipt e riconciliazione dei residui rendono ripetibile il commit. Mai eliminare file referenziati o versioni storiche; sweep soltanto temporanei/orfani verificati con receipt scaduta.

Un servizio parser Python interno (già previsto dal piano MVP) espone solo conversione sincrona di byte e health/version; non ha DB, coda, LLM, volume originali o stato autorevole. Il backend prende il lease di parsing, trasmette il file e valida il manifest. Un subprocess isolato per conversione consente kill per timeout/memoria. Processo non root, filesystem root read-only, tmpfs limitato, CPU/RAM/pids limitati, nessuna porta host, nessun egress; modelli scaricati e licenze verificati in build autorizzata, mai a richiesta. Il parser usa Docling per PDF/DOCX e MD, percorso testuale lossless per TXT; per MD mantenere anche sorgente e range di righe per code fences/link/tabelle. HybridChunker prepara gruppi strutturali, ma il contatore del runtime .NET decide l'effettiva dimensione delle richieste AI. Vedi contratto parser per dettagli/versioni.

### Lifecycle e concorrenza

Upload ricevuto → acquisito/Queued parsing → Running → parsed → estrazioni di tutte le porzioni → Ready oppure Incomplete/Failed. Parsed non equivale a Ready. Ogni generazione di preparazione è immutabile; retry riusa risultati completi compatibili e crea nuova generazione quando cambia parser/configurazione, senza mutare quelle selezionate storicamente. Prima generazione completa di ciascuna versione file diventa corrente; rielaborazioni della medesima versione sono candidate anche se Ready e richiedono promozione esplicita. LatestReady considera solo versioni file con preparazione corrente Ready, mai una candidata non promossa (data-model §Lifecycle F6).

Parser usa un piccolo BackgroundService CPU nel monolite con claim SQL e lease della stessa semantica; non occupa slot DGX durante parsing. AI documentale entra invece nel dispatcher globale come unità bounded di estrazione: dopo ciascuna unità rilascia lo slot, chat/run eleggibili precedono il prossimo claim. Nessun monopolio dell'intero documento; priorità non significa preemption della chiamata in corso. Tempi e code restano visibili; starvation termina alla deadline persistente.

### Fonti e compatibilità

Versioni file distinte da versioni di parsing e da versioni di estrazione Evidence. `ProjectSourceSelection` contiene modalità LatestReady oppure Pinned(versionId), incluso/escluso e revisione concorrente. Durante preparazione nuova versione il default resta l'ultima Ready; una selezione esplicita della nuova versione incompleta blocca Analizza. Nuovo documento incluso ma non pronto blocca Analizza. Upload e transizione Ready acquisiscono lo stesso lock selezione dei comandi, con ordine progetto → conversazione → job.

Al POST chat/Analizza congelare manifest documentale e versioni Evidence documentali già pronte, escluse comprese. Evidence PM ancora pendenti mantengono il gate della 001 e il loro congelamento al primo claim entro cutoff: non includere mai documenti diventati pronti dopo la richiesta. Analisi con soli documenti ammette cutoff zero nel nuovo schema. La generazione eredita manifest e citazioni dalla revisione confermata, mai dalla selezione attuale.

Evoluzione `AnalysisContent 1.1.0`: DocumentPassage fra le fonti, Documented fra classificazioni, campi documentali condizionali e cutoff ≥0. DTO/validator separati/version-dispatch; 1.0.0 rimane identico in storage e letture, edit di vecchie revisioni rimane 1.0.0. Nuove analisi con documenti sono 1.1.0; senza documenti restano sul ramo 001. Vecchi checkpoint ripresi solo con la propria topologia/config/versione; nessun resume su grafi nuovi.

**Nota T125 (2026-11-03)**: lo scambio con il modello per l'analisi documentale non prevede più che questi restituisca `sources`, `evidenceMap`, UUID, hash o `visitedEntryIds` (formato v2, storico/frozen per i run già richiesti): il modello cita evidenze con alias brevi (E/S/R) e chiavi locali, il server risolve contro il catalogo dello snapshot congelato, genera id canonici stabili e compone lo stesso AnalysisContent 1.1.0 canonico ([AI §7](contracts/ai-workflow.md)). Definition `project-analysis-documents-v3` per le nuove richieste; schema, validatori, patch e completezza invariati. La readiness bounded del runtime (T126) è un raccordo del preflight T124 dentro la deadline originale, non una nuova policy.

### Documenti oltre contesto

Contratto normativo in [ai-workflow](contracts/ai-workflow.md). Tre livelli di copertura distinti:
1. Inventario parser: ogni blocco/pagina/elemento informativo è trattato o ha issue. L'assenza di testo non prova l'assenza di informazione in un'immagine.
2. Estrazione: ogni range informativo è assegnato a una unità e ha estrazione valida oppure zero evidenze motivato. Nessun range perso in splitting/overlap o tabelle.
3. Risultati: ogni evidenza selezionata ha elemento collegato o errore esplicito; ogni elemento confermato ha frammento Markdown. Presenza di ID non prova equivalenza semantica: rischi e revisione umana separati.

Consolidamento gerarchico con due viste globali complementari e riconciliazione trasversale dei collegamenti, non confronto obbligatorio di ogni coppia. Tutte le evidenze partecipano alle passate, mantenendo enunciati/qualificatori/fonti; cataloghi di correlazione non sostituiscono gli originali nei confronti semantici. AI §4.1–4.5 determina schemi, directory paginata delle chiavi, riduzione strettamente decrescente dei livelli, riapertura delle evidenze e composizione simultanea delle patch. AI §5 determina topologia MAF fissa con feedback, sessione isolata per batch, lookup prima della chiamata e replay idempotente. Non si promette identificazione semantica assoluta dei conflitti.

Generare unità editoriali dai soli elementi integrali della revisione confermata; includere i relativi conflitti, qualificatori e fonti. Ogni item va in una unità principale, con riferimenti di contesto ove necessario. Il modello restituisce frammenti con item IDs coperti; assemblare Markdown in ordine stabile, validare copertura/citazioni e pubblicare una sola bozza solo alla fine. Niente summarizer sulla revisione confermata né passata finale che rischia di perdere pezzi. Singolo elemento/relazione irriducibile oltre budget → errore esplicito, nominale non superato.

Chat documentale: scansione di tutte le unità/evidenze ammesse rispetto alla domanda, note di risposta bounded con citazioni; riduzione multipasso delle sole risposte pertinenti con registro di copertura di tutte le unità visitate. Non modifica il registro dei requisiti o revisioni formali. Non è retrieval: nessun indice/query ranking/top-k, tutte le unità selezionate visitate. Vecchi assistant con manifest diverso esclusi dall'input operativo; originali PM restano ma riferimenti a fonti ora escluse non vengono promossi a nuove evidenze documentali. Sessione ricostruita su hash selezione/config/cutoff.

### Configurazione e budget

Valori iniziali operativi, modificabili dall'Admin/configurazione server, non nuovi requisiti funzionali di prodotto:

| Impostazione proposta | Iniziale | Comportamento |
| --- | --- | --- |
| Documents:MaxFileBytes | 50000000 | 50 MB decimali, streaming; proxy limite multipart leggermente maggiore, backend decide |
| Documents:ProjectQuotaBytes | 1000000000 | Tutte le versioni e upload riservati; quota derivati separata di pari valore |
| Documents:MaxExpandedBytes | 250000000 | DOCX/zip, contro decompression bomb; nessun file esterno |
| Documents:MaxPages / MaxBlocks | 1000 / 100000 | Eccesso → incomplete/limit esplicito, mai taglio |
| Documents:ParseTimeoutSeconds | 300 | subprocess terminato, parsing Failed |
| Documents:PreparationDeadlineSeconds | 3600 | Dalla richiesta, coda inclusa; unità estrazione con max 3 tentativi |
| Documents:MaxAiCallsPerPreparation | 512 | Limite di sicurezza persistito, non limite silenzioso di contenuto |
| Documents:MaxAiCallsPerRun | 512 | Include scansioni/riconciliazioni e retry, sempre entro deadline del comando |
| Documents:MaxDerivedBytes | 250000000 per versione | Manifest/parsing/range/output intermedi; esaurimento esplicito |
| Documents:UploadTimeoutSeconds | 300 | Ricezione senza falso acquisito |
| Parser CPU / RAM / parallelismo | 2 / 4 GiB / 1 | App server, non GPU DGX |
| Documents:OrdinaryDeadlineSeconds / ExtendedDeadlineSeconds | 180 / 3600 | Regime automatico; cambio una sola volta prima della scadenza, dal t0 originario |
| Documents:MaxReconciliationLevels / MaxCandidatesPerRun | 32 / 100000 | Limiti espliciti, oltre → coverage_incomplete; inclusi nel policy hash |
| Lease / rinnovo / max tentativi | 30 s / 10 s / 3 | Riuso semantica esistente; scadenza padre prevale su tentativo e rinnovo |

Nessun ctx-size fisso. Per modello/fase verificare disponibilità, capacità per slot e tokenizer; `effectiveWindow=min(cap configurato, finestra misurata)` se verificata, altrimenti errore prima delle nuove chiamate documentali. Il ramo storico conserva la propria politica. Budget input = finestra effettiva − output massimo totale (ragionamento incluso) − margine. Non sottrarre reasoning due volte; se il provider non dimostra che sia incluso, rifiutare thinking per quella fase o dichiarare configurazione non verificabile. Conteggio payload completo con template/schema/tool e framing, non somma ingenua di testi; se il runtime non espone template applicato, usare margine calibrato e gate di confronto con usage reale, mai dichiarare esattezza non misurata. Tokenizer locale preinstallato compatibile per HybridChunker, guard finale sempre sul runtime destinatario.

Snapshot di finestra/tokenizer/opzioni, policy e topologia salvati prima delle chiamate; batch dipendenti dagli output sono derivati successivamente e salvati immutabili con hash del genitore (AI §5). Runtime cambiato al resume: ricontrollare, riprendere solo se compatibile; nessun ricalcolo silenzioso della topologia con input differente. Modello non disponibile o finestra non verificabile: errore esplicito, nessun fallback. Config per Chat/Analysis/Document/Compaction/Extraction riusata; niente registry multi-provider nuovo. Metriche nuove composizioni `document_sources`, owner job documentale, singole chiamate/tentativi e aggregati effettivi, con misure/stime/unavailable distinti.

## Migrazioni e piano di verifica

### Delta T130/N4 — A + C minima

- Riusare `DocumentAnalysisAliasPlan`, `DocumentBatchPort`/`IGuidedRetryPlan`, `ContextBudget`, workflow/router e `Documents/SourceSnapshotStore`. Unico retry single-call con prima proposta integrale non fidata, motivi tipizzati e output autonomo; misura dell'input effettivo prima di marker/riserva/invio, recheck deadline/fencing dopo misura. Nessun merge, conservazione automatica, chiamata di revisione aggiuntiva o secondo retry.
- Valutazione Gap strict AI §13.2 nei JSONB WorkOutput esistenti e proiezione coverage-3: nessuna tabella/migrazione nuova, modifica a AnalysisContent 1.1.0, ledger, messaggi o estrattore. EvidenceMap rimane supporto; valutazione e motivo consultabili sono rendicontazione distinta, mai conferma PM.
- (Storico: gate eseguito il 2026-11-07, **non superato**; vedi la decisione C + D qui sotto.) Gate anticipato T138, prima della implementazione completa del raccordo gerarchico v5: prova offline mirata sul percorso reale degli attuali batch leaf/reduce/Cross con SDK MAF 1.26.0 e PostgreSQL reali, client modello simulato e output controllati. Collocare Gap e riscontro in porzioni diverse e verificare nei payload effettivi che il batch valutatore riceva simultaneamente entrambi gli originali integrali con provenienze risolvibili, WorkOutput/checkpoint persistiti e ripresa coerente; registrare percorso, batch, input/hash e numero/tipi di chiamate in `validation/T130-N4-revision.md`. Due riferimenti nei DTO non bastano. Se il grafo esistente non garantisce il nominale senza nuove chiamate o fasi, fermarsi prima della implementazione completa e segnalare il limite per decisione architetturale, senza introdurre autonomamente stage o chiamate LLM. SC-004, limiti e gold restano invariati.
- **Esito gate T138 e decisione C + D (2026-11-07)**: il gate non passa ([validation/T130-N4-revision.md](validation/T130-N4-revision.md)). Soluzione **temporanea** approvata ([AI §13.6](contracts/ai-workflow.md#136-decisione-c--d-percorso-temporaneo-e-routing-2026-11-07)): `GapAssessment` v5 solo nel percorso single-call (coverage-3, anche con lista vuota senza Gap). Routing v5: (a) payload che entra in una chiamata ⇒ single-call v5 (alias-2, prompt v4, `gapAssessments`, retry T137); (b1) oltre contesto con ≥1 evidenza Gap nello snapshot congelato ⇒ **fail-fast con zero invocazioni LLM**, dopo snapshot/catalogo/misura e prima di piano, port e riserva di chiamate: run `Failed`, codice `document_limit_exceeded`, motivo tipizzato `hierarchical_gap_assessment_unsupported`, nessuna revisione né anteprima incompleta, nessun WorkOutput; (b2) oltre contesto senza Gap ⇒ gerarchia v4 invariata (`document-reconcile-v3`, `reconcile-alias-1`, `global-reconcile-v1`, WorkOutput v4, coverage-2); (c) v2/v3/v4 storici invariati. Scelta a runtime da snapshot congelato + misura; il freeze (T141) fissa in anticipo tutte le versioni, incluse le chiavi gerarchiche v4 e `reconcileGapPolicy` = `hierarchical-gap-incomplete-1` (nessun `plan["gapPolicy"]` né modifica del `configHash` del piano). Realizzazione in T143 (file reali: `DocumentAnalysisWorkflow.cs`) e T141. **Rischi residui**: (1) finché T138 non è risolto i run oltre contesto con Gap falliscono subito; (2) **soglia single-call**: prompt `document-analysis-v4` e schema `alias-2` aumentano la misura del payload e riducono la finestra single-call (rischio approvato dal committente); documenti di confine con Gap che in v4 entravano ora cadono nel fail-fast, senza Gap vanno nella gerarchia v4-equivalente (più chiamate e durata, prestazioni non garantite uguali alla v4); nessuna approssimazione, troncamento o fallback silenzioso a v4; test di confine in T141/T143.
- **Adattamento I5 nominale (futuro, T138 bloccato, non attivo; richiede nuova decisione A/B)**: la completezza globale non deve continuare a obbligare una domanda riscontrata a diventare item. Estendere DTO/metadati dei nodi/assembly nei batch leaf/reduce/Cross esistenti, senza fase LLM nuova, secondo AI §13.4. Riscontri finali solo con originali disponibili, propagazione senza reinterpretazione e sostituzione esplicita per discendenza. Divergenze non riconciliate ⇒ frontiera/incompleto, nessun arbitrato server-side. Con C + D non è prerequisito di v5; i requisiti oltre contesto (FR-013, SC-004, T082/T083/T090) restano aperti e non ridotti; nessun fallback semantico a v4.
- **Decisione D1 — stadio Gap su ledger completo, definizione v6 (approvata 2026-11-09, supera C + D solo per le definizioni ≥v6; v5 invariato con fail-fast e `reconcileGapPolicy`)**: per i nuovi run `project-analysis-documents-v6` oltre contesto/output con ≥1 Gap, dopo la gerarchia v4-equivalente **una** chiamata dedicata (prompt `document-gap-v1`, schema `gap-stage-alias-1`) valuta ogni Gap contro l’**intero** ledger congelato. **Eccezione esplicita: lo stadio Gap su ledger completo non è RAG né indice di ricerca** (nessun top-k, ranking, indice, embedding, partizionamento, arbitrato; sono vietati in 002). Preflight di input e output con fail-fast tipizzato a zero invii (`gap_ledger_exceeds_input`, `gap_assessment_exceeds_output`; `ErrorCode` `document_limit_exceeded`), nessuna valutazione parziale. Nuovo valore `Stage` nel piano (non un nuovo executor), nessuna tabella/migrazione, riuso di `GapAssessmentRules`, coverage-3 estesa per algoritmo con metadati derivati (`searchBasis`, `absenceProven`, guardia non bloccante). Contratto di ingresso modulare: candidati + `searchBasis` + `retrievalComplete` (sorgente = ledger intero in 002; la 003 potrà sostituire solo la sorgente). Realizzazione T144–T150 (T150 = gate DGX «SC-004-Gap», da autorizzare a parte). **Limite residuo**: ledger oltre il budget di una chiamata = perimetro non coperto, non soddisfatto né trasferito automaticamente alla 003. La misura offline autorizza l’implementazione ma non è validazione funzionale: T138, FR-013 e SC-004 restano aperti. Contratto: AI §13.7. Il «nessuna nuova fase/chiamata» di C + D resta vero per i v5.
- Versioni normative AI §13.1 (con C + D: gerarchia v5 temporanea sulle versioni v4 della gerarchia più `reconcileGapPolicy`; `reconcile-v4`/`reconcile-alias-2`/`global-reconcile-v2` sono contratto futuro non attivo): v5; prompt analysis-v4/reconcile-v4; DTO alias-2/reconcile-alias-2; nuovi algoritmi/validazioni, cataloghi di forma invariata legati ai nuovi contratti; gap-assessment-1; coverage-3. Conservare canon-ids-1, run limits snapshot 1 e decoder storici. Config/manifest/lookup/recompose verificano hash nuovi; retryPayloadHash distinto dall'inputHash originario. Nessun backfill.
- Pianificare DET/MAF-PG su codec/coerenze, falso conflitto, Gap, riferimenti, payload al limite/oltre limite, crash/recovery/fencing/hash, legacy v2/v3/v4 e T129/T134; UI coverage tastiera/mobile. Poi DGX/browser soltanto su mandato successivo: tre N4 seriali conformi, avversi e N1/N2/N3/N5; oltre contesto e 180 s invariati. Task T135–T143 in tasks.md, unica baseline operativa (T143 aggiunto dalla decisione C + D; T139/T141 dipendono da T143, non da T138). T067 rimane aperto. T142 verifica specificamente retry informato, valutazioni Gap, casi avversi e stabilità v5 con almeno tre N4 seriali conformi; T067 mantiene la verifica finale integrata I4 (N1–N5, browser reale, provenienze, conferma PM e bozza). Riutilizzare evidenze verificabili comuni con riferimenti puntuali a run/output/hash/configurazione ed esiti quando soddisfano integralmente entrambi i task, senza duplicare inferenze DGX né ridurre criteri, numero di prove N4 o livello di verifica. T142 resta obbligatorio; T082/T083 restano gate I5 separati e non condizionano la chiusura I4.

**Constitution Check post-design**: I/IX riuso senza nuovo servizio/ADR; II/III nessuna nuova API MAF assunta, verifiche eseguibili nella versione fissata durante implementazione; IV originali/ledger/revisioni immutati; V solo provider interno; VI raccordo I4/I5 e gate espliciti; VII nessuna operazione esterna; VIII prove reali pianificate, non eseguite. Nessuna deroga. Rischi residui: ancoraggio, riscontro semanticamente errato pur con riferimenti validi, input maggiore, limiti fra porzioni; nessuno è risolto dal solo controllo statico.

Espansione dello schema, backfill discriminanti Message senza riscrivere JSON storici, vincoli XOR/FK di progetto e nuovi owner checkpoint/telemetria. Nessuna migrazione automatica all'avvio. Cutover con app ferma, backup DB+volume coerente e prova upgrade da dati 001; rollback applicativo alla 001 consentito solo prima di creare dati 1.1/documentali, altrimenti restore coordinato o versione compatibile (vedi data-model).

Matrice requisiti: FR-001–006 → storage/parser/golden e prove limiti; FR-007–010/015 → ledger/fonti/coverage; FR-008/017–019 → concorrenza/versioni/snapshot; FR-011–014 → grafi bounded e percorsi oltre contesto; FR-016/020/021 → lease/retry/metriche/security; FR-022 → UI/E2E. [Quickstart](quickstart.md) definisce comandi, prove e gate reali senza attribuire esiti prima di implementarli.

## Phase 0 — Research

Decisioni, alternative, fonti ufficiali e limiti della verifica statica completati in [research.md](research.md). Non sono stati usati agenti di ricerca separati: nessuna capacità di delega disponibile nella sessione; letture locali e ricerca ufficiale dirette.

## Phase 1 — Design e contratti

Completati [data-model](data-model.md), [HTTP](contracts/http-api.md), [AI](contracts/ai-workflow.md), [parser](contracts/parser.md), [quickstart](quickstart.md). Gate costituzionale riesaminato dopo il disegno, nessuna violazione progettuale. Le primitive base MAF sono presenti nel codice .NET 1.26.0 letto; composizione del feedback/routing e nuovi owner necessitano prove eseguibili sulla release fissata prima dei nuovi percorsi.

## Rischi e stato delle decisioni

Nessuna decisione funzionale/architetturale irrisolta impedisce di derivare i task. Restano gate tecnici espliciti: build offline Docling e licenze dei modelli, fedeltà parser/locator (soprattutto PDF misti e DOCX), adattamento HybridChunker al tokenizer reale, migrazioni con FK e compatibilità JSON, resume con nuovi owner, costi e fedeltà della riconciliazione globale gerarchica e latenza di chat/analisi sotto uno slot DGX. Nessun PASS attuale su questi gate.

Il profilo ordinario resta soggetto a 180 s; il regime esteso è assegnato automaticamente e limitato a 60 minuti, coda e retry inclusi. Superamenti o incompletezze restano FAIL del profilo pertinente; nessun aumento della finestra o riduzione delle fonti per far passare le prove. I 18 controlli precedenti non vengono attestati qui: checklist indipendente ancora da svolgere.


## Decisioni consolidate e raccordo F1–F8

D1: regime automatico; PM impartisce un comando e vede eventuale durata maggiore asincrona, non seleziona strategie. D2: riconciliazione gerarchica globale, nessun all-pairs obbligatorio, Evidence comune e copertura strutturale separata da fedeltà. D3: parser isolato stateless, backend unica autorità durevole, [ADR-0001](../../docs/adr/0001-parser-docling-stateless.md).

F1/F7 → §Regimi qui sotto; F2 → AI §5 e Data model manifesti; F3 → Data model §Commit aggregato; F4/F5 → AI §1–3 e HTTP payload; F6 → Data model §Lifecycle e HTTP corrente/candidata; F8 → ADR. Si dettaglia il disegno esistente, senza nuova piattaforma né rivalidazione automatica della checklist.

## Regimi, deadline e profili operativi (F1/F7)

### Autorità temporale e terminazione

Tutti i tempi autorevoli sono timestamp PostgreSQL. t0 = accettazione del comando o commit acquisizione originale per la preparazione (upload ha timeout separato 300 s dalla ricezione). Coda, preflight, elaborazione, retry e recupero sono inclusi nella durata del rispettivo root; attesa umana fra conferma e generazione non appartiene ad alcun run attivo.

Ogni root documentale (Preparation, ChatTurn, ProcessingRun) ha RequestedAt=t0, Regime, InitialRegime, ClassificationVersion/Reason, PolicyHash, OrdinaryDeadlineAt=t0+180 s per i comandi, HardDeadlineAt=t0+ExtendedDeadlineSeconds (default approvato 3600 s), EffectiveDeadlineAt e PromotionCount 0/1. Preparazione nasce Extended con hard/effective=t0+PreparationDeadlineSeconds, default 3600 s. Config estesa validata ≥180 s e finita; cap congelato all'avvio, non allungato da modifiche Admin durante il lavoro. Nessuna stima di un'ora mostrata come durata attesa.

- Ordinary ha EffectiveDeadlineAt=OrdinaryDeadlineAt; Extended ha EffectiveDeadlineAt=HardDeadlineAt. Ordinary→Extended consentito una sola volta e solo mentre root è non terminale e now<EffectiveDeadlineAt, mediante transizione atomica tracciata. HardDeadlineAt non cambia; non si calcolano 3600 s dal cambio. Nessun downgrade o riapertura automatica di Failed.
- Ogni figlio ha ParentRootId, DeadlineAt≤HardDeadlineAt e operazioni consentite solo se padre e figlio sono attivi e now<min(deadline figlio, EffectiveDeadlineAt padre). La promozione del padre consente soltanto lavoro ulteriore entro lo stesso hard cap; eventuale limite locale più stretto rimane. Figlio non può promuovere autonomamente o ricreare il root.
- Timeout chiamata AI = min(Ai:Phases:phase:RequestTimeoutSeconds congelato, limite residuo tentativo, EffectiveDeadlineAt−now del root, DeadlineAt−now figlio). Parser: analogamente min(ParseTimeoutSeconds=300, residui). Tentativo bounded non oltre 300 s per operazione di preparazione o deadline effettiva residua per workflow, mai un nuovo budget globale. Rinnovo lease 30 s/heartbeat 10 s non sposta deadline. Trasporto backend del parser scade al timeout subprocess più 5 s di cleanup, comunque entro deadline effettiva; nessun commit dopo la scadenza.
- Retry automatico/reclaim: massimo 3 tentativi sul medesimo job/owner, stessa deadline e configurazione, backoff 2/4 s entro residuo; conteggio persistente e limiti chiamate root prima di ogni invio. I superstep riusciti non sono nuovi tentativi. Parser timeout può ritentare solo se residui e tentativi lo permettono. Schema/quote invalidi, assenza di formato o limite irriducibile sono errori permanenti, non motivi per consumare un'ora.
- Sweep chiude root scaduto e lavori figli aperti, inibisce claim/commit anche prima della pulizia. Se app è offline, il confronto sul timestamp DB impedisce nuove scritture al primo riavvio e rende terminale il root; niente tolleranza che riparta da zero. Cancellazione cooperativa non garantisce stop immediato del calcolo remoto, ma il fence ne impedisce la pubblicazione.
- Retry manuale dopo terminale è un nuovo comando idempotente con nuovo root/Preparation e t0 dichiarato, RetryOfId. Può riusare output completi immutabili solo con stessi input/config/parser/fonti verificati; non altera il lavoro vecchio né gli snapshot storici. Non è un retry automatico illimitato.

### Classificazione automatica riproducibile

Policy normativa `document-regime-v2`, corretta per B1 prima di qualunque release runtime; PolicyHash congela anche questa revisione. **Per B1 questa sezione sostituisce i riferimenti precedenti al timer 120 s e al FAIL N da sola promozione in Research R8/R10 e nel raccordo temporale del Data model.** Documents:PromotionAfterSeconds è rimosso dalla configurazione; PromotionDueAt, dove già previsto nel disegno, è sempre null e non governa classificazione o transizioni. La prima classificazione non è più subordinata a PromotionDueAt. Identità/schema/topologia B2/B3 invariati; record con policy hash differente non sono reinterpretati al resume.

Al POST si persistono t0, snapshot e ricevuta; preflight tecnico senza LLM applica la tabella prima del primo claim AI. Se non finisce nel POST, ClassificationStatus=Pending, Regime provvisorio Extended e reason preflight_pending; InitialRegime non è ancora definitivo e nessuna inferenza è ammessa. La prima classificazione dipende solo dagli input tecnici sotto elencati. Nella medesima transazione fissa InitialRegime, Regime e relativa EffectiveDeadlineAt dal t0 originale; se la deadline così determinata è già raggiunta, termina Failed deadline_exceeded senza invio. Esempio: preflight concluso a 130 s con piano singola chiamata valido → Ordinary con 50 s residui; a 190 s → classificazione Ordinary e fallimento, mai Extended per sola attesa. Un piano strutturalmente esteso può essere ammesso oltre 180 s solo prima del proprio hard cap. Dopo Classified nessun downgrade. I comandi senza documenti seguono la 001.

Input congelati: tipo comando, disponibilità/fonti/cutoff o revisione confermata, WorkManifest, versione planner/schema/prompt/policy, impronta modello/tokenizer/template, W=finestra effettiva verificata, O=massimo output totale, M=margine calibrato, B=W−O−M, T=conteggio del payload completo, qualità del conteggio (`measured|calibrated_upper_bound|unavailable`), piano di chiamate iniziali e dipendenze non ancora note. L'upper bound deve avere calibrazione identificata per quel template/runtime; una mera stima non certificata equivale a unavailable per l'ammissione Ordinary. Slot occupato, carico DGX e durata osservata/prevista non sono predicati del classificatore; la disponibilità tecnica del runtime resta necessaria per inviare.

Output persistito `ClassificationRecord`: version, inputHash, policyHash, observedAt (DB), outcome `AdmitOrdinary|AdmitExtended|Reject|Fail`, reason (prima regola soddisfatta), valori W/O/M/B/T con provenance, shapeFloorTokens nullable, plannedCalls nullable, eventuali unknown. Record append-only; snapshot delle misure aggiornato al preflight/resume, mai mutato retroattivamente.

La tabella assegna il regime solo nella prima classificazione: su root già Classified si rilegge il record originario e si applicano i guard correnti di vita/deadline, senza una nuova ammissione o modifica di InitialRegime. I preflight successivi verificano compatibilità e invio entro budget; un cambio di regime segue esclusivamente la ricevuta e la tabella di promozione sotto definite. Misure aggiornate non sostituiscono il record iniziale.

Valutazione in quest'ordine, prima corrispondenza vince:

| Ordine | Condizione | Esito/reason |
| --- | --- | --- |
| 1 | Input/autorizzazione/fonti non validi, quote violate | Reject con errore specifico; readiness chat segue HTTP, nessun ampliamento fonti |
| 2 | Root terminale o now ≥ HardDeadlineAt; oppure root già Classified e now ≥ EffectiveDeadlineAt | Leggere terminale oppure Fail deadline_exceeded; nessuna promozione tardiva |
| 3 | Modello/finestra/tokenizer/template non identificabili, O≤0, M<0 o B≤0 | Fail runtime_unverified/config_invalid prima di inferire |
| 4 | Preparazione documentale | AdmitExtended preparation; deadline separata |
| 5 | Singolo input obbligatorio irriducibile non inviabile o limite certo di chiamate/derivati superato | Fail document_limit_exceeded/coverage_incomplete, non attesa estesa inutile |
| 6 | T unavailable, conteggio solo stimato, numero/dipendenze delle chiamate o output shape non determinabili dagli input tecnici | AdmitExtended workload_unknown con elenco dei campi mancanti; preflight di ogni invio obbligatorio, unknown non autorizza invio fuori budget |
| 7 | Piano iniziale contiene esattamente una chiamata, nessuna dipendenza da altri output/compattazione/riduzione, T≤B e shapeFloorTokens≤V | AdmitOrdinary single_call_fit; controllo atomico della deadline Ordinary derivata da t0 |
| 8 | Tutti gli altri input validi | AdmitExtended multipass_or_capacity con piano/hash e causa verificabile: più chiamate, dipendenze, T>B o floor>V; il planner esteso deve ancora dimostrare batch validi |

`shapeFloorTokens` è il conteggio con lo stesso tokenizer dello skeleton strutturale canonico costruito dal server, inclusi ID/fonti obbligatori e cardinalità minime dello schema del batch. Skeleton versionato per fase/schema: campi nell'ordine dello schema, ID obbligatori reali ordinati, minItems richiesto, stringhe segnaposto `x` ripetuta minLength (vuote solo se ammesse), numeri al minimo ammesso; hash dello skeleton nel ClassificationRecord. Schema senza skeleton determinabile → unknown. È una soglia strutturale prudenziale riproducibile, non il minimo matematico dei token di ogni possibile JSON né la previsione della risposta. V è la riserva visibile: O se reasoning disabilitato, O−R se esiste un tetto reasoning R verificato e congelato; altrimenti V e shapeFloorTokens utilizzabile sono unknown → regola 6. Uno skeleton maggiore di V esclude Ordinary, ma il planner esteso deve ancora dimostrare batch validi; impossibilità di suddivisione → regola 5. Uguaglianza T=B o floor=V ammessa; un token oltre esclude Ordinary. Nessuna stima di token o tempo garantisce output valido o successo.

### Promozione tecnica e concorrenza

Unico motivo di promozione: `additional_work`, dimostrato da una ricevuta server immutabile `{reason,sourceHash,triggerOutputHashes,remainingObligationIds,additionalBatchIds,derivedPlanHash}`. triggerOutputHashes riferisce almeno un WorkOutput Committed valido del root. remainingObligationIds è un insieme non vuoto di obbligazioni richieste di AI §4–6 ancora non soddisfatte; additionalBatchIds contiene almeno un batch logico distinto necessario a tali obbligazioni, senza output compatibile già Committed. Ogni batch ha input/genitori/hash e preflight valido secondo il planner congelato. derivedPlanHash usa canonicalizzazione/ordinamento di AI §4.1 e copre ricevuta e manifesti ordinati escludendo il proprio campo derivedPlanHash, senza dipendenza hash circolare. Il predicato è vero soltanto se tutti questi vincoli sono verificati dal server; un booleano AI «serve più tempo» non basta.

Esempi: dopo output valido occorre confrontare una coppia Evidence pendente, espandere un candidato o produrre un'unità editoriale necessaria non ancora coperta. Output già riutilizzabili portano ad Advance/assemblaggio senza promozione per reinferirli. Coda, inferenza lenta, preflight lento, backoff/retry della stessa chiamata, assenza di output valido o solo risultato finale in attesa del commit non soddisfano il predicato. Output troncato/invalido resta errore permanente; limite irriducibile/runtime incompatibile produce errore, non promozione.

Decisione in quest'ordine sotto lock root, con ordine dei lock e guard owner/fence/lease già definiti:

| Priorità | Condizione | Azione |
| --- | --- | --- |
| 1 | Root terminale | Rileggere risultato, nessuna transizione/inferenza |
| 2 | now ≥ EffectiveDeadlineAt, errore permanente o lavoro necessario oltre un limite di chiamate/byte | Failed con errore e copertura residua; nessuna promozione/Completed tardivi. Un risultato già completo entro i limiti non fallisce per il solo contatore esattamente al massimo ammesso |
| 3 | Risultato finale valido, copertura completa e condizioni di commit soddisfatte | Commit risultato+Completed atomico; nessuna promozione per attesa del commit |
| 4 | Ordinary e ricevuta additional_work valida | CAS Ordinary→Extended una sola volta, prima della scadenza corrente |
| 5 | Altrimenti | Conservare regime e deadline; avanzare/attendere secondo B2 e disponibilità reale |

Un Ordinary ancora in coda o con la prima inferenza attiva a 120 s mantiene regime e deadline. Senza motivo tecnico di promozione, a 180 s scade e un risultato successivo non può essere committato. Il solo trascorrere del tempo non attiva alcuna promozione.

La transazione fenced della promozione verifica la ricevuta contro output/obbligazioni correnti e persiste ricevuta/riferimento, RegimeTransition (reason additional_work, old/new deadline, timestamp DB, elapsedMs, policyHash), PromotionCount 0→1 e DeadlineAt=EffectiveDeadlineAt=HardDeadlineAt, insieme ai batch derivati già determinabili. HardDeadlineAt rimane t0+3600 s iniziali configurati. Commit terminale precedente vince; se la promozione valida committa prima, il completamento successivo legge la deadline aggiornata. A scadenza esatta prevale l'errore. Sweep/monitor ogni secondo indipendente dallo slot AI, senza timer di promozione. Gli stessi guard precedono claim/invio/renew/commit/checkpoint; un vecchio claim non è autorità temporale.

Dopo promozione, retry automatico, reclaim e resume mantengono regime, ricevuta, t0 e deadline del medesimo root, senza azzerare contatori o riclassificare InitialRegime. Il nuovo comando PM esplicito dopo terminale resta la distinta richiesta con nuovo root e RetryOfId di AI §5.3: non prosegue il root scaduto e non ne cambia limite o valutazione. Questa distinzione preserva B2 e non autorizza reset automatici.

**Raccordo dispatcher 001**: nel nuovo ramo documentale `DeadlineAt` esistente di ChatTurn/ProcessingRun è la proiezione di EffectiveDeadlineAt, aggiornata **nella stessa transazione** di promozione (CHECK uguaglianza). Claim/renew/release/sweep, store di commit/checkpoint e guard SQL devono leggere tale autorità attuale; il DeadlineAt copiato nel claim non può chiudere il root a 180 s dopo una promozione. Timer del dispatcher root si riarma dal DB; il timer di una chiamata già inviata resta quello assegnato e può scadere/ritentare entro i residui. Figli usano min(DeadlineAt locale, EffectiveDeadlineAt padre), mai un vecchio cap root incorporato nel claim. Il ramo legacy mantiene campi/policy precedenti. Questo è delta da implementare, non capacità già presente in ExecutionLeaseStore.

UI espone stato persistito, coda, motivo tecnico e unità terminate/totali noti; niente percentuali simulate, nuova scelta PM o promessa di un'ora. La promozione preserva fence, contatori, snapshot e output validi. Limiti chiamate/byte, tentativi esauriti, errore permanente o hard deadline terminano esplicitamente con copertura mancante; nessuna anteprima/risposta/bozza parziale dichiarata completa. La valutazione nominale è indipendente dal regime come sotto definito. B2 mantiene commit prima di Advance, doppio lookup e sessione nuova per invio; B3 mantiene obbligazioni, patch e condizioni di arresto senza indebolirle per promuovere.

### Misure e accettazione indipendenti dal regime

Profilo N (riferimento): servizio disponibile, zero altro carico AI, una chiamata prevista, input misurato ≤50% del budget della fase, output massimo riservato, fonti Ready e dati/attese fissati prima della prova. Rilevare separatamente N-chat, N-analysis, N-document; obiettivo 180 s dal comando. Profilo e attese del caso di accettazione sono fissati prima della misura e non ricalcolati da InitialRegime, Regime o promozioni.

Profilo E: input oltre contesto o multipasso/riconciliazione richiesti, inclusi documento singolo >2× budget e almeno tre documenti >2× complessivo, 12 fatti distribuiti e conflitti noti distanti/inter-documento. Completamento entro hard cap 3600 s e criteri di qualità invariati. File massimo 50 MB non implica trattamento in 180 s né promessa che ogni contenuto entro upload sia semanticamente trattabile; incapacità note/localizzate restano esiti espliciti, non nominali riusciti.

Per ogni caso registrare separatamente risultato funzionale e valutazione prestazionale. Risultato funzionale Completed richiede output finale valido, copertura completa e commit autorizzato; altrimenti Failed/Incomplete con motivo e parti mancanti. Il criterio temporale N è soddisfatto solo con risultato completo committato entro 180 s da RequestedAt, coda/preflight/retry inclusi. Esito nominale N = PASS solo se tempo e tutti i criteri funzionali/qualitativi approvati sono soddisfatti; nessuna proprietà di Regime entra nella formula. Un N completo a 150 s è PASS se soddisfa gli altri criteri, anche dopo promozione tecnica; un N promosso e completo a 190 s è successo funzionale con FAIL prestazionale N. Un Ordinary non promosso scade a 180 s; errore/pending o output tardivo non sono PASS. Il dataset non diventa E ex post.

Durata totale = timestamp DB di Completed/Failed (oppure osservazione per pending) − RequestedAt. queueMs somma intervalli persistiti di attesa slot/dipendenze senza elaborazione; processingMs somma intervalli osservati di esecuzione effettiva, inferenza/Docling inclusi, non l'intero Running o il lease. Preflight, backoff e recupero hanno intervalli/cause distinti; tempo non osservabile dopo crash resta unavailable, non tempo attivo inventato. Per il root gli intervalli sono mutuamente esclusivi: attività di un figlio conta come elaborazione, non anche come coda dei fratelli; queueMs si accumula solo senza attività del root/figli e fuori da preflight/backoff/recupero. Usare unione temporale degli intervalli della stessa categoria, senza doppio conteggio o somma di figli sovrapposti; chiamate/tentativi rimangono dettaglio distinto. Total elapsed da t0 resta autorevole anche con dettagli unavailable. A 120 s queueMs=120000 e processingMs=0 non cambia il regime.

Tempi upload/preparazione/chat/analisi/generazione distinti; attesa umana esclusa dai run. Registrare profilo e attese originari, esito funzionale, esito temporale/qualitativo, initial/final regime, ricevuta/motivo tecnico del cambio, timestamp e tempo consumato, cap, unità e fonti/hash, modello e token provenance. UI/metriche non presentano Completed tardivo come rispetto dei 180 s; errore gestito/copertura parziale non superano il nominale. Nessun criterio, FAIL o riserva della 001 è cambiato.
