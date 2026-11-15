# Riconciliazione gerarchica della 002 — assessment architetturale

Stato: **analisi offline**. Nessun codice di produzione, prompt, JSON Schema, configurazione DGX o `tasks.md` ufficiale è stato modificato. Nessuna inferenza generativa è stata eseguita. T040, T027 e T150 restano aperti; questo documento non li supera.

Documenti collegati: [plan.md](plan.md) (intervento) e [tasks.md](tasks.md) (task operativi).

## 0. Sintesi

1. **Il problema non è un singolo bug, ma la struttura del contratto con il modello.** Ogni batch della gerarchia chiede al modello lo stesso involucro `reconcile-alias-1` a sette proprietà. Le regole che rendono valido quell'involucro per ciascuno dei sei stadi sono però distribuite su sei strati: un prompt unico per tutti gli stadi, varianti di schema, DTO, compositore, `CheckResult`, controlli della gerarchia e del motore. Nessun artefatto le confronta tutte. Ogni campagna reale ha scoperto un disallineamento: Run 2 item nelle foglie B, Run 3 `descriptor` nullo, Run 4 espansione senza appartenenza ai gruppi, Run 5 `noFindings`. Ogni correzione è diventata una nuova definizione (v8, v9, v10).
2. **Run 5, chiamata 030: difetto di contratto dimostrato.** `ReconciliationContracts.CheckResult` (`ReconciliationContracts.cs:699`) considera «senza risultati» un batch di confronto che ha prodotto una disposizione `Open` motivata.
   - Codice, prompt v5 (riga 18) e §4.1 del contratto sono coerenti fra loro, e il modello ha disatteso un'istruzione esplicita (F).
   - La regola però conta i *campi*, non gli *esiti*, e la grammatica non la può esprimere.
   - Prova del difetto: i confronti del Run 4 sono passati solo perché il modello aveva aggiunto un candidato ridondante sulla stessa coppia. La risposta semanticamente pulita è stata rifiutata, quella ridondante accettata.
3. **Esisteva un secondo blocco, latente, in 2 dei 4 run che hanno raggiunto le foglie A.** La precondizione dello stadio Gap (ogni Gap ha una domanda canonica in `openQuestions`) dipende da item generati liberamente dal modello nelle foglie A. Il prompt di riconciliazione non la menziona mai. Run 4: la Gap E1 è stata resa con una domanda senza evidenze, quindi `evidence_not_mapped`. Run 5: la Gap E4 è stata classificata come `requirements`, quindi `gap_reference_missing`. Correggere solo la 030 non avrebbe portato il Run 5 allo stadio Gap.
4. **Le foglie A fanno un lavoro che potrebbe essere deterministico, ed è il più costoso.** Nei Run 2–5 le foglie A hanno prodotto 1.128 item per 1.128 voci: 1.127 citano esattamente un'evidenza, nessuna voce è citata due volte, e l'unica eccezione è l'item del Run 4 senza evidenze al posto di E1. Le foglie A valgono l'82,5% dei token di output e l'81% del tempo della riconciliazione del Run 5, e introducono deriva di classificazione: una Gap del PM `Inferred` in 3 run su 4, una Gap finita fra i requisiti.
5. **Le correzioni v8–v10 non proteggono il percorso predefinito della 002.** Senza `Retrieval:Enabled` (default `false`) le nuove analisi congelano la v6 (`SourceSnapshotStore.cs:258`). La v6 usa il prompt `document-reconcile-v3`, lo schema unico e l'espansione storica (`DocumentAnalysisWorkflow.cs:617-631`), cioè il contratto del Run 2.
6. **Fallire una sola volta costa l'intero run.** Ogni violazione è `work_output_invalid` permanente e non c'è alcun retry nel percorso di riconciliazione. L'infrastruttura di retry guidato del port (`IGuidedRetryPlan`/`IInformedRetryPlan`, `DocumentBatchPort.cs:146-179`) esiste ed è già usata da bozza, analisi ed estrazione, ma non dalla riconciliazione. Il lavoro committato di un run fallito non è riutilizzabile da un nuovo run.
7. **Le ~4.257 richieste non generative del Run 5 sono misure del Pack.**
   - Dal codice: ogni misura costa 1 `/apply-template` più 1–2 `/tokenize`; il Pack lineare rimisura l'intero prefisso a ogni voce; anche le **pagine del catalogo**, mai inviate al modello, passano dal Pack.
   - *Ipotesi* da confermare (RH-004): circa 567 misure delle foglie e circa 1.275 delle pagine. Non è ancora riconciliata con la prova isolata, che prima della gerarchia registra 577 `/apply-template` e 865 `/tokenize` (§7).
8. **Raccomandazione: una v11 essenziale della sola 002 (senza recupero), in 5 milestone offline (M0–M4), poi verifiche reali autorizzate a parte (M5), e solo dopo l'attivazione (M6).**
   - Contenuto della v11:
     - tabella unica per stadio;
     - validazione `reconcile-validation-v2` (`noFindings` informativo);
     - confronto senza candidati e con `Open` motivato per grammatica;
     - item delle foglie A composti dal server (tipo, origine e certezza dell'evidenza), con gli estremi dei conflitti `Open` solo come alternative;
     - prompt unico v6 ripulito;
     - memo dei token degli extra (P-1, risultato identico).
   - Retry, istruzioni per stadio e misure P-2/P-3 sono **opzioni differite** con un criterio d'ingresso misurabile ([plan.md](plan.md) §12).
   - Le definizioni v2–v10 restano congelate e rigiocabili.
   - Alternative al §9; decisioni da approvare in [plan.md](plan.md) §11; esiti della revisione indipendente al §11.

## 1. Baseline e fonti

| Voce | Valore |
|---|---|
| Branch | `feature/003-retrieval-evidence-search` |
| HEAD | `c76c6c4fefb5a896544d32be29fbc3c9d59284c7` |
| File non tracciati (preservati) | `benchmarks/`, quattro `docs/roadmap/*-HANDOFF.md` |
| Codice v7–v10 | presente **solo** su questo branch (`git branch --contains d63e7cb`); `feature/002-implementation` (`9d2feb4`) ne è antenato |
| Archivi grezzi delle campagne (locali, non versionati) | `%TEMP%\bprojects-diag\` — Run 1 `20261110-203417-…-4effbbb5`, Run 2 `20261110-214153-…-6e615ff2`, Run 3 `20261111-160309-…-48872147`, Run 4 `20261111-234029-…-b369e4e7`, Run 5 `20261113-093355-…-eb600542`; prova isolata `20261113-181418-…-89ae35fa`; rapporti diagnostici in `%TEMP%\bprojects-t040-campaign\` |

Le cifre per chiamata di questo documento provengono dagli archivi locali: richieste, risposte grezze e `NNN-result.json` letti in sola lettura. Gli eventi HTTP (`events.jsonl`) non contengono i corpi delle richieste `/tokenize`, quindi la scomposizione per misuratore del §7 è un'inferenza aritmetica da confermare (task RH-004). I database di prova dei run sono stati eliminati: gli stati persistiti non sono riesaminabili.

Fonti normative lette: costituzione; `specs/002-project-documents/contracts/ai-workflow.md` §4, §5.3, §11–§13; `specs/002-project-documents/tasks.md`; `specs/003-retrieval-evidence-search/validation/M3-real.md`, `isolated-retrieval-gap-plan.md` (App. A–C), `isolated-single-run1-diagnosis.md`; `docs/roadmap/slice-002-technical-closure.md` §13–§17.

## 2. Architettura reale ricostruita

Percorso (definizioni v4–v10 con alias; v6 = senza recupero, v7–v10 = con recupero):

```
Evidence congelate (snapshot)
  → instradamento: single-call se ledger e stima di output entrano, altrimenti gerarchia (+ stadio Gap se ci sono Gap)
  → Directory: CatalogEntry, chiavi letterali A/B, Pack delle foglie, pagine (Members/Keys/Relations), candidati server (shared_literal_key, relazioni note)
  → Gerarchia: foglie A, foglie B (descriptor + candidati [+ item nelle sole foglie A]), riduzioni, Cross
  → Candidati: relazioni note, A, B, Cross → espansione (gruppi) / confronto (coppie) → round finale
  → Composizione deterministica alias → canonico reconcile-1 (per batch)
  → Validazione (compositore, CheckResult, References, gerarchia, motore)
  → Assembly: AnalysisContent (item per sezione, conflitti da disposizioni Open e da patch incompatibili), relazioni, mappa evidenze
  → Completamento (EvaluateHierarchical) → [recupero 003] → stadio Gap (una chiamata) → commit
```

| Passaggio | Input → output | Responsabilità del modello | Responsabilità deterministiche | Validazioni | Stato persistito, recovery |
|---|---|---|---|---|---|
| Instradamento (`DocumentAnalysisWorkflow`) | snapshot → single-call / gerarchia / fail-fast | — | misura del payload, stima di output `single-call-output-estimate-1`, `GapStageBudget` | budget di input/output | definizione e chiavi congelate nel run (`PromptVersions`), `ContractOf` (`:545-575`) |
| Directory (`ReconciliationDirectory.BuildAsync`, `:35-62`) | evidenze → foglie, pagine, set, candidati server | — | normalizzazione chiavi, ordinamento, **Pack misurato a ogni prefisso** (`DocumentBatchPacking.PackNextAsync`, `:29-77`), pagine (`ChainAsync`, `:96-115`), shared_literal_key | copertura per vista, nessuna riga spezzata | **non persistita**: ricalcolata a ogni processo (cache `_measures` in memoria, `DocumentReconcilePlan.cs:76`) |
| Foglia A | ~140 voci integrali → descriptor, candidati E–E, **item per ogni evidenza** | descriptor, candidati, **testo, sezione, classificazione di ogni item** | alias, id canonici, sourceIds | schema LeafA; compositore (classificazione/origine, `assumptions`, evidenze); `CheckResult`; descriptor obbligatorio (`ReconciliationHierarchy.cs:187-190`); copertura e domanda canonica dei Gap **solo a fine gerarchia** | WorkOutput Committed per batch; replay senza inferenza con verifica di `catalogHash`/`aiOutputHash`/`canonicalHash` (`DocumentReconcilePlan.cs:460-529`) |
| Foglia B | voci → descriptor, candidati | descriptor, candidati | come sopra | schema LeafB (`items` 0); `noFindings` se 0 candidati | come sopra |
| Riduzione / Cross | descriptor dei nodi → descriptor (solo riduzione), candidati N–N | descriptor, candidati fra nodi | membership esatta dei nodi, carry, livelli | `descriptor` per stadio, stretta decrescita | come sopra |
| Espansione v10 (`ExpandGridAsync`, `ReconciliationCandidates.cs:283-426`) | candidato di gruppi + blocco di voci con `gruppo=` → candidati E–E, `Unresolved` | collegamenti plausibili fra gruppi | griglia S1×S2, copertura bitmap, saturazione a 16, precheck delle chiamate | tetto 16 (schema e compositore), focus null/Unresolved | ordinali locali deterministici |
| Confronto (`CompareAsync`, `:240-273`) | coppia integrale → esito Compared/Open (+ candidati, patch) | **esito e motivo**, patch, candidati sulla stessa coppia | cache per coppia, una sola disposizione | focus obbligatorio, `noFindings` (vedi I-1) | `pairCache` in memoria, ricostruita al replay |
| Assembly (`DocumentReconcileAssembly`) | output Committed → AnalysisContent | — | item dalle proposte; **alternative dei conflitti composte dal server** da `Statement` e origine (`:98-113`); patch | sezione dichiarata per ogni item (`:121-123`) | funzione pura degli output |
| Stadio Gap (`GapState`, `DocumentReconcilePlan.cs:798-824`) | analisi assemblata + ledger/pacchetto → `gapAssessments` | valutazione per Gap | riferimenti `i<n>`/`c<n>`, domanda canonica per Gap (`GapStageInput.cs:82-95`) | gerarchia completa; **ogni Gap deve avere una domanda `openQuestions`**, altrimenti `coverage_incomplete gap_reference_missing` | WorkOutput dello stadio |

**Valutazione delle separazioni.**
- *Corrette:* ID, hash, provenienza, visite, membership, griglia, copertura e assembly sono deterministici e verificati al replay. Il modello vede solo alias. È un buon confine.
- *Sbagliate o incomplete:*
  - (a) il modello deve produrre campi **strutturali** che dipendono solo dallo stadio: `descriptor` null/obbligatorio, `items` vuoti, `focus` null, `noFindings` condizionale;
  - (b) il modello deve rigenerare, come item, informazioni già **determinate** dall'evidenza: testo, origine → classificazione, tipo → sezione, Gap → domanda;
  - (c) un **prompt unico** descrive sei compiti diversi, quindi le istruzioni di uno stadio contaminano gli altri (§4.3);
  - (d) la precondizione dello stadio Gap è verificata dopo l'intera gerarchia, non dove nasce.
- *Recovery:* corretta e idempotente per crash e takeover (replay su Committed, fence, lease). Assente per gli output **validi semanticamente ma rifiutati**: nessun retry, e il nuovo run riparte da zero (`ai-workflow.md` §5.3, riga «Retry PM dopo Failed», output non rimappabili perché il `batchId` include l'owner del run).

## 3. Analisi forense dei Run 1–5

Classi: **A** esterno alla riconciliazione · **B** difetto implementativo certo · **C** difetto di contratto modello/applicazione · **D** limite strutturale dell'algoritmo · **E** prestazioni · **F** comportamento probabilistico del modello.

| Run (def., commit) | Primo blocco osservato | Evidenza | Classe | Blocchi latenti (stesso run, a valle) |
|---|---|---|---|---|
| 1 (v7, `ce2c66e`) | estrazione `finish=length`, 16.384 token, prompt 4.388, 282 s | `NNN-result.json` della 012; `M3-real.md` §Diagnosi Run 1 | **A** (calibrazione `ExtractionOutputExpansion` 8 → 16, già corretta) + F | — (riconciliazione non raggiunta) |
| 2 (v7, `ccb61a3`) | indicizzazione 3/3 `Failed` in 0,3 s | embedding `sleeping` senza attivazione | **A** (corretto con attivazione on-demand) | — |
| 2 | chiamata 026, foglia B: 139 `items` | archivio 026; prompt v3 «items SOLO nelle foglie A» accanto alla regola di copertura; schema unico | **C** (schema permissivo, prompt ambiguo) + F | non noti |
| 3 (v8, `b445871`) | chiamata 026, foglia B: `descriptor: null` | schema v8 `["string","null"]` contro `ReconciliationHierarchy.cs:187-190` | **C** (schema ≠ validatore) | non noti |
| 4 (v9, `5cbcd15`) | chiamata 036, espansione: `finish=length`, 16.384 token, 63 voci, 285 s | `036-result.json`; 162 candidati enumerati (`ReconcileAliasContracts.cs:360-362`) | **D** (scansione di tutte le coppie demandata al modello, output non limitato) + E + F | 034 e 035 `Unresolved`: il messaggio cita N1/N2 ma non dice a quale gruppo appartiene ogni voce, quindi compito non rappresentabile (**C**, motivo dichiarato dal modello) ⇒ `candidate_unresolved` e copertura incompleta. Foglia A 022: la Gap E1 è resa come `openQuestions` **senza evidenza** (`i21`, `Assumed`) ⇒ E1 `evidence_not_mapped` (**D/F**) |
| 5 (v10, `07c1110`) | chiamata 030, confronto E165/E262: `focus Open` con motivo, 0 candidati, `noFindings: null` ⇒ rifiuto | `030-assistant.txt`; `ReconciliationContracts.cs:699`; prompt v5 riga 18 | **C** (regola coerente con prompt e §4.1, ma mal specificata rispetto al dominio: conta i campi, non gli esiti; non esprimibile in grammatica) + **F** (istruzione esplicita disattesa) | Foglia A 024: Gap E4 ⇒ item `requirements`/`Declared` ⇒ `gap_reference_missing:E4` a fine gerarchia (**D/F**). Foglie B 026/027: esattamente 16 candidati ciascuna, 20 su 32 `SameRule` deboli («stessa regola per linee diverse»), contro 1–5 nei Run 3–4 (**C/F probabile**: frase del tetto di 16 del prompt v5). 12 min 52 s di misure (**E**, §7) |
| Prova isolata Single (v10, `6d0180a`) | stadio Gap: E5 `Open` senza `openItem`/`openConflict` | `isolated-single-run1-diagnosis.md` §2 | **C** (regola condizionale fuori dalla grammatica; il riferimento è derivabile dal server, §4.4) + F | — |

Conteggi: 34 chiamate della gerarchia osservate nei Run 2–5, 4 rifiutate (una per run, ognuna di una classe nuova). Fra le 20 mappature Gap → domanda (5 Gap × 4 run) se ne contano 2 errate, entrambe bloccanti.

**Che cosa NON è dimostrato.** Il Run 5 si sarebbe fermato davvero su E4? Sì, se nessun altro item `openQuestions` avesse citato E4: nel batch 024 E4 era l'unica voce, e nessun altro meccanismo crea domande (`GapStageInput.cs:79-81`). Comportamento dell'espansione v10 con prompt v5 e membership: **mai osservato** (zero chiamate Expand nel Run 5). Recall reale della gerarchia sui conflitti gold: NotVerified.

## 4. Verifica sistematica dei contratti

### 4.1 Matrice per tipo di batch (definizione v10, profilo `-3`)

Legenda: **P** prompt `document-reconcile-v5`, **S** schema inviato, **D** DTO `AiReconcileOutput`, **K** compositore `ReconcileAliasComposer`, **V** `CheckResult` + `ReconciliationReferences` + `ReconciliationHierarchy.InvokeAsync`, **W** motore (`ReconciliationCandidates`) e workflow (completamento, stadio Gap).

| Stadio | Campo / regola | P | S | K | V | W | Coerenza |
|---|---|---|---|---|---|---|---|
| tutti | `noFindings` obbligatorio se nessun esito | «se `items`, `candidates` e `patches` sono tutti vuoti» (riga 18) | stringa nullable, nessun condizionale | copia | obbligatorio se 0 proposte, 0 candidati, 0 patch; **ignora le disposizioni** (`:699`) | nessun consumatore a valle (solo validazione) | **I-1** |
| Confronto | `focus` | obbligatorio; `Open` ⇒ motivo non vuoto; `Compared` ⇒ `reason: null` | oggetto; esito ∈ {Compared, Open}; `reason` nullable | `Open` senza motivo ⇒ rifiuto (`:298-301`); `Compared` con motivo accettato | `Open` richiede hash e motivo | una sola disposizione Compared/Open (`:264`) | **I-2** |
| Confronto | `candidates` | ammessi, estremi E | ammessi | `ParentCandidateId` = candidato in esame | — | figli `Pending` ⇒ round finale ⇒ **cache per coppia: ereditano la disposizione del padre** (`:243-247`, `:269-271`) | **I-3** |
| Espansione | `noFindings` con blocco vuoto | regola generale (riga 18), assente nella sezione dell'espansione (riga 20) | nullable | — | obbligatorio | griglia con molti blocchi senza collegamenti | **I-1** (latente su ogni blocco vuoto) |
| Foglia A | Gap ⇒ domanda canonica | **assente** | assente | assente | assente | `gap_reference_missing` dopo l'intera gerarchia (`DocumentReconcilePlan.cs:814-821`) | **I-4** |
| Foglia A | ogni evidenza citata | «ogni evidenza del batch deve essere citata» | non esprimibile | consente `openQuestions` senza evidenze | — | `evidence_not_mapped` a fine gerarchia (`ReconciliationCompletion.cs:275-276`) | **I-5** |
| Foglia A | classificazione | `Declared` solo con `origine=Message`, `Documented` con `Document`, altrimenti `Inferred`/`Assumed` ammessi | enum libero | controllo a senso unico (`:162-175`) | — | assembly per sezione | **I-6** (una Gap del PM `Inferred` passa; la distinzione fatto documentato / dichiarazione PM si perde) |
| tutti | istruzioni di stadio | un solo prompt per sei stadi | — | — | — | — | **I-7** (contaminazione: Run 2, Run 5 probabile) |
| Foglia, riduzione | `descriptor` ≤ 512, non solo spazi | ≤ 512 | `minLength: 1`, nessun `maxLength` | — | rifiuto (`:689-692`) | — | residuo dichiarato (v9) |
| tutti con candidati | alias nel batch, estremi distinti, nessun duplicato | dichiarati | solo pattern `^E`/`^N` | rifiuto (`:253-275`) | `Unique` (`:704`) | — | residuo, **ammesso dalla grammatica** (I-8) |
| Espansione, confronto | coerenza `op`/`value`/`target` delle patch | dichiarata | `value` anyOf indipendente da `op`; `target` con due nullable | rifiuto (`:366-460`) | `CheckPatch` | 0 patch nelle 34 chiamate reali | residuo (D9) |
| tutti | duplicati nello stesso batch | «nessun duplicato» | non esprimibile (`uniqueItems` ignorato) | rifiuto | rifiuto | `ai-workflow.md` §4.1: «duplicati uniscono le origini» (fra batch) | residuo; incoerenza documentale minore |

### 4.2 Incoerenze dimostrate (con test necessari)

| ID | Descrizione | Dimostrazione | Test positivo (v11) | Test negativo |
|---|---|---|---|---|
| I-1 | Un batch con una disposizione (Compared/Open/`candidate_unresolved`) è trattato come «senza risultati» | Run 5 030 rifiutata; Run 4 029–033 accettate solo con un candidato ridondante sulla stessa coppia | confronto `Open` + motivo, 0 candidati, `noFindings` null ⇒ accettato; blocco di espansione vuoto senza `noFindings` ⇒ accettato | in v1 lo stesso output resta rifiutato (caratterizzazione, v10 invariata) |
| I-2 | Lo schema ammette `Open` con `reason: null`, il compositore lo rifiuta | `BuildBatchSchema` Compare (`ReconcileAliasContracts.cs:483-486`) contro `ReconcileAliasComposer.cs:298-301` | la grammatica v11 rende `Open` senza motivo non generabile; `Compared` con o senza motivo ammesso | istanza `Open`/null rifiutata dal validatore della v11 come difesa in profondità |
| I-3 | Un candidato emesso nel confronto è risolto per cache con la disposizione del padre, senza verifica del suo tipo | `CompareAsync` imposta `pairCache` prima di `AddChildren`; i figli sulla stessa coppia trovano un hit (`ReconciliationCandidates.cs:243-247`, `:269-271`); ogni disposizione `Open` diventa un conflitto (`DocumentReconcileAssembly.cs:98-113`) | v11: lo schema del confronto ha `candidates.maxItems = 0`; il dubbio si esprime solo con `Open` + motivo | un `Contradicts` emesso in un confronto `Compared` diventa `Compared` senza chiamata (conflitto perso); un `DependsOn` invertito in un confronto `Open` (forma 029 del Run 4) eredita `Open` e produce un **secondo conflitto** per la stessa coppia |
| I-4 | La precondizione dello stadio Gap non è nel contratto del passo che deve soddisfarla | prompt v5 senza istruzione; Run 5 E4 | v11: domanda canonica di ogni Gap composta dal server ⇒ `MissingGaps` vuoto per costruzione | v10: output 024 del Run 5 ⇒ `gap_reference_missing:E4` (fixture dall'archivio) |
| I-5 | Copertura delle foglie A verificata solo a fine gerarchia | Run 4 022 (E1) | v11: item per ogni voce ⇒ `evidence_not_mapped` impossibile | v10: output 022 del Run 4 ⇒ `evidence_not_mapped` (fixture) |
| I-6 | Classificazione lasciata al modello, controllo a senso unico | E1 `Inferred` nei Run 2, 3, 5 | v11: `Declared` ⇔ origine Message, `Documented` ⇔ Document, `Assumed` ⇔ tipo Assumption | — |
| I-7 | Prompt unico per sei compiti | Run 2 026; Run 5 026/027 (16+16, probabile) | v11: istruzioni per stadio (comune + sezione dello stadio), SHA per stadio nel `configHash` | test che nessuna istruzione di uno stadio compaia nel prompt di un altro |
| I-8 | La grammatica ammette alias fuori batch, estremi coincidenti, duplicati, `descriptor` > 512 o di soli spazi, testi di soli spazi nelle patch, patch incoerenti | dal codice; da quantificare con il generatore di istanze (RH-003). **0 occorrenze** nelle 34 chiamate reali | v11: classi residue tipizzate e misurate (RH-016, RH-050); retry solo come opzione differita O1 | restano fatali (fail-closed con diagnostica tipizzata) |

### 4.3 Caso 030: regola di dominio e responsabilità

- **Fatto.** Il confronto è un batch con un candidato in esame. Il suo unico esito ammesso dal motore è *una* disposizione `Compared` o `Open` per quel candidato (`ReconciliationCandidates.cs:264`). L'esito della 030 (`Open` con il motivo da presentare al PM) è esattamente l'informazione che l'assembly trasforma in conflitto (`DocumentReconcileAssembly.cs:98-113`).
- **Regola di dominio corretta.**
  - Ogni batch deve lasciare una **traccia verificabile** del proprio esito. Quella traccia esiste già ed è del server: voci e nodi visitati (`visitedEntryIds`/`visitedNodeIds`), hash dell'output nella disposizione e nel WorkOutput, disposizione del candidato in esame.
  - `noFindingsReason` è una **spiegazione** facoltativa del modello. Non ha consumatori a valle (unico uso: la riga 699) e non deve condizionare la validità.
  - Il «nessun esito» di un batch è *zero proposte, zero candidati, zero patch **e** zero disposizioni*. Nel confronto questo caso è impossibile per costruzione.
- **Responsabilità.**
  - L'invariante «il batch ha un esito tracciabile» spetta al **validatore canonico e al motore**, non al modello.
  - Lo **schema** deve esprimere solo ciò che il modello decide: nel confronto esito e motivo (`Open` ⇒ motivo non vuoto, esprimibile con `anyOf` sull'esito).
  - Il **compositore** non deve né inventare né completare il motivo.
- **Perché non basta «aggiungere `noFindings` allo schema».** Un vincolo condizionale con `noFindings` *prima* di `candidates` nell'ordine delle proprietà costringerebbe il modello a impegnarsi su un campo formale prima della scelta semantica. Con `noFindings: null` scelto per primo, la grammatica obbligherebbe a inventare un candidato. La sonda v9 mostra che la grammatica forza la forma, non il contenuto (descriptor `"\"\""`, candidati con gli stessi estremi: `M3-real.md` §Esito della verifica dei vincoli v9).
- **Principio di progetto per la v11.** I vincoli condizionali della grammatica stanno solo *a valle* di una scelta semantica (prima l'esito, poi il motivo), mai a monte.

### 4.4 Segnale trasversale: stadio Gap (E5)

Stessa classe di I-1/I-4: una regola condizionale («`Open` ⇒ esattamente uno fra `openItem` e `openConflict`») non è espressa nella grammatica. In più, il valore di `openItem` è **derivabile dal server** quando non si riferisce un conflitto: è la domanda canonica di quel Gap (`GapStageReferences.GapQuestion`). Quindi il modello deve ripetere un'informazione strutturale. Il principio di §4.3 (il modello decide se il Gap è aperto per conflitto o come domanda; il server compone il riferimento) si applicherebbe anche lì. **Non** fa parte di questo intervento (vincolo del mandato e contratti 003): è registrato come decisione futura in [plan.md](plan.md) §11.

## 5. Valutazione critica della gerarchia

| Domanda | Risposta con evidenza |
|---|---|
| Più passaggi generativi del necessario? | Sì, nelle foglie A: rigenerano 1:1 le evidenze come item. Nei Run 2–5, 1.127 item su 1.128 citano esattamente un'evidenza, con le voci in corrispondenza biunivoca; l'unica eccezione è l'item senza evidenze del Run 4 (E1). Costo nel Run 5: 18.361 dei 22.244 token di output (82,5%) e 328 s dei 404 s della riconciliazione. In produzione il costo cresce: la stima di output della single-call scala con gli item e decide quando si entra in gerarchia. |
| Decisioni che potrebbero essere deterministiche? | Testo dell'item (= enunciato), classificazione (= origine; `Assumed` per il tipo Assumption), sezione (tabella dal tipo di evidenza, salvo 3 tipi da decidere), domanda canonica del Gap, campi nulli o vuoti per stadio, `noFindings`. L'assembly compone già alternative di conflitto dal server in questo modo (`DocumentReconcileAssembly.cs:101-107`). |
| Confronta più volte gli stessi candidati? | Le coppie no (`pairCache`). Le espansioni sì: lo stesso insieme di membri è riscandito per candidati di gruppo diversi (rischio 5 del manifesto T040 v6, 7 candidati su 2 partizioni, 68 blocchi proiettati). **Ipotetico**, mai eseguito: da simulare offline (RH-005) prima di qualunque modifica. |
| Moltiplica i candidati senza controllo? | Nelle foglie B del Run 5: 32 candidati, 20 `SameRule` deboli, quindi 32 confronti e rischio di conflitti spuri. Probabile effetto del prompt unico (I-7). Il precheck v10 protegge il budget, non la qualità. |
| Preserva conflitti, alternative, casi irrisolti? | Sì per costruzione nei percorsi validi: `Open` diventa conflitto, `NotProcessed` impedisce la completezza, saturazione fail-closed. Due falle: I-3 (perdita possibile di un conflitto emesso nel confronto) e candidati Gap↔evidenza che producono conflitti «domanda contro risposta» (Run 4 029; Run 5 027 E255–E4, osservato), lavoro che spetta allo stadio Gap (§13.7). |
| Sensibile a lunghezza e distribuzione? | Sì. Foglie limitate dall'output (`OutputExpansion` 1,0 su tutto il payload) a causa degli item; espansione quadratica sui gruppi grandi; con W=131072 i blocchi di espansione crescono e la saturazione a 16 diventa più probabile (**ipotetico**). |
| Può fallire dopo molte chiamate senza nulla di utilizzabile? | Sì: violazione permanente, nessun retry, nessun riuso fra run, precondizione Gap verificata a fine gerarchia. Con *n* chiamate e probabilità *p* di violazione residua per chiamata, la riuscita vale (1−*p*)<sup>*n*</sup>; per *n* ≈ 100–150 (proiezione T040 con espansioni) anche *p* = 0,5% dà 47–61%. |
| Costo ragionevole su documenti realistici? | Su T040-single (W=32768, 5.570 token di testo) la gerarchia completa è stimata in ≈ 80–150 chiamate e ≈ 25–35 min, a cui si sommano ≈ 23 min di estrazione. In produzione (W=131072) lo stesso ledger (282 evidenze) supererebbe di poco la soglia di output della single-call (≈ 270, §8) e andrebbe in gerarchia con foglie più grandi. Il costo eccessivo è concentrato in foglie A, misure ed espansioni ripetute. |

**Strutture complete o decisioni circoscritte?** L'ipotesi è verificata per la parte strutturale. Il modello deve fornire solo ciò che richiede interpretazione: descriptor, collegamenti candidati, esito del confronto con motivo, patch, `Unresolved`. Il codice compone le strutture canoniche. Nessuna evidenza suggerisce di togliere al modello la scoperta dei candidati: è il contenuto semantico della riconciliazione.

## 6. Affidabilità degli output e recovery

| Output del modello | Oggi (v10) | Valutazione | v11 proposta |
|---|---|---|---|
| JSON non parsabile | `work_output_invalid` permanente | con la grammatica avviene solo per troncamento | invariato (fail-closed) |
| `finish=length` | permanente | Run 1 (estrazione) e Run 4 (espansione): rimedio strutturale (calibrazione, tetto), non un retry | invariato; output della v11 piccoli (niente item) |
| Conforme allo schema, non valido semanticamente | permanente | classi dimostrate I-1, I-2, I-3; I-8 dal codice | I-1/I-2/I-3 eliminate per contratto; I-8 tipizzata, misurata, ancora fatale |
| Alias inesistente o fuori batch | permanente | ammesso dalla grammatica (pattern); mai osservato | invariato (fatale, tipizzato); O1 se osservato |
| Candidati duplicati | permanente | nessuna perdita semantica, ma la fusione silenziosa scarterebbe una motivazione | invariato (nessuna fusione); O1 se osservato |
| Inconcludente ma legittimo (confronto `Open`, blocco vuoto) | **rifiutato** se manca `noFindings` | difetto (I-1) | accettato |
| Item di foglia errati (Gap non domanda, evidenza non citata) | scoperto a fine gerarchia | precondizione fuori contratto (I-4, I-5) | eliminato: item composti dal server |

Meccanismi valutati:

| Meccanismo | Esito |
|---|---|
| Prevenzione con contratti più stretti | **Sì**, dove la grammatica lo esprime con un vincolo a valle della scelta (§4.3) |
| Scomposizione deterministica | Già presente per l'espansione (griglia); non necessaria per gli altri stadi della v11 |
| Retry mirato e limitato | **Differito (O1).** Con il protocollo T137 del port il payload del retry, con la prima risposta, deve entrare nel budget: per foglie ed espansioni, impacchettate al massimo prefisso con 256 token di riserva, non entra (`DocumentBatchPort.cs:391-406`). In più: collisione degli ordinali del marcatore, perdita della risposta del retry a un crash, nessuna occorrenza reale delle classi I-8. Utile, al più, per confronto, riduzione e cross |
| Correzione di un singolo output | No: niente patch dell'output del modello lato server (correzione silenziosa) |
| Stati espliciti di non risolto | Già presenti (`NotProcessed` con motivo) |
| Arresto fail-closed con diagnostica | Mantenuto dopo il retry |
| Ripresa senza ripetere il lavoro committato | Garantita entro lo stesso run (replay). Fra run diversi richiederebbe un'identità dei batch indipendente dall'owner: fuori perimetro, rischio residuo dichiarato |

## 7. Analisi prestazionale

**Misurato** (Run 5, finestra 10:00:50–10:13:42, 772 s): 2.414 `/tokenize` e 1.843 `/apply-template`, cioè 4.257 richieste sequenziali per 656 s (85%), ≈ 154 ms ciascuna, con latenza piatta rispetto alla dimensione del testo (`isolated-single-run1-diagnosis.md` §5).

**Meccanismo, dal codice.**
- Ogni misura (`DocumentPreflight.CountAsync`, `ContextBudget.cs:472-525`) costa 1 `/apply-template` del messaggio intero (istruzioni di ≈ 6,8k caratteri comprese) e 1 `/tokenize` del testo reso, più 1 `/tokenize` per ogni extra: lo schema, testo costante.
- `PackNextAsync` misura ogni prefisso: *k*+1 misure per una foglia di *k* voci.
- Le pagine del catalogo (`ChainAsync`, misuratore `PageMeasurer` senza schema, `DocumentReconcilePlan.cs:1148-1150`) passano dallo stesso Pack, voce per voce. Le pagine però non compaiono in nessun messaggio alias (`ReconcileAliasMessages.Build`, `:25-106`).

**Scomposizione inferita: ipotesi, non dimostrata (da confermare con RH-004).** La quota «Keys/Relations ≈ 711» è un residuo per differenza, quindi la coincidenza sui `/apply-template` vale per costruzione; sui `/tokenize` restano 3 richieste di scarto. La prova isolata (stessa v10, W=32768, 283 evidenze) registra prima della gerarchia solo 577 `/apply-template` e 865 `/tokenize`. È incompatibile con ≥ 567 misure di foglia più ≥ 564 misure di pagine per voce, salvo differenze di ledger (per esempio `DirectoryFacts` vuoti) o di confine delle fasi del router. **Non riconciliata.** *Aggiornamento M0 (2026-11-14): RH-004 ha riconciliato i conteggi con un residuo di 2 coppie di richieste e ha corretto le uguaglianze «per vista» (cache per contenuto senza la vista): vedi «Evidenze M0», C-1…C-3.*

| Misuratore | Misure | `/apply-template` | `/tokenize` |
|---|---|---|---|
| Pack foglie A (138+1, 143+1, 1) e B (140+1, 142) | 567 | 567 | 1.134 |
| Pack pagine (Members ≈ 564, Keys/Relations ≈ 711) | ≈ 1.275 | ≈ 1.275 | ≈ 1.275 |
| Rimisura del primo batch inviato | 1 | 1 | 2 |
| **Totale** | | **1.843** (osservato 1.843) | **2.411** (osservato 2.414) |

**Interventi.**

| # | Intervento | Tipo | Definizioni | Beneficio stimato sul Run 5 |
|---|---|---|---|---|
| P-1 | Memoizzare i token degli extra (schema) per (impronta del runtime congelata, modello, SHA-256 del testo), con la vita del piano del run | throughput, risultato identico | **tutte** (numeri identici, nessuna identità cambia) | −567 `/tokenize` (−13%, ≈ 87 s); anche −281 nella misura dei costi del recupero 003 (stessa funzione) |
| P-2 | Pagine limitate da un numero fisso di voci, senza tokenizer | complessità algoritmica | solo in una definizione nuova (cambia gli hash delle pagine) | **se** l'ipotesi regge: ≈ −2.550 richieste (−60%, ≈ 390 s). Opzione differita O3 |
| P-3 | Pack delle foglie con ricerca esponenziale + binaria del confine (ogni prefisso tentato misurato per intero, nessuna additività; verifica finale «*k* entra, *k*+1 no») | complessità algoritmica | solo in una definizione nuova | 567 → ≈ 80 misure, ≈ −970 richieste (con P-1). Opzione differita O3 |
| — | Totale P-1+P-2+P-3 | | | 4.257 → ≈ 170 richieste (−96%) se l'ipotesi regge. **Nel piano raccomandato solo P-1** (−13%) |

- *Misurato:* conteggi complessivi e latenza per richiesta.
- *Ipotetico:* la ripartizione per misuratore (RH-004 la rende esatta offline con un tokenizer finto che conta le richieste) e la monotonia dei token rispetto ai prefissi. Senza monotonia, P-3 produce comunque batch validi ma possibilmente diversi dalla scansione lineare: per questo è solo v11.
- *Cache escluse:* misure del messaggio reso, cache fra processi, cache senza impronta. Il template e la fase entrano nel resto della misura, che non è memoizzato.
- *Effetto sul recovery:* oggi ogni ripresa in un processo nuovo rifà tutte le misure della directory, ≈ 11 min prima della prima chiamata; con P-2 e P-3, meno di 1 minuto.

## 8. Documenti realmente usati dai PM

Nell'ambiente di lavoro non è disponibile alcun documento cliente autorizzato: `benchmarks/` contiene solo un benchmark zero-shot. La valutazione usa quindi il codice, i regimi di configurazione e un'analisi funzionale ERP/EDI tipo; il plan prevede una fixture sintetica realistica scritta da zero (RH-040).

**Regime di produzione** (`infra/compose.yaml`): W=131072, output dell'analisi O=32768, quindi B ≈ 97k token.
- La gerarchia scatta se il ledger supera B **o** se la stima di output della single-call supera O (`AnalysisOutputEstimate`, policy `single-call-output-estimate-1`: ≈ 54 token di struttura per evidenza non-Gap più l'enunciato a 2,5 caratteri/token × 1,25, con margini 1,08 × 1,25; per enunciati da ≈ 70 caratteri, ≈ 120 token per evidenza). Il limite di output arriva prima: **≈ 270 evidenze** con O=32768, ≈ 135 con O=16384.
- Senza recupero (v6/v11) la gerarchia con Gap esegue lo stadio Gap solo se **l'intero ledger** entra nel budget dello stadio; altrimenti c'è fail-fast a zero invii, prima della gerarchia. Con ≈ 65 token per voce di ledger il limite è ≈ 1.400 evidenze in produzione e ≈ 200 con W=32768.
- T040 usa W=32768 con recupero proprio per forzare la gerarchia oltre questo limite: è uno **stress test** del percorso con recupero, non il caso rappresentativo della 002.

Le soglie di 270 e 1.400 evidenze derivano dalle costanti della policy e del budget. Le **evidenze per tipologia** della tabella seguente sono invece *ipotesi*: l'unica densità misurata, T040 con circa 20 token di testo per evidenza e una regola per frase, è molto più alta di quella di un'analisi in prosa, e con quella densità 30–60 pagine supererebbero le 1.400 evidenze. RH-040 registra la densità della fixture rappresentativa; prima di allora le righe che seguono non sostengono D1.

| Tipologia | Evidenze tipiche (ipotesi) | Percorso in produzione (senza recupero) | Rischi principali |
|---|---|---|---|
| Documento breve (≤ 15 pagine) | ≤ 200 | single-call | nessuno della gerarchia |
| Analisi funzionale media ERP/EDI (30–60 pagine: flussi, requisiti, 3–5 tracciati, tabelle di trascodifica, gestione errori, decisioni aperte) | 300–600 | **gerarchia + stadio Gap** | le tabelle generano molte evidenze strutturalmente simili («campo X lunghezza N», «codice A → B»): stesso schema dei `SameRule` deboli del Run 5 ⇒ candidati in eccesso e conflitti spuri; foglie A costose; precondizione Gap affidata al modello (I-4) |
| Fascicolo multi-documento (specifica + verbali + versioni) | 600–2.000 | gerarchia + stadio Gap fino a ≈ 1.400 evidenze; oltre, fail-fast (serve la 003) | conflitti di versione fra documenti (Cross ed espansioni grandi, saturazione), Gap del PM risolti da documenti diversi |
| Stress (T082/T083, riempitivo artificiale) | molto alto | fail-fast con Gap; gerarchia senza Gap | non rappresentativo della semantica, utile per costo e limiti |

Se l'ipotesi sulla densità è confermata, la gerarchia senza recupero è il percorso normale per le analisi funzionali medie. Comunque vada, **ogni** analisi della 002 che entra in gerarchia oggi usa il contratto della v6, con i difetti I-1…I-7: è questa la motivazione di D1.

Che cosa tratta bene la gerarchia: provenienza, conflitti fra documenti (Cross più confronto), alternative non decise, regole con eccezioni (`Qualifies`, osservati nel Run 4). Che cosa tratta male: complessità sproporzionata sulle tabelle (righe simili ⇒ candidati), item 1:1 che duplicano le evidenze, e la dipendenza dello stadio Gap da una classificazione generativa.

## 9. Alternative valutate

| Alternativa | Contenuto |
|---|---|
| **A** — stabilizzazione minima | v11 = v6 + profilo con confronto senza candidati ed esito motivato + validazione V2; item ancora generativi |
| **A+** | A + regole fail-fast nelle foglie A (Gap ⇒ `openQuestions`, copertura totale), tipizzate. Senza retry, che per le foglie non entra nel budget (§6): il fallimento arriva prima ma resta fatale |
| **B (raccomandata)** | A + item delle foglie A **composti dal server** (tipo, origine, certezza) con gli estremi dei conflitti `Open` solo come alternative + prompt unico v6 ripulito + P-1 + violazioni tipizzate. Attivazione solo dopo le verifiche reali |
| **B+** — versione iniziale di questo piano | B + istruzioni per stadio, retry informato, pagine senza tokenizer e Pack a ricerca del confine |
| **C** — riscrittura | candidati generati dal recupero ibrido (003) e solo confronti al modello, senza descriptor né livelli |
| **D** — cambiare modello o finestra | es. W=131072 nella campagna |
| **E** — retry generico | N tentativi per qualunque violazione |

| Criterio | A | A+ | **B** | B+ | C | D | E |
|---|---|---|---|---|---|---|---|
| Affidabilità sui blocchi osservati | chiude I-1, I-2, I-3; lascia I-4–I-7 | come A, con I-4 e I-5 rilevate subito (ancora fatali) | chiude I-1–I-6 per costruzione; I-7 mitigata; I-8 resta (0 occorrenze) | come B, più I-8 per confronto, riduzione e cross | dipende dal recall del recupero (non accettato) | nessuna: il validatore rifiuta la 030 con qualunque modello | maschera i difetti |
| Rischi semantici | bassi | bassi | testo degli item = enunciato; tabella da approvare (D2), certezza inclusa | come B, più il rischio che il retry perda candidati validi | alti | — | alti |
| Complessità | bassa | bassa | media | alta | alta | bassa | bassa |
| Compatibilità retroattiva | piena | piena | piena (v2–v10 congelate) | piena, ma tocca il port | rottura | — | — |
| Costo computazionale | invariato | invariato | −80% dei token di output della gerarchia; −13% delle misure | come B; −96% delle misure se l'ipotesi del §7 regge | variabile | invariato | crescente |
| Verifica offline | sì | sì | sì | sì, con più casi di recovery | parziale | no | sì |
| Rischio di regressione | basso | basso | medio, isolato dalla definizione | medio-alto | alto | — | alto |
| Effort | ≈ 14 gg | ≈ 15 gg | **≈ 21,5 gg offline** (18–25), più 1,75 per verifiche reali e attivazione | ≈ 30 gg | ≥ 35 gg | — | ≈ 3 gg |

**Decisione raccomandata: B.**
- A e A+ lasciano al modello il lavoro più costoso e la fonte dei due blocchi latenti osservati.
- B+ aggiunge meccanismi senza evidenza propria: nessuna classe I-8 nelle 34 chiamate reali, retry non realizzabile per foglie ed espansioni, scomposizione delle misure non riconciliata. Le sue parti restano opzioni differite con criterio d'ingresso.
- C confonde la 002 con la 003.
- D non agisce sulla causa.
- E viola il vincolo del retry limitato.

B non riscrive la pipeline. Restano invariati motore, griglia v10, port, checkpoint e modello dati. Cambiano soltanto, in una definizione nuova:
- chi compone gli item delle foglie A;
- una regola di assembly per gli estremi dei conflitti `Open`;
- lo schema del confronto;
- una regola di validazione;
- il prompt.

**Che cosa B non risolve** (rischi residui, plan §8 e §12):
- violazioni residue ancora fatali;
- costo delle misure ridotto solo del 13%;
- costo quadratico delle espansioni ripetute;
- candidati Gap↔evidenza nella gerarchia;
- riuso del lavoro fra run diversi;
- stadio Gap (E5);
- percorso con recupero (v12);
- qualità semantica reale (verifiche M5).

## 10. Problemi dimostrati e ipotetici

| Dimostrati (codice + archivi) | Ipotetici (da verificare) |
|---|---|
| I-1 (030, difetto di contratto), I-2, meccanismo di I-3 (perdita o duplicazione di conflitti), I-4 (Run 5 E4), I-5 (Run 4 E1), I-6, I-7 per il Run 2; v6 non protetta; item 1:1 (1.127/1.128) e loro costo; pagine misurate ma mai inviate (codice); soglie 270 e 1.400 dalle costanti | I-7 per il Run 5 (16+16); scomposizione 567/1.275 e sua riconciliazione con la prova isolata (RH-004); densità di evidenze per pagina dei documenti reali (RH-040); monotonia per P-3; costo delle espansioni ripetute e saturazione con W=131072 (RH-005); frequenza reale delle classi I-8 (RH-050); comportamento del modello con il prompt v6 e le foglie senza item (M5) |

Limiti dell'analisi: database dei run eliminati; corpi delle richieste `/tokenize` non registrati; un solo corpus reale (T040, sintetico senza riempitivo); nessun documento PM autorizzato disponibile.

## 11. Revisione indipendente

Revisione in sola lettura di un agente architetto, 25 rilievi verificati sul codice e sugli archivi. Esiti:

| Rilievo | Esito |
|---|---|
| Due punti di instradamento mancanti in RH-010 (`DocumentAnalysisWorkflow.cs:678`, `:835`): con la v11 la single-call avrebbe perso il formato di messaggio 2 e l'instradamento sarebbe caduto nel ramo v5 | **Accolto, verificato.** RH-010 li include e prova single-call e instradamento v11 = v6 |
| Retry non realizzabile per foglie ed espansioni (payload del retry oltre budget); collisione dell'ordinale del marcatore; semantica T137 al crash; registrazione della variante alla ripresa; codifica della prima risposta; precheck ottimista | **Accolto.** Retry spostato nell'opzione differita O1 con questi vincoli; D5 ritirata; port non toccato |
| Attivazione prima delle verifiche reali (costituzione VIII) | **Accolto.** RH-060 dipende da RH-050 e RH-051 |
| `tasks.md` parallelo contro la regola «unica baseline operativa» del `CLAUDE.md` | **Accolto come conflitto da decidere (D11).** Il file è un allegato tecnico; dopo il mandato i task vanno riportati nel `tasks.md` ufficiale |
| Certezza delle evidenze ignorata dalla proiezione (`Hypothesized`/`ToClarify` presentati come fatti) | **Accolto.** Tabella con la certezza e test esaustivo |
| Avviso D3 deterministico e fatto conteso «deciso» per gli estremi dei conflitti `Open` (`EvidenceCoverage.cs:1066-1104`) | **Accolto, verificato.** Regola D2b nell'assembly v11 |
| 030 classificata «difetto implementativo certo» | **Accolto.** Riclassificata C + F |
| Scomposizione delle misure presentata come dimostrata e non riconciliata con la prova isolata | **Accolto.** Spostata fra le ipotesi; P-2/P-3 nell'opzione differita O3 |
| Densità di evidenze per pagina non fondata | **Accolto.** Marcata come ipotesi; D1 motivata dalla v6 non protetta |
| `const` fuori dal sottoinsieme validato (`ReconcileSchemaSubset.cs:15-18`) | **Accolto, verificato.** `enum` a un valore |
| Etichettatura incompleta delle violazioni; testi delle patch controllati solo in `CheckPatch` | **Accolto, verificato** (`ReconciliationContracts.cs:858`, `:867`). Tabella completa in RH-013 |
| Istruzioni di stadio nel messaggio con il materiale non fidato | **Accolto.** Prompt unico ripulito; istruzioni di sistema per stadio nell'opzione differita O2 |
| Replay di gerarchie intere impossibile; costruttore di fixture mancante | **Accolto.** RH-006, replay solo per batch |
| Proporzionalità: variante più piccola con la stessa affidabilità sui blocchi osservati | **Accolto.** È la nuova raccomandazione B; la versione iniziale è B+ |
| Test di conservazione tautologici; inflazione dei candidati non coperta | **Accolto.** RH-041 dichiarato strutturale, con scenario di inflazione; conservazione rimandata a M5 |
| I-3 genera anche conflitti duplicati | **Accolto.** I-3 e RH-002 estesi |
| Incoerenze interne (ID, «port invariato», numero dei punti) e caso v10 del test di `ContractOf` | **Accolto.** Testi riallineati; il ramo v10 non si tocca |
| «Una sola citazione per voce» in tutti i run | Già corretto prima della revisione (1.127/1.128) |

## 12. Evidenze

Sezione prevista dalla regola di STOP generale di [tasks.md](tasks.md). Le evidenze di M0 e M1 sono qui sotto; quelle di M2–M4 saranno aggiunte solo se quelle milestone verranno autorizzate.

### Evidenze M0 (2026-11-13/14; T154–T159; solo offline, nessuna modifica di `backend/src`, nessuna inferenza)

Commit locali: RH-001 `5deb3ed`, RH-006 `1192ef6`, RH-002 `371eeb4`, RH-003 `80da706`, RH-004 `bafa8a4`, RH-005 `1fd9e4d`. Test mirati: 80 (RH-001/006), 30 (RH-002/003), 45 (RH-004/005), tutti verdi, riesaminati dal coordinatore.

**Correzioni alla diagnosi imposte dai test** (la diagnosi è stata corretta, non il codice):

| # | Affermazione | Esito | Correzione |
|---|---|---|---|
| C-1 | §7: `apply-template(foglia)` = Σ(*k*+1) − 1 **per vista** e `apply-template(pagina)` = Σ(voci + interruzioni) **per vista** | **Falsa come formulata** | Valgono sui **prefissi distinti**: la cache `_measures` del piano è per contenuto del prefisso e non contiene la vista, e il Pack misura sempre con «Vista: A». Con `DirectoryFacts` vuoti A e B hanno lo stesso ordine (per GUID), quindi la vista B costa zero (283 misure invece di 566 per le foglie, 282 invece di 564 per i Members). Con fatti deterministici le foglie tornano a Σ(*k*+1)−1 per vista (566) ma i Members coincidono in parte (521 invece di 564) |
| C-2 | §7: la prova isolata (577/865) è incompatibile con 567 + 564 misure, «salvo differenze di ledger (per esempio `DirectoryFacts` vuoti)» | **Ipotesi confermata** | Con fatti vuoti il piano da solo dà 567 `/apply-template` e 851 `/tokenize`. 865 − 577 = 288 = misure con schema = 284 di Pack (3 foglie A 138/143/1, come nel Run 5) + 1 rimisura + 3 fuori dal piano. Le richieste senza schema (289) = 282 pagine Members + 5 sonde del monitor, con **un residuo di 2 coppie** (0,3%), **non dimostrato** (i corpi non sono registrati) |
| C-3 | §7: «567 misure di foglia» e «≈ 1.275 di pagine» nel Run 5 | **Parzialmente corretta** | Identità del Run 5: 2.414 − 1.843 = 571 = 567 (Pack) + 1 (rimisura) + 3 (fuori dal piano). Le pagine sono ≈ 1.260 (1.272 meno le sonde); il «≈ 1.275» è un residuo che le include. Il meccanismo regge; la stima assoluta non è riproducibile offline senza i fatti reali dell'estrazione |
| C-4 | RH-013, tabella: «`value` incoerente con `op`» ⇒ `PatchShape` (`ReconcileAliasComposer.cs:457-459`) | **Sbagliata nel percorso alias** | `AiPatchConverter` (`AnalysisAliasContracts.cs:376-382`) deserializza `value` secondo `op`: un `value` incoerente fallisce in decodifica (`JsonException`) e dà **`Undecodable`**. Il ramo del compositore e quello di `CheckPatch` non sono mai raggiunti dal percorso alias (0 occorrenze) |
| C-5 | RH-016: `Undecodable` non compare mai per la grammatica `by-stage-1` | **Non garantita** | Lo `anyOf` del `value` nel profilo `-3` è indipendente da `op` e produce il rifiuto in decodifica nel 59–61% delle istanze di Expand e Compare. RH-016 passa solo se la grammatica `by-stage-1` lega `value` a `op` (`anyOf` di coppie `op` `enum` + `value`), costrutto da verificare dentro `ReconcileSchemaSubset.Keywords` |
| C-6 | Ledger «283 evidenze» | **Precisazione** | Nello snapshot del piano entrano 282 evidenze (277 non-Gap + 5 Gap): la 283ª è del documento escluso dal PM |

**RH-001/RH-006 (T154/T155).** 34 fixture (17 foglie, 6 riduzioni, 2 cross, 3 espansioni, 6 confronti), estratte dal solo corpo delle richieste; il messaggio utente riscritto dai campi estratti coincide **byte per byte** con l'archivio su tutte e 34; `assistantSha256` verificati; nessun segreto, header, percorso HTTP o indirizzo IPv4 nelle fixture. Tutte e 34 le richieste sono ricostruibili con il compositore di produzione (nessuno STOP): righe `<<<VOCE` e messaggio intero uguali all'originale, compresa la 025 del Run 3 (foglia B da 142 voci, senza item già proposti: nessuna eccezione). Osservazioni: nessun archivio contiene membership (`gruppo=`) né relazioni note (l'espansione con membership v10 è esercitata solo su una variante sintetica della 036); le espansioni 034–036 del Run 4 portano quasi un item già proposto per voce (109, 110 e 62 item per 109, 110 e 63 voci). Il compositore accetta gli output archiviati salvo Run 2 026 (item nelle foglie B) e Run 4 036 (JSON troncato); accetta anche Run 3 026 e Run 5 030: i loro rifiuti stanno più a valle (descriptor nullo alla gerarchia; `noFindings` a `CheckResult`), coerente con la classe «schema ≠ validatore». Il rifiuto di `CheckResult` sull'output archiviato della 030 non è ancora stato eseguito (è materia di RH-017, non autorizzato); RH-002 caso 1 lo prova su un'istanza equivalente.

**RH-002 (T156): i nove casi coincidono con l'assessment, identici in v10 e v6** (lo schema inviato ammette il DTO in tutti i casi).

| Caso | Esito osservato |
|---|---|
| 1 | Confronto `Open` con motivo, 0 candidati, `noFindings` null: compositore accetta, validatore rifiuta con `noFindingsReason: obbligatorio quando non ci sono proposte, candidati né patch.` (I-1). Anche un confronto `Compared` senza candidati è rifiutato: la regola conta i campi, non gli esiti |
| 2 | `Open` con `reason` null o di soli spazi: il compositore rifiuta con `focus.reason: obbligatorio e non vuoto quando l'esito è Open.` (I-2) |
| 3 | `Contradicts` sulla stessa coppia con `focus Compared`: accettato; il figlio diventa `Compared` per cache con gli stessi hash, **una sola chiamata**, 0 disposizioni `Open`, 0 conflitti all'assembly: il conflitto dichiarato dal modello non arriva al PM (I-3) |
| 4 | `DependsOn` invertito con `focus Open`: il figlio eredita `Open` senza chiamate (1 sola), 2 disposizioni `Open`, **2 conflitti** con id diversi e stesse alternative (I-3, forma 029) |
| 5 | Blocco di espansione vuoto: compositore accetta, validatore rifiuta con lo stesso messaggio (I-1 latente) |
| 6 | Foglia B senza candidati e `noFindings` null: rifiutata dal validatore |
| 7 | Forma dei confronti del Run 4: accettata; dettaglio nuovo: il motore scarta il figlio con identità uguale al padre, resta un solo conflitto |
| 8 | Foglia A con una Gap come `requirements`: accettata dal compositore e dal validatore (I-4). Il compositore non vede il tipo di evidenza: `ReconcileCatalogEntry` porta alias, id, hash e origine |
| 9 | Foglia A con `openQuestions` senza evidenze (finisce in `Ungrounded`) e una voce mai citata: accettata (I-5) |

**RH-003 (T157): 2.500 istanze per variante del profilo `-3`**, seed 20261113, 0 istanze fuori schema per costruzione, golden pinnato e riverificato in processi separati. Conteggi (K = rifiuto del compositore, V = rifiuto di `CheckResult`/validatore; un'istanza può cadere in più categorie):

| Categoria | LeafA | LeafB | Reduce | Cross | Expand | Compare |
|---|---|---|---|---|---|---|
| accettate / rifiutate da K / rifiutate da V | 120 / 2224 / 156 | 373 / 1637 / 490 | 341 / 1700 / 459 | 462 / 1744 / 294 | 138 / 2276 / 86 | 81 / 2359 / 60 |
| K estremi coincidenti (candidati) | 609 | 610 | 850 | 902 | 248 | 336 |
| K candidato duplicato | 225 | 205 | 155 | 166 | 97 | 81 |
| K testo di soli spazi, `rationale` | 615 | 585 | 585 | 582 | 253 | 228 |
| K I-2 (`Open` senza motivo) | – | – | – | – | – | 254 |
| V I-1 | 64 | 248 | 248 | 213 | 49 | 38 |
| V descriptor oltre 512 o di soli spazi | 86 | 220 | 201 | – | – | – |
| K decodifica del `value` incoerente con `op` | – | – | – | – | 1484 | 1514 |

Altre categorie osservate (alias sconosciuto o fuori batch su candidati, item, patch; chiave e alias duplicati; patch incoerente; testi di soli spazi nelle patch) sono nel golden di `ReconcileSchemaPropertyTests`. Classi **fuori dall'elenco del task**, documentate come nuove incoerenze di caratterizzazione: `ItemShape` in LeafA (Declared senza Message 326, Documented senza Document 170, `assumptions` non Assumed 225, item senza evidenze 655), patch duplicata (37 Expand, 31 Compare) e la decodifica del `value` (C-4/C-5). `StageShape` non compare mai nel golden. Il tetto di 16 candidati nel profilo `-3` è imposto dalla grammatica; il compositore lo ripete solo con la membership (solo v10); in v6 17 candidati sono accettati.

**RH-004 (T158): conteggi fino al primo batch (`PrepareAsync(0)`), W=32768, O=16384, M=1024, ledger da 282 evidenze nello snapshot.** La v6 dà gli stessi totali per tipo (cambiano solo i confini delle foglie: v6 vuoti [154,128], v10 vuoti [150,132]).

| `DirectoryFacts` | Tipo | `/apply-template` | `/tokenize` | Totale | Quota |
|---|---|---:|---:|---:|---:|
| vuoti | foglia (Pack + 1 rimisura) | 284 | 568 | 852 | 60,1% |
| vuoti | pagina (Members 282, Keys 0) | 282 | 282 | 564 | **39,8%** |
| vuoti | altro (sonda del monitor) | 1 | 1 | 2 | 0,1% |
| vuoti | **totale** | **567** | **851** | **1.418** | 100% |
| deterministici | foglia (Pack + 1 rimisura) | 567 | 1.134 | 1.701 | 51,8% |
| deterministici | pagina (Members 521, Keys 269) | 790 | 790 | 1.580 | **48,1%** |
| deterministici | altro (sonda del monitor) | 1 | 1 | 2 | 0,1% |
| deterministici | **totale** | **1.358** | **1.925** | **3.283** | 100% |

Uguaglianza `tokenize` = 2·foglie + pagine + 2·rimisure: **vera** in tutte e quattro le configurazioni, sonda compresa. Le altre due: C-1. **Limiti:** tokenizer finto a 3,5 caratteri/token (foglie diverse da quelle reali: 138/143 contro 150/132) e fatti deterministici scritti come proxy per regex dalle tre forme di `LargeDocumentCorpus.BulkRule` (269 voci di chiave contro ≈ 700 reali): la quota delle pagine è quindi un minimo, con fatti reali ≈ 59% (Run 5). **Criterio O3** ([plan.md](plan.md) §12): quota ≥ 30% **soddisfatta** in entrambe le configurazioni; riconciliazione con la prova isolata entro 2 coppie di richieste, residuo non dimostrato. L'adozione di O3 resta una decisione fuori da M0.

**RH-005 (T159): costo dell'espansione**, motore puro di produzione (`ExpansionPolicy(16, 512, 1)`), ledger RH-004 con fatti deterministici, `MaxAiCallsPerRun` = 512. Struttura: foglie 151+131, 7 chiamate di gerarchia (4 foglie, 2 riduzioni, 1 cross), **5 candidati di gruppo** (due Set, due Node 151×131 con 8 blocchi ciascuno, un Cross 282×282 con 20 blocchi) = **52 blocchi**, indipendenti da *c*. Item delle foglie A con testo nei messaggi (v10 reale). Due politiche dei figli: *condivisi* (stesse coppie, la cache le riusa) e *disgiunti* (nessun riuso, costo massimo).

| *c* | Chiamate totali (condivisi) | `NotProcessed` (condivisi) | Esito (condivisi) | Chiamate totali (disgiunti) | `NotProcessed` (disgiunti) | Esito (disgiunti) |
|---:|---:|---|---|---:|---|---|
| 0 | 63 | 0 | Complete | 63 | 0 | Complete |
| 1 | 97 | 0 | Complete | 115 | 0 | Complete |
| 2 | 132 | 0 | Complete | 167 | 0 | Complete |
| 3 | 167 | 0 | Complete | 219 | 0 | Complete |
| 5 | 237 | 0 | Complete | 203 | 1 (`expansion_budget_exceeded`) | CoverageIncomplete |
| 16 | 275 | 5 (`expansion_budget_exceeded` 1, `expansion_saturated` 4) | CoverageIncomplete | 419 | 5 (`expansion_budget_exceeded` 2, `expansion_saturated` 3) | CoverageIncomplete |

Sensibilità senza item nei messaggi (`items-ref-1`): **22 blocchi** (4+4+4+4+6) invece di 52 (**×2,4**), nessun precheck rifiutato per nessun *c*; a *c* = 16 tutti e 5 i candidati saturano (`CoverageIncomplete`). Il precheck stima sempre blocchi × (1 + 16), indipendentemente da *c*: con *c* = 2 disgiunti la spesa effettiva è 160 contro 884 richieste preventivate (sovrastima ×5,5). Quattro candidati espandono lo stesso spazio di coppie 151×131. Inferenza dal codice, non misurata: un Cross con ≥ 30 blocchi non supererebbe il precheck neanche a *c* = 0. **Criterio O5** (precheck violato con ≤ 2 figli per blocco): **non soddisfatto** (rifiuti = 0 per *c* ≤ 3, con entrambe le politiche, con e senza item). Limiti: modello finto con selezione delle coppie scritta dal test; completezza = esito del round finale con zero candidati non terminali, senza assembly.

**Riesame di D2, D2b, O3 e O5 (Checkpoint C0).**
- **D2 (item delle foglie A dal server):** RH-002 casi 8–9 confermano I-4 e I-5 sul codice attuale. Il dato di RH-005 (blocchi di espansione ×2,4 con gli item testuali nei messaggi) è un motivo aggiuntivo, a favore del formato `items-ref-1`. Resta da verificare con RH-022 (M2, non autorizzato) che le fixture reali 022 (Run 4) e 024 (Run 5) mostrino i due blocchi latenti.
- **D2b:** nessuna evidenza M0 la smentisce; il rischio R-D2b ([plan.md](plan.md) §8) resta aperto e vincola la decisione. M0 non ha prodotto la disposizione `Open` per i candidati Gap↔evidenza degli archivi (RH-022(c)).
- **O3:** criterio d'ingresso soddisfatto numericamente (C-2, C-3). **O5:** non soddisfatto.
- **M1:** vanno corretti prima di RH-013 e RH-016 i punti C-4 e C-5.

### Evidenze M1 (2026-11-14; T160–T167; solo offline, nessuna inferenza, PostgreSQL non usato)

Commit locali (tutti senza coautori): annex `2a2666d` (C-4/C-5 in RH-011/013/016, prima dell'implementazione); RH-010 `c42e08d`, RH-011 `60bcb7f`, RH-012 `06453a9`, RH-013 `8512b30`, RH-014 `f93f0d3`, RH-015 `fe882ca`, RH-016 `ebff05e`, RH-017 `7527034`. La v11 è **inattiva** (`DocumentaryAnalysisFreeze` invariata) e in M1 **non è funzionante end-to-end**: le foglie A non producono item e la proiezione è di M2.

**Esito dei test** (suite completa, senza `BPROJECTS_TEST_POSTGRES`): prima di M1 4.700 test, 2.771 superati, 1.929 ignorati; dopo M1 **4.819 test, 2.888 superati, 1.931 ignorati, 0 falliti**. I 119 test nuovi sono 117 superati e 2 ignorati (`ReconcileDefinitionV11WorkflowTests`, richiedono PostgreSQL: **scritti, non eseguiti**; RH-010 resta aperto per questi). Nessun test di integrazione con database né con parser reale è stato eseguito: la suite **non** è dichiarata integrata completa.

**Revisione di RH-013 e RH-016 (C-4, C-5).**
- *C-4 recepita:* `Undecodable` = solo errore di deserializzazione (anche `op`/`value` incoerenti, rifiutati da `AiPatchConverter`); `PatchShape` = solo violazioni semantiche. Il ramo `default` del compositore resta difesa in profondità non raggiungibile da JSON (nessun test dedicato). Deviazione: decimo membro diagnostico `IntegrityMismatch` per il controllo di `canonicalHash` al replay (giudicato corretto dal revisore).
- *C-5 recepita:* nel profilo `by-stage-1` l'elemento di `patches` è un `anyOf` di tre rami con `op` = `enum` a un valore e `value` dell'operazione; solo keyword del sottoinsieme (`ProviderProblems` vuoto, nessun `const`). Non si lega `target` (item xor relation) né `minItems` dell'`evidence`: restano `PatchShape`. Le sei coppie `op`/`value` incrociate sono rifiutate dallo schema.
- *Baseline del 59–61% (RH-016):* spiegata e confrontata. Il `value` del `-3` ha tre rami indipendenti da `op`, quindi 1/3 di coerenza per patch; con 0–3 patch equiprobabili l'attesa teorica di almeno una patch incoerente è 63,0%; osservato 59,4% (Expand, 1.484/2.500) e 60,6% (Compare, 1.514/2.500), poco sotto per le repliche esatte e le proprietà facoltative. Con lo stesso seme il profilo `by-stage-1` dà **0** `Undecodable` in tutte le varianti.
- *Limite dichiarato:* i test provano l'equivalenza con lo schema valutato da `ReconcileSchemaSubset`, **non** con la grammatica reale di llama.cpp b10703 (ordine delle proprietà imposto dalla grammatica ma non dal test; resa dell'`anyOf` con `enum` a un valore). Resta la sonda RH-050 (M5) e il rischio di plan §8.

**Classi residue (RH-016, 2.500 istanze per variante, seed 20261113, istanze con la classe).** 0 rifiuti a valle del compositore (CheckResult V2, riferimenti, gerarchia) in tutte le varianti; `StageShape`, `ItemShape`, `Undecodable`, `IntegrityMismatch` mai presenti.

| Classe | LeafA | LeafB | Reduce | Cross | Expand | Compare |
|---|---:|---:|---:|---:|---:|---:|
| istanze accettate / rifiutate dal compositore | 643 / 1.857 | 643 / 1.857 | 599 / 1.901 | 756 / 1.744 | 235 / 2.265 | 621 / 1.879 |
| `UnknownAlias` | 1.038 | 1.038 | 1.038 | 1.028 | 1.632 | 1.062 |
| `CoincidentEndpoints` | 610 | 610 | 850 | 902 | 784 | 317 |
| `Duplicate` | 649 | 649 | 649 | 690 | 1.446 | 1.180 |
| `DuplicateCandidate` | 205 | 205 | 155 | 166 | 206 | – |
| `InvalidText` | 1.080 | 1.080 | 1.080 | 582 | 926 | 574 |
| `PatchShape` | – | – | – | – | 1.568 | 1.569 |

Il generatore è casuale e non rappresenta la frequenza reale (0 occorrenze reali di queste classi nelle 34 chiamate, 0 patch reali): dato per l'opzione O1, non un tasso. `PatchShape` è la classe più frequente nelle patch (target non esclusivo, evidenza mancante o diversa dagli estremi): legare `target` all'`op` ridurrebbe il residuo ma allarga la grammatica prima della sonda reale.

**Replay per batch (RH-017, 25 fixture non foglia A).**
- *v10 (caratterizzazione, coincide con l'assessment §3):* Run 5 030 rifiutata dalla validazione V1 (I-1); Run 4 029–033 e 034–035 accettate; Run 4 036 non decodificabile; Run 2 026 rifiutata dal compositore (item nelle foglie B, `StageShape`); Run 3 026 rifiutata dalla gerarchia (descriptor nullo); le altre 14 accettate (21 accettate in tutto).
- *v11 (output archiviati così come sono):* accettata **Run 5 030** e tutte le fixture ammesse dallo schema `by-stage-1` (17 su 25: Run 2 024–025, Run 3 024–025, Run 4 024–028 e 034–035, Run 5 025–030); **non generabili sotto v11** (8): Run 4 029–033 (candidati nel confronto), Run 4 036 (JSON troncato), Run 2 026 e Run 3 026 (item e descriptor nullo). Nessun output è stato trasformato.

**Correzioni a testi di M0 emerse in M1.**
- RH-012: l'attesa del lookup delle definizioni con alias è (`reconcile-alias-1`, validazione), non (`reconcile-1`, validazione) come scritto nel task; la v11 differisce dalla v10 solo per la versione di validazione (`reconcile-validation-v2`).
- Test M0 e storici adeguati solo meccanicamente (nessuna asserzione indebolita, nessun golden toccato): `ReconcileMeasurementBudgetTests` (ottavo argomento di `ReconcileAliasMode`), `ReconcileContractMatrixTests` (profilo `V11`, argomento `rules`, opzioni del compositore), `ReconcileSchemaProfileV9Tests` (stringa sorgente attesa della chiamata `InvokeAsync`), `ReconcileDefinitionV8Tests` e `ReconcileDefinitionV10Tests` (l'esempio di prompt ignoto passa da `document-reconcile-v6` a `-v7`, perché il v6 ora è supportato), `ReconcileSchemaInstanceGenerator` (gemelli con altra chiave per elementi in `anyOf`, sequenza storica invariata).

**Revisione indipendente del prompt v6 (RH-014).** Nessun rilievo bloccante; la struttura (sezione per stadio, tetto di 16 solo nell'Espansione, nessuna istruzione sugli item, Confronto senza candidati) e la sicurezza (materiale non fidato) sono confermate. Rilievi **non applicati** perché toccano righe che il task dichiara letterali dal v5 o aggiungono differenze oltre le sei approvate: richiedono una decisione e, se adottati, l'aggiornamento dello SHA pinnato e del test di diff (il testo si congela solo in RH-044).
1. *Importanti:* (a) il ruolo generale della riga 1 («individui collegamenti da verificare») contraddice il Confronto, che non individua collegamenti; (b) manca l'istruzione di applicare solo la sezione del tipo di batch ricevuto; (c) nelle foglie manca la protezione contro candidati deboli («solo plausibili, mai le coppie scartate») e «il compito è soltanto individuare candidati» contraddice il `descriptor`; (d) nel Confronto `Compared` («relazione già chiara») e `Open` («alternative incompatibili») si sovrappongono e `Open` è l'unico canale verso il PM; (e) il `reason` di `Open` è mostrato al PM ma il prompt impone etichette `E<n>`; (f) il tetto di 16 non dice cosa fare con più di 16 collegamenti plausibili; (g) le patch (riga letterale dal v5) non dicono che `AddRelation` vuole `target.item` e che l'`evidence` non è vuota per `AddSupport`/`ProposeAlternative`, né definiscono `classification` ora che il bullet `items` è sparito.
2. *Minori:* `noFindings` «facoltativo» vs sette proprietà; `[i3] (E3, E4)` vs una sola voce per item; paragrafo finale senza intestazione; `basis` non definito; terminologia (livello/tipo di batch/stadio, foglia/foglie, dubbio/conflitto/contrasto); ignorare richieste anche nel motivo del candidato in esame.

**Rischi residui e condizioni per M2.**
1. **D2b (regola da scrivere prima di M2):** serve una regola esplicita e test che provino, per una Gap estremo di un conflitto `Open`: domanda canonica ancora in `openQuestions`, provenienza conservata, nessuna decisione contestata presentata come acquisita, assenza di `gap_reference_missing`. Nessuna modifica di assembly o stadio Gap è stata fatta.
2. **RH-021 deve aggiungere** la regola «`items` non vuoti nella foglia A ⇒ `StageShape`» (qui non implementata, fuori da RH-013) e il formato `items-ref-1`: oggi `ReconcileAliasMessages.Build` stampa ancora il testo degli item mentre il prompt v6 dichiara «senza testo».
3. Il bundle `reconcile-stage-contract-1` del `configHash` nomina comportamenti di M2 (`leaf-projection-1`, `open-endpoints-as-alternatives-1`, `items-ref-1`) non ancora implementati: l'identità della v11 si congela solo in RH-044.
4. Il piano v11 usa la validazione V2 e il compositore con opzioni; la v11 reale non può completare finché mancano le foglie A (copertura incompleta, fallimento chiuso). Nessun percorso la congela in una nuova richiesta.
5. Grammatica reale non provata (`anyOf`/`enum` a un valore, ordine delle proprietà): sonda RH-050 in M5. I due test PostgreSQL di RH-010 vanno eseguiti con un database di test.
6. Decisione aperta sul prompt (punti 1 e 2 sopra) prima di RH-050.

### Chiusura M1 e gate M2 (2026-11-14; nessuna inferenza, nessuna modifica del codice di produzione)

**RH-010 / T160 chiuso.** Ambiente: container di test esistente `bprojects-test-pg` (immagine `pgvector/pgvector:pg18`, volume anonimo proprio, porta host 15432), avviato dopo Docker Desktop. Non è lo stack `bprojects`: i volumi `bprojects_pgdata` e `bprojects_documents` non sono stati toccati. Ogni classe di test crea un database `bprojects_test_<guid>` e lo elimina; i 7 database dello stesso tipo preesistenti sono rimasti (7 prima, 7 dopo, nessuna eliminazione). `BPROJECTS_TEST_DGX_ALLOW` e `BPROJECTS_TEST_PARSER_ENDPOINT` non impostate: nessuna inferenza, nessun parser reale. Al termine il container è stato fermato (stato iniziale); Docker Desktop è rimasto in esecuzione.
- `ReconcileDefinitionV11WorkflowTests`: **2 su 2 superati** (anche dopo l'asserzione aggiunta sul default). Single-call v11 uguale alla v6 (messaggi, istruzioni, schema di risposta; nessun batch derivato); oltre budget entrambe entrano in gerarchia con una foglia A, la v11 con le istruzioni v6 e lo schema LeafA di `by-stage-1` (`items.maxItems` 0); la definizione v11 è persistita nel run e riconosciuta dal workflow; la richiesta nativa dell'endpoint persiste la **v6** (default invariato).
- Suite completa con PostgreSQL: **4.819 test, 4.721 superati, 98 ignorati, 0 falliti** (22 min 27 s). Ignorati per motivo: 72 opt-in DGX mancante, 21 parser documentale non configurato, 2 traccia di accettazione V7 assente, 1 benchmark diagnostico (`BPROJECTS_BENCH`), 1 `BPROJECTS_REVIEW_OUT`, 1 symlink non consentito all'utente Windows. Nessun test v11 o di riconciliazione ignorato. **Non eseguiti**: i test con parser reale e con DGX. La suite è integrata completa per PostgreSQL e modello finto, **non** per parser reale né inferenza.

**`IntegrityMismatch`: confermato, nessuna modifica di codice.**
1. *Solo violazione effettiva dell'integrità canonica:* è costruito in un solo punto (`ReconcileAliasComposer.cs:149`), solo se `expectedCanonicalHash` è valorizzato e diverso dal canonico ricomposto. L'unico chiamante con un hash atteso è la ripresa di un WorkOutput Committed (`DocumentReconcilePlan.cs:559`); la composizione di una risposta nuova passa `null` (`:492`): non è raggiungibile da un output del modello.
2. *Non correggibile dal modello:* nel replay non c'è inferenza; il piano di riconciliazione non implementa `IGuidedRetryPlan` né `IInformedRetryPlan` e lancia `DocumentWorkException` semplici (non `GuidedRetryTrigger`), quindi il retry guidato del port non le intercetta; nessun codice di produzione legge `TypedViolations` o `ReconcileViolationKind` (diagnostica per i test). Vincolo per il futuro registrato in plan §12, O1 (h).
3. *Fail-closed:* `A_stored_output_whose_verified_hash_does_not_match_is_rejected_on_replay_without_a_new_inference` (PostgreSQL, superato): run `Failed`, codice `integrity_conflict`, nessuna richiesta al modello, nessuna revisione creata.
4. *v2–v10 invariate:* senza opzioni il compositore dà codice e messaggio storici (`ReconcileAliasComposerTests`, rifiuto per hash diverso, superato); l'unica aggiunta è l'etichetta tipizzata. Nessun nuovo retry, nessuna modifica di policy.

**Prompt v6.** Nessun difetto dimostrato ⇒ nessun rilievo applicato, in coerenza col mandato di chiusura («nessuna modifica di schemi o prompt della v11 senza un difetto dimostrato»). Il testo resta quello pinnato (SHA `2e68e7f2…`). I sette rilievi importanti restano ipotesi da verificare in RH-050; se compare contaminazione fra stadi si adotta l'opzione O2.

**D2b preparata, non implementata.** Regola candidata R-D2b-1: «Un'evidenza di tipo Gap deve conservare la propria domanda canonica in `openQuestions`, anche quando partecipa a un candidato con disposizione `Open`.» Piano (§2, §3, §8, §9, §11) e task (RH-020, RH-021, RH-041, RH-043) aggiornati; nessun file di `backend/src`, nessuna tabella di proiezione, nessuno schema e nessun prompt modificati. Mappa fra le condizioni richieste e i test di RH-021(b), da scrivere **prima** della modifica dell'assembly:

| Condizione richiesta | Test |
|---|---|
| Gap in un conflitto `Open` | (b)1, (b)2 |
| Gap in più conflitti | (b)3 |
| `MissingGaps` vuoto | asserito in (b)1–(b)4 e (b)11 |
| Provenienza conservata | (b)9 |
| Nessuna duplicazione non consentita da `EvidenceCoverage` | (b)8, (b)5 |
| Nessuna decisione contestata presentata come acquisita | (b)10, (b)5 |
| Coesistenza con patch accettate | (b)4 (`Applied`, `Duplicate`, `PreservedAsConflict`) |
| Compatibilità col contratto dello stadio Gap | (b)11, (b)12 |
| Difetto che l'eccezione evita (caratterizzazione) | (b)7, RH-022(c) |

**Incompatibilità architetturali (nessuna bloccante, tre da decidere o già corrette nei testi).**
1. *D2b nella forma originaria contro la precondizione dello stadio Gap:* senza eccezione un Gap estremo di un conflitto `Open` perde la domanda e `GapStageReferences.From` (che non legge le alternative, `GapStageInput.cs:92-110`) lo dichiara mancante ⇒ `gap_reference_missing` (`DocumentReconcilePlan.cs:864`). Risolta dall'eccezione R-D2b-1.
2. *L'eccezione crea una coppia identica:* per ogni Gap estremo esistono un item `openQuestions` e un'alternativa con testo, classificazione ed evidenze uguali. La `Coherence` di alias-2 (`AnalysisAliasComposer.cs:355-364`, §13.4) li rifiuterebbe nella single-call, ma non si applica alla gerarchia (`DocumentReconcileAssembly` e `DocumentAnalysisContent.Check` non la eseguono). L'avviso D3 `item_repeats_conflict_alternative` scatta quindi **sempre** per quel Gap (una volta per alternativa identica), come `SemanticRisk` non bloccante (`EvidenceCoverage.cs:827-830`). Il piano diceva «può restare»: corretto in «compare sempre». È una **deviazione consapevole** dal principio di §13.4, limitata ai Gap nella gerarchia v11. **Decisione D2b-3 dell'autore:** accettare l'avviso come informativo (raccomandato) oppure sopprimerlo con una modifica esplicita di `EvidenceCoverage` (non fatta).
3. *Incoerenze nei testi, corrette:* il piano §8 escludeva dalla D2b anche gli item `ToClarify` di `openQuestions`, mentre RH-021(b)6 li escludeva dalle sezioni ⇒ ora è la decisione **D2b-2** (raccomandato: esclusi; l'evidenza resta visibile come alternativa di un conflitto non risolto). Il criterio di accettazione 6 e RH-041 dicevano «avvisi D3 = 0» senza qualifica ⇒ ora valgono solo per le evidenze non Gap.
Il token `open-endpoints-as-alternatives-1` del `configHash` v11 identifica già la regola con l'eccezione (nessuna versione senza eccezione è mai stata implementata): nessun golden cambia; si congela in RH-044.

**Vincolo di intento.** Aggiunto a plan §1 e all'intestazione di M2: M2 non è un esercizio per rendere verdi i test di copertura; la proiezione è una rappresentazione intermedia e la sua completezza non dimostra la qualità della documentazione finale (livelli 7–8). `leaf-projection-1` non si modifica senza decisione esplicita e nuova versione.

**Condizioni per avviare M2.** (1) Conferma dell'autore di D2b-1, D2b-2 e D2b-3. (2) Test (b)1–(b)12 scritti per primi. (3) Mandato esplicito per RH-020…RH-022 (D11 vale solo per M0 e M1). (4) Resta il rischio della grammatica reale (sonda RH-050) e la decisione sul prompt prima di RH-050.

### Evidenze M2 (2026-11-14; T168–T170; PostgreSQL di test e modello finto, nessuna inferenza DGX, nessun parser reale)

**Perimetro.** Solo RH-020, RH-021, RH-022 (D11: T168–T170 nel registro ufficiale). La v11 **non** è attivata come default (`DocumentaryAnalysisFreeze` e `SourceSnapshotStore` non modificati); `specs/003-retrieval-evidence-search/**` non toccata; M3–M6 non avviate. Modifiche **non committate** (working tree): nessun commit autorizzato dal mandato.

**Decisioni dell'autore e lettura applicata.** D2b-1: un Gap conserva la domanda canonica in `openQuestions`, provenienza integra. D2b-2: `ToClarify` resta un elemento da chiarire (mai eliminato, mai requisito acquisito, mai risolto perché alternativa). **Lettura** (diversa dalla raccomandazione originaria «esclusi»): l'assembly non esclude mai un item della **sezione** `openQuestions`; l'esclusione D2b riguarda solo gli item delle altre sezioni (inclusa `assumptions`). Il tipo `Alternative` non è nominato dalla precisazione: è trattato come `ToClarify` perché la tabella lo colloca in `openQuestions` (test (b)6b, da invertire con una condizione se l'autore preferisce l'esclusione). Precisazione emersa dalla revisione indipendente: un'`Alternative` con certezza `Hypothesized` finisce in `assumptions` (la certezza prevale) e quindi è esclusa come ogni assunzione: la regola segue la sezione, non il tipo. D2b-3: solo transitorio; nessuna modifica di `EvidenceCoverage`, `GapStageReferences`, stadio Gap o validatore del contenuto; nessuna deduplicazione.

**Ordine eseguito (test per primi).** (1) registrazione D11; (2) test D2b scritti prima dell'assembly (`ReconcileD2bAssemblyTests`, 18 test: (b)1–(b)12 più (b)5b, (b)6b/c/d e le tre varianti di patch di (b)4); (3) proiezione, compositore, messaggi e **passo A** dell'assembly, cioè la D2b **senza eccezione** (come scritta in origine): **14 rossi su 18** — tutti per la domanda del Gap sparita da `openQuestions` (`Assert.IsType` su null in (b)1–(b)4, (b)6/6b, (b)8–(b)10) e, in (b)7/(b)11/(b)12, `MissingGaps = [E1]`/chiave assente; verdi i 4 test che non coinvolgono un item di `openQuestions` ((b)5, (b)5b, (b)6c, (b)6d). Il difetto dichiarato in plan §8 è dunque **osservato sul codice**, non solo letto; (4) **passo B**, eccezione R-D2b-1/D2b-2: **18/18 verdi**. Il negativo (b)7 documenta il difetto con una simulazione di test della D2b senza eccezione (nessuna versione senza eccezione è mai implementata in produzione): `MissingGaps = [Gap]`, quindi `gap_reference_missing` (`coverage_incomplete`) prima dello stadio Gap.

**Esito dei test.** Suite completa **con PostgreSQL di test** (container `bprojects-test-pg`, nessun DGX, nessun parser): **4.865 test, 4.767 superati, 98 ignorati, 0 falliti** (20 min 22 s). Prima di M2 (chiusura M1): 4.819 / 4.721 / 98 / 0: i **46 test nuovi** sono tutti superati e gli ignorati sono gli stessi 98 (72 opt-in DGX mancante, 21 parser documentale non configurato, 2 traccia V7 assente, 1 `BPROJECTS_REVIEW_OUT`, 1 benchmark/symlink della piattaforma; nessun test v11 o di riconciliazione ignorato). Ripartizione dei nuovi: `ReconcileD2bAssemblyTests` 18; `ReconcileLeafProjectionTests` 9 (tabella esaustiva 12×2×4 contro un'attesa indipendente, testo letterale, determinismo, distinzioni del prodotto, regole di contenuto); `ReconcileLeafAComposerV11Tests` 9 (proiezione nella foglia A, `items` del modello ⇒ `StageShape`, errore di configurazione senza evidenze, v10 invariata, formato `items-ref-1` e `Legacy` byte per byte); `ReconcileReplayLeafATests` 7 (RH-022); `DocumentReconcileAliasWorkflowV11Tests` 3 (PostgreSQL, MAF 1.26.0). **Non eseguiti:** test con DGX reale e con parser reale: la suite è integrata completa per PostgreSQL e modello finto, **non** per inferenza reale né parser.

**Finding D3 atteso (D2b-3), verificato.** L'avviso `item_repeats_conflict_alternative` **viene prodotto** nella v11 per ogni Gap estremo di un conflitto Open: una volta per ciascuna alternativa identica alla domanda (due conflitti ⇒ due avvisi), categoria `SemanticRisk`, `Blocking` falso, riferito alla domanda del Gap in `openQuestions`; nessun altro finding bloccante offline; `DocumentAnalysisContentRules` non rifiuta la coppia. Verificato su tre livelli: offline (b)8 e replay; endpoint di produzione `coverage-reports` sul run PostgreSQL (un solo avviso, per il Gap; nessuno per l'evidenza non Gap). Nel run PostgreSQL l'unico finding bloccante è `source_range_invalid` su ciascun documento: artefatto della fixture (documenti sintetici senza blocchi materializzati in `DocumentBlocks`, identico per la v10), non della v11. **Registrato come finding atteso e non bloccante nel caso specifico.** Nota: lo stesso avviso scatta per gli item `ToClarify`/`Alternative` conservati per la lettura di D2b-2 (stessa struttura: testo, classificazione ed evidenze identici alla alternativa); D2b-3 lo ha approvato per i Gap, quindi l'estensione a `ToClarify`/`Alternative` è una conseguenza della lettura di D2b-2 **da confermare**. La duplicazione resta un rischio **semantico** da verificare con documenti realistici (RH-050/RH-051) **prima** della promozione della v11 a default; i criteri storici di `EvidenceCoverage` non sono toccati.

**Vincolo fondamentale (conservato in documenti e test).** Un candidato che collega un Gap a un'evidenza potenzialmente risolutiva non dimostra una contraddizione fra requisiti: la valutazione della risposta al Gap è dello stadio Gap. L'assembly e la proiezione non la anticipano: il conflitto resta `Open` con `Resolution` nulla, nessuna alternativa coincide con un item di una sezione strutturale, una Decision estremo di un conflitto con un Gap non compare fra i requisiti (test (b)1, (b)10, (b)11; plan §1, §8, §11; `tasks.md` allegato, intestazione di M2). Riferimenti `i<n>`/`c<m>` dello stadio Gap stabili a ogni replay e `DocumentReconcileGapStage.Verify` risolve record con `openItem` e con `openConflict` senza modificare item, conflitti, mappa né `EvidenceRefs` ((b)11).

**Replay delle foglie A reali (RH-022).**

| Fixture | v10, output archiviato composto così com'è | v11, stesse voci proiettate |
|---|---|---|
| Run 5 024 (1 voce, Gap E4 del PM) | accettata; E4 resa come `requirements`, nessuna domanda la cita ⇒ `MissingGaps = [E4]` | 1 item (domanda di E4, testo letterale) ⇒ `MissingGaps` vuoto |
| Run 4 022 (140 voci, Gap E1 del PM) | accettata; nessun item cita E1 (e almeno una voce non è mappata) ⇒ `MissingGaps = [E1]` | 140 item, una domanda per E1 ⇒ `MissingGaps` vuoto, nessuna voce non coperta |
| Run 5 027 + candidato E255–E4 (`Contradicts`, proposto dall'output della foglia B) Open | — | con l'eccezione: domanda di E4 presente, `MissingGaps` vuoto, conflitto Open senza scelta; senza (simulata): `[E4]` |
| Run 4 029 (E4–E42) e Run 4 032 (E4–E254), `DependsOn`, Open | — | come sopra; un avviso D3 non bloccante per E4 |

**Test storici adeguati (3), perché RH-021 cambia esplicitamente il comportamento che asserivano.** Nessun golden, nessun test v2–v10 e nessuna asserzione sulle altre definizioni toccati. (1) `ReconcileViolationTypingTests` (tabella di etichettatura, profilo V11): le righe sugli item della foglia A attendono ora `StageShape` («gli item li compone il server dalle evidenze»), le stesse righe restano verificate sul profilo v10; (2) `ReconcileViolationTypingTests` (percorsi): il caso dell'item della foglia A si prova sulla v10; (3) `ReconcileStageContractV11Tests`: la foglia A v11 senza item produce una proposta per voce (il commento originale rimandava la proiezione a M2). Adattamento solo meccanico: `ReconcileBatchWorld.ComposeOptions` passa le evidenze del ledger al profilo V11.

**Correzioni a testi di piano/M1 emerse in M2.** (a) RH-022(c): la fixture Run 5 027 è una **foglia B**, non un confronto; il candidato E255–E4 è quello che il suo output propone; i confronti archiviati con un Gap estremo sono Run 4 029 (E4–E42) e Run 4 032 (E4–E254). (b) `ReconcileComposeOptions` richiede le evidenze del ledger nella foglia A con il contratto per stadio: senza è un errore di configurazione (`ArgumentException`), mai una proiezione vuota silenziosa; il piano le passa sempre (non raggiungibile in produzione, verificato dalla revisione). (c) Il run PostgreSQL usa solo Gap **documentali**: l'helper `NewDocument` crea solo evidenze documentali, quindi il Gap del PM è coperto offline ((b)1) e non nel run PostgreSQL. (d) (b)12 è offline come determinismo; la ripresa in un processo nuovo è nel test PostgreSQL (crash a metà gerarchia e durante lo stadio Gap: hash canonici già committati invariati, nessun WorkOutput né batch duplicato, nessun batch Committed reinviato, replay senza inferenza, stessa revisione).

**Revisione indipendente del codice di produzione (agente revisore, sola lettura).** Nessun difetto: nessuna regressione v2–v10 (formato `Legacy` identico, nuovi rami spenti senza opzioni/politica), `OpenEndpointItems` corretta (una proposta per evidenza, bersagli delle patch per `LocalKey`, fonti preservate tramite le alternative), proiezione funzione pura e `Rule` conforme alla tabella, l'eccezione di configurazione non raggiungibile in produzione. Osservazioni: (1) il commento sull'`Alternative` era troppo largo — **corretto** (la regola segue la sezione); (2) **da decidere dall'autore**: un'evidenza `Hypothesized`/`Inferred` estremo di un conflitto Open esce dalla sua sezione e rientra come alternativa classificata per origine (`Declared`/`Documented`), non `Assumed`/`Inferred` — è il comportamento di v6–v10 per le alternative, non modificato; (3) `Project`/`Rule` lanciano eccezioni non tipizzate (non raggiungibili oggi: le enum sono coperte dai test); (4) `ReconcileLeafProjection.Version` e `ReconcileAliasContracts.StageItemsPolicy` sono due literal separati: l'uguaglianza è asserita da un test; (5) da verificare in RH-041: la regola deterministica `open_conflict_as_decision` di `EvidenceCoverage` (§ item strutturali con evidenza in relazione aperta del ledger) non è evitabile dalla proiezione se il modello risolve quel candidato come `Compared`.

**Limiti dichiarati.** Il modello finto è scritto dal test: prova strutturale, **non** di conservazione semantica; la qualità degli item proiettati, dei descriptor e dei candidati si valuta solo ai livelli 7–8 con gold annotato prima. Nessuna sonda della grammatica reale (RH-050, M5), nessun task RH-041/RH-042/RH-043/RH-044 e nessuna promozione a default (RH-060). Il bundle `reconcile-stage-contract-1` nomina ora comportamenti implementati (`leaf-projection-1`, `open-endpoints-as-alternatives-1`, `items-ref-1`) ma l'identità della v11 si congela solo in RH-044; nessun golden è cambiato.

**Checkpoint C2.** Build verde; suite completa con 0 falliti (4.865 / 4.767 / 98 ignorati); nessuna `evidence_not_mapped` né `gap_reference_missing` sui ledger di test della v11 (offline e PostgreSQL); golden v2–v10 e profili `-1/-2/-3` invariati; v11 non default; 003 e DGX non toccati. **Decisioni aperte per l'autore:** (i) lettura di D2b-2 per il tipo `Alternative` e per l'estensione dell'avviso D3 a `ToClarify`/`Alternative`; (ii) classificazione delle alternative di evidenze `Hypothesized`/`Inferred` esclusi; (iii) avvio di M3 e/o dei task RH-041…RH-044 (non avviati).

### Evidenze M3 (2026-11-14; T171–T172; solo offline, modello finto, nessuna inferenza, nessun PostgreSQL necessario)

**RH-030 (P-1).** `ExtraTokenMemo` (chiave = impronta del profilo, modello, SHA-256 UTF-8 del testo) memoizza **solo** `ResponseSchemaJson` e `ToolsJson` nei due rami che li tokenizzano; il messaggio reso e i messaggi non si memoizzano mai; un conteggio fallito non entra nel memo. Il memo ha la vita del piano (`DocumentReconcilePlan._extraTokens`), mai globale. Gli altri chiamanti del preflight non lo passano e, senza memo, il comportamento è quello storico (`Without_a_memo_the_behaviour_is_the_historical_one`).
Equivalenza: 100 misure con 3 schemi ⇒ `Tokens`, `PayloadHash` e qualità identici con e senza memo; `/tokenize` 200 → 103 (−97 = misure − 3). Impronta diversa ⇒ nessun riuso. Sul ledger Single (v6 e v10, DirectoryFacts vuoti e deterministici) primo batch (`BatchId`, `InputHash`, `MeasuredTokens`, `InputIdsJson`), sequenze di Pack, `/apply-template` e `/tokenize` del testo reso e delle pagine sono identici; i `/tokenize` di schema si riducono agli schemi distinti. I golden di RH-004 restano la baseline «prima di P-1» (`MeasureAsync(..., memo: false)`, che rimuove il memo per riflessione) e non sono cambiati.

**RH-031 — richieste fino al primo batch (ledger Single, W=32768, O=16384, M=1024, DirectoryFacts vuoti).**

| Scenario | Def. | P-1 | `/apply-template` | `/tokenize` | Totale | `/tokenize` di schema |
|---|---|---|---:|---:|---:|---:|
| primo avvio | v10 | no | 567 | 851 | 1.418 | 284 |
| primo avvio | v10 | sì | 567 | 569 | 1.136 | 2 |
| primo avvio | v11 | sì | 567 | 569 | 1.136 | 2 |
| processo nuovo | v10 | no | 567 | 851 | 1.418 | 284 |
| processo nuovo | v10 | sì | 567 | 569 | 1.136 | 2 |
| processo nuovo | v11 | sì | 567 | 569 | 1.136 | 2 |

Riduzione −282 `/tokenize` (−19,9 % delle richieste di misura) = 284 misure con schema − 2 schemi distinti, come previsto dal criterio; v11 con P-1 (1.136) ≤ v10 senza P-1 (1.418). Nota onesta: «processo nuovo» ricostruisce rig, runtime e piano da zero, quindi costa come il primo avvio: il memo non è persistito (scelta di RH-030) e la ripresa non ne beneficia più di quanto faccia la prima misura. Le 567 `/apply-template` restano il costo dominante (O3, differita, non implementata).

### Evidenze M4 (2026-11-15; T173–T177; PostgreSQL di test e modello finto, nessuna inferenza DGX, nessun parser reale)

**Natura della prova.** Il modello è FINTO e scritto dal test: un workflow `Completed` dimostra affidabilità **strutturale** (gerarchia completa, provenienza integra, nessuna evidenza persa, ripresa deterministica), **non** la correttezza semantica del ragionamento generativo. Conflitti e Gap sono dichiarati dallo script; la loro conservazione con un modello reale si misura solo in M5 (RH-050/RH-051, non avviati). La v11 NON è validata semanticamente, NON è pronta al rilascio e NON è il default (le nuove richieste congelano la v6).

**RH-040 — fixture.** 169 evidenze (167 non Gap, 2 Gap), 26 fatti gold con frase esatta, 2 conflitti di versione, 2 Gap del PM; gold scritto prima del ledger. Densità ≈ 32,6 evidenze per pagina da 3.000 caratteri (assessment §8 verificato come ipotesi: densità alta). **Discrepanza del piano:** con il tokenizer caratteri/3,5 di RH-004 lo stadio Gap della fixture misura 21.810 token contro un budget di 15.360, quindi «160–190 evidenze» e «lo stadio Gap entra nel budget» non reggono insieme; per la v11 (senza recupero) un ledger Gap oltre il budget è un fail-fast tipizzato (`GapLedgerExceedsInput`). Nel rig PostgreSQL il modello finto conta PAROLE e lo stadio entra: i numeri di RH-041 sono quelli del rig, non del runtime reale. Budget e policy non sono stati cambiati.

**RH-041 — workflow (W=32768, O=16384, M=1024, tokenizer a parole).**

| Scenario | Ledger | Esito | Chiamate (per stadio) |
|---|---|---|---|
| A (gerarchia + stadio Gap, un Gap estremo di un conflitto Open) | 169 | Completed | 7: foglia 2, cross 1, confronto 3, Gap 1 |
| B (gerarchia senza Gap, ledger T040-single senza Gap) | 277 | Completed | 3: foglia 2, cross 1 |
| C (inflazione: 30 candidati per batch) | 169 | Completed | 34: foglia 2, cross 1, confronto 30, Gap 1 (≤ 512) |

Verificato in A: 3 conflitti `Open` senza risoluzione con le due alternative integrali (i 2 di versione e quello con il Gap PM-04 come estremo); ogni Gap ha la sua domanda canonica; `MissingGaps` vuoto (nessun `gap_reference_missing`, nessuna `evidence_not_mapped`); nessuna evidenza persa né proiettata due volte (eccetto il Gap estremo, R-D2b-1); un solo avviso D3 `item_repeats_conflict_alternative`, non bloccante, sulla domanda del Gap (le evidenze non Gap: 0); `ToClarify` e `Alternative` restano domande aperte e mai requisiti, `Hypothesized` ⇒ assunzione `Assumed`, `Inferred` ⇒ classificazione `Inferred`; WorkOutput = batch; replay da zero in un processo nuovo senza inferenza, stessa revisione. **Limite di copertura:** con questo rig la gerarchia non genera riduzione né espansione (il ledger entra in due foglie), quindi l'espansione con molti candidati NON è esercitata end-to-end; l'inflazione (C) esercita solo i confronti.

**RH-042 — fault injection (scenario A).** Crash a cinque confini (dopo la foglia A; prima del cross; prima del primo e dell'ultimo confronto; durante lo stadio Gap): tutti riprendono in un processo nuovo con `Completed`, senza reinviare batch durevoli, senza duplicati (WorkOutput = batch = 7), con output già durevoli invariati e analisi uguale a quella di un run pulito. Takeover con nuovo lease (fence 2): il proprietario vecchio non produce effetti; completa una sola revisione. Limite chiamate = 4: `Failed document_limit_exceeded`, 4 richieste, 4 batch durevoli, nessuna revisione (fail-closed). Scadenza a metà gerarchia: `CheckpointFencedException`, nessuna revisione, 2 batch durevoli. Non esercitato: crash «a metà espansione» (nessuna espansione nel rig).

**RH-043 — contratti.** `contracts/ai-workflow.md`: paragrafi «Emendamento v11» in §4.1, §4.3, §11, §13.7 (solo aggiunte, 25 righe; v2–v10 invariate), costanti verificate nel codice. **Divergenze note, non risolte (decisione dell'autore):** (1) §13.8 dice l'avviso D3 «spento per le definizioni ≠ v6» ma `EvidenceCoverage.AppliesStructuralRepetition` lo applica da v6 a v11; (2) commenti di `ReconcileAliasContracts.cs:211-217` ancora «non implementati»; (3) la specifica RH-043 dice «estremi non Gap solo come alternative» ma il codice aggiunge l'eccezione `ToClarify` (D2b-2); (4) la precondizione §13.7 è provata solo con modello finto.

**RH-044 — congelamento (`ReconcileV11FreezeTests`).** `configHash` v11 `2cbef06c…43fa`, SHA del piano `4d03ee2b…1f46`, SHA del prompt `document-reconcile-v6` `36efd347…9d29`, tabella `leaf-projection-1` (tutte le combinazioni tipo × origine × certezza) `4d1d19c8…a47b`, JSON canonico di `stageContract` `b69bab4e…47b7`, profilo `by-stage-1` `87d6e243…f0aa` (**sei** varianti di schema: LeafA, LeafB, Reduce, Cross, Expand, Compare; non sette). Dopo il congelamento ogni cambiamento della v11 richiede una definizione nuova.

**Suite completa con PostgreSQL di test (2026-11-15).** 4.897 test, 4.799 superati, 98 ignorati (invariati: DGX, parser e traccia non configurati), **0 falliti**; golden v2–v10 invariati. Il fallimento ambientale isolato di `RetrievalGapCampaignTests` di M2 non si è ripresentato. Il commit RH-020 (`096d567`) compila da solo (`bProjects.App`, esportato in cartella temporanea). **Non eseguiti:** test con parser reale, test DGX, campagna T040.

**Checkpoint C4.** Build verde, suite 0 falliti, v11 congelata e non default, 003 e DGX non toccati. **Non** equivale a validazione semantica: RH-050/RH-051 con modello reale restano obbligatori prima di RH-060.
