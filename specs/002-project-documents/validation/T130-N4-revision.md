# T130/N4 — Gate anticipato T138 (prova di fattibilità offline)

Data: 2026-11-07. Ambito: solo il gate di AI §13.4 / T138; nessuna implementazione v5 della gerarchia. SDK MAF 1.26.0 reale, PostgreSQL reale (container di test), modello **finto** con output controllati. Nessun DGX, nessuna inferenza reale: le prove dimostrano la struttura del grafo, non la qualità semantica di un LLM.

## Verdetto

**Il gate NON PASSA.** Il grafo esistente leaf/reduce/Cross/espansione/confronto consegna Gap e riscontro, collocati in porzioni diverse, simultaneamente e per intero a un batch solo in un caso particolare, che dipende da condizioni che il server non controlla. Il nominale non è garantito senza nuove chiamate o fasi. Come da AI §13.4 e T138 ci si ferma qui, senza introdurre fasi o chiamate.

## Percorso reale e batch osservati (prova positiva, caso ingegnerizzato)

Test: `DocumentReconcileGapGateTests.With_a_server_candidate_...` (run creato dal vero endpoint `Analizza`, definizione v4, 8 documenti). Gap = doc1 (soggetto `aaa`, kind Gap), riscontro = doc8 (soggetto `zzz`), chiave di scope condivisa «regola». Sequenza dei 13 invii (uno per batch, `CallCount` = 13 = WorkOutput Committed = richieste ricevute dal client simulato):

| # | batch | Gap+riscontro nel payload |
|---|---|---|
| 0-2 | leaf A (3) | no |
| 3 | reduce A | no (soli descriptor) |
| 4-6 | leaf B (3) | no |
| 7 | reduce B | no (soli descriptor) |
| 8 | cross | no (soli descriptor; inputHash 5713627b… costante) |
| 9 | expand A | **sì**, entrambi integrali |
| 10-11 | expand A | no |
| 12 | compare A | **sì**, esattamente i due originali |

Tipi di chiamata: 6 leaf, 2 reduce, 1 cross, 3 expand, 1 compare; nessuna fase nuova. Gli id di batch e gli input hash dipendono dal run (UUID): la traccia completa si rigenera con `BPROJECTS_GATE_TRACE_DIR` impostata (file `gate-trace.txt`: indice, stadio, vista, esito, batchId, inputHash). In un run: expand #9 batchId `a559e71c2458…`, inputHash `9134548f386c…`; compare #12 batchId `e3e6231fc79f…`, inputHash `f959414b6fce…`.

Verifiche sui payload effettivamente ricevuti (expand #9 e compare #12): enunciato e citazione integrali di entrambi, `tipo=Gap` per il Gap, ogni alias E/S risolto nel catalogo del run (`AnalysisAliasCatalog`) verso evidenza, versione di documento e citazione persistite; `inputHash` del batch = hash del payload misurato dal preflight (istruzioni, messaggio, schema); WorkOutput Committed con `InputHash` uguale, `catalog.entries` con entrambe le evidenze e `catalogHash`/`aiOutputHash`/`canonicalHash` presenti. Replay del run completato: nessuna nuova inferenza.

Ripresa: caduta del processo proprio nel compare (#12). Prima della caduta 12 WorkOutput Committed; alla ripresa il primo invio è lo **stesso payload** del batch caduto (stesso testo, stesso inputHash), nulla di già Committed è reinviato, un solo commit di revisione, replay successivo senza richieste.

## Limite tecnico dimostrato

Condizioni perché un batch riceva Gap e riscontro insieme (tutte necessarie):

1. Esiste un candidato: chiave letterale condivisa (`ReconciliationDirectory.SharedKeyCandidates`, r.204-251), relazione nota del ledger (`KnownRelations`), o proposta del modello. Riduzioni e Cross ricevono solo descriptor (`ReconciliationHierarchy.cs` r.74-112; `ReconcileAliasMessages.cs` r.58-63): il modello vi può citare solo alias di nodo, mai di evidenza.
2. Il manifesto di espansione (`ReconciliationCandidates.ExpandAsync`, r.243-294) è l'unione dei membri dei due estremi, ordinata per EvidenceId e **partizionata** da `PackLevelAsync` in batch isolati: Gap e riscontro devono cadere nello stesso batch per effetto dell'ordine degli id e del budget.
3. Solo se un'espansione li vede insieme e propone il figlio Evidence-Evidence nasce il confronto (`CompareAsync`, r.208-241), che riceve la sola coppia.

Prove di caratterizzazione (passano finché il limite esiste):

- `..._without_a_shared_literal_key_...`: stesso corpus senza chiave condivisa, modello che collegherebbe ovunque veda entrambi: 9 invii (leaf, reduce, cross), nessun expand/compare, **nessun payload con Gap+riscontro**. Il run è comunque Completed (la copertura attuale non richiede valutazioni): il grafo non segnala la valutazione mancante.
- `..._expand_manifest_is_partitioned_...`: chiave condivisa ma riscontro con id massimo: 3 expand, **nessuno** li vede insieme, nessun confronto.
- `..._a_Gap_with_two_findings_...`: Gap con due riscontri: il budget del rig mette due voci per batch di espansione; il secondo riscontro non è mai visto col Gap, un solo confronto, nessun batch con il Gap e entrambi i riscontri.

Nota sul layout: la prova positiva richiede ids scelti dal test (Gap rango 1, riscontro rango 4 in id order, adiacenti nel manifesto di espansione). In un run reale gli id sono UUID casuali.

## Perché il contratto approvato non è soddisfacibile con il grafo attuale

AI §13.4 richiede che una valutazione finale sia emessa dal batch che ha Gap e **tutti** i riscontri integrali, «mai chiusa dal server sul solo descriptor», con esattamente un record finale per Gap, senza nuove fasi/chiamate e senza arbitrato server. Il grafo attuale: (a) non ha alcun batch che per costruzione riceva gli originali di due foglie diverse (reduce/Cross solo descriptor); (b) l'unico batch che può riceverli, expand/compare, esiste solo con un candidato e un partizionamento favorevole, entrambi non controllati; (c) il confronto è per coppia, quindi un Gap con N riscontri verrebbe valutato in batch fratelli (nessuno antenato dell'altro) con supporti parziali, cioè record divergenti ⇒ `gap_assessment_unresolved`; (d) un Gap senza candidato non ha alcun batch valutatore e il run oggi risulta Completed. L'assenza di valutazione resterebbe quindi un FAIL strutturale, non un caso raro.

## Alternative tecniche (costi/impatti)

- A. Fase di valutazione dedicata dei Gap dopo la gerarchia (un batch per Gap, o per gruppo, con gli originali dei riscontri scelti dal server): garantisce il nominale ma è una nuova fase/chiamata LLM (vietata senza decisione), richiede di scegliere i riscontri (candidati per prossimità = ranking/top-k, escluso da AI §4) e consuma `MaxAiCallsPerRun`; impatto su manifest, WorkOutput, limiti e deadline.
- B. Rendere i Gap obbligazioni di confronto: ogni Gap diventa candidato del server verso l'intero resto del manifesto, con espansione/confronto già esistenti ma con i riscontri raccolti nello stesso batch (pack che mantiene insieme Gap e riscontri). Cambia il Pack e il costo (chiamate ~ numero di Gap × porzioni), nessun nuovo stadio; non garantisce comunque che tutti i riscontri entrino nel budget di un batch.
- C. Valutazione solo come proposta nel confronto esistente (Open/Compared per candidato) e Gap non coperti ⇒ ValidIncomplete esplicito (`gap_not_assessed`) invece di Completed: nessun costo, ma l'analisi gerarchica oltre contesto non completa mai i Gap non collegati: da accettare come limite dichiarato di I5 con N4 coperto solo dal percorso single-call.
- D. Limitare le valutazioni Gap al percorso single-call (già v5, T136) e dichiarare esplicitamente fuori perimetro la gerarchia oltre contesto per i Gap: nessun cambiamento al grafo; modifica di AI §13.4/T138 e di SC-004/I5.

## Decisione architetturale necessaria

Scegliere fra A, B, C, D (o variante) e aggiornare di conseguenza AI §13.4/T138. Senza questa scelta T138 non va implementato oltre il gate: nessuna nuova fase, chiamata, retry o aumento di limiti è stato introdotto.

## Esiti

| Prova | Esito |
|---|---|
| `DocumentReconcileGapGateTests` (5: positiva ingegnerizzata, ripresa nel compare, 3 caratterizzazioni del limite) | 5 PASS, 0 FAIL, 0 SKIP |
| Regressioni `DocumentReconcileAliasWorkflowTests` / `DocumentReconcileWorkflowTests` (helper `ReconcileRig.NewDocument` esteso con parametro opzionale `kind`) | 32 PASS / 11 PASS |
| DGX, MafReal, browser | NOT RUN |

Le prove di caratterizzazione sono etichettate `Characterization_LIMIT_*`: documentano l'impossibilità del nominale e si romperanno se il grafo cambierà; non sono una prova che il gate passi.

## Decisione successiva (aggiunta 2026-11-07, il report sopra resta invariato)

Il committente ha approvato come soluzione temporanea C + D, recepita in `contracts/ai-workflow.md` §13.6 e nel task T143: valutazione Gap solo single-call, oltre contesto con Gap fail-fast: run `Failed` con `document_limit_exceeded` e motivo `hierarchical_gap_assessment_unsupported`, zero chiamate LLM, senza revisione né WorkOutput. T138 resta aperto e bloccato in attesa di una nuova decisione tra A e B; il gate non è dichiarato superato.

## T137 — retry informato single-call v5 (aggiunto 2026-11-07)

Implementato in `AI/DocumentBatchPort.cs` (`IInformedRetryPlan`, misura del payload effettivo PRIMA di marker e riserva, ricontrollo di autorità/deadline dopo la misura), `DocumentAnalysisAliasPlan.cs` (trigger v5, messaggio con la prima risposta come stringa JSON delimitata non fidata, marker con `firstOutputHash`/`retryPayloadHash`/misura) e `ContextBudget.cs` (composizione unica del payload, condivisa con il controllo all'invio). Nuovo codice `context_budget_exceeded` (solo diagnostica di misura). v3/v4 (T130) e T129/T134 restano sul protocollo storico.

Prove (`DocumentAnalysisGuidedRetryTests.cs`, filtro `*V5_*`): rosso comportamentale prima del codice (19 su 50 falliti: nessun retry v5 inviato), poi 51/51 PASS con PostgreSQL + MAF reali e modello finto, nessun DGX. Coperti: protocollo positivo, delimitatori ostili, al limite/oltre il limite di un token, misura non disponibile, errori misti esclusi, secondo errore definitivo senza terzo invio, deadline, tetto chiamate, crash prima/dopo marker, riserva, invio, commit di output e revisione, concorrenza, replay senza inferenza. Il modello finto non dimostra la qualità semantica reale.

Unica modifica a un'asserzione T136 (`A_v5_run_with_a_gap_that_has_no_record...`): `Assert.Single(model.Requests)` diventa `Equal(2, ...)`, perché il commento originale la vincolava a «prima di T137»; il modello finto ripete l'output incompleto e l'esito (coverage_incomplete, nessuna revisione) è invariato.

## T143 — sicurezza del percorso oltre contesto v5, decisione C + D (aggiunto 2026-11-07)

Implementato in `AI/DocumentAnalysisWorkflow.cs` (`RunAliasGraphAsync`, `RunReconcileGraphAsync`, `ContractOf`, costante `ReconcileGapPolicyV1`) e `AI/DocumentWorkStore.cs` (costante `DocumentWorkException.HierarchicalGapAssessmentUnsupported`). Non toccati: Pack, `DocumentReconcilePlan`, `ReconciliationCompletion`, `DocumentReconcileAssembly`, `ReconcileAlias*`, T135/T136/T137.

**Prima (rosso comportamentale, `diagnostics-local/t143-red.log`)**: un v5 oltre contesto falliva con `document_limit_exceeded` e il solo messaggio «… la riconciliazione gerarchica v5 non è ancora disponibile (T138)», senza distinguere Gap/non Gap né motivo tipizzato; un v5 senza Gap non entrava mai nella gerarchia; `ContractOf` accettava un v5 con le sole chiavi single-call. 18 test su 74 falliti (fail-fast Gap, misura e confine, v5 senza Gap = v4, manifest/configHash, `workflow_incompatible` per chiavi gerarchiche/policy mancanti o diverse).

**Dopo**: `ContractOf` restituisce 5 solo con tutte le chiavi single-call v5 **e** `reconcile`/`reconcileAiSchema`/`reconcileAlgorithm`/`reconcileCatalog` della gerarchia v4 **e** `reconcileGapPolicy` = `hierarchical-gap-incomplete-1`. Con `!measure.Fits` e almeno un Gap nel catalogo congelato il run termina `Failed` (`document_limit_exceeded`, `ErrorMessage` che inizia con `hierarchical_gap_assessment_unsupported: ` e riporta token misurati, qualità della misura, budget di input e numero di Gap) prima di piano, port e `RunCallReservation`. Senza Gap entra nella gerarchia v4 con la definizione v5 nel manifest e un secondo catalogo con identità `alias-1`.

Prove (PostgreSQL + MAF reali, modello FINTO, nessun DGX, nessun MafReal):

| Verifica (tasks.md T143) | Test |
|---|---|
| 1. Gap oltre contesto: zero invii, nessun WorkOutput/batch/manifest/checkpoint/revisione, `CallCount` 0, motivo tipizzato in testa | `V5_above_context_with_a_gap_fails_fast_...`, `A_v5_run_with_gaps_and_a_regime_classification_...` |
| 2. Takeover, replay, run già Failed, crash fra misura e `FailAsync` (trigger di test) | `V5_fail_fast_is_deterministic_on_takeover_replay_and_a_crash_...` |
| 3. Senza Gap: stesse richieste, `inputHash`, batch, completezza e chiamate della v4 equivalente | `V5_above_context_without_a_gap_runs_the_v4_equivalent_hierarchy_...` |
| 4. Manifest/identità diversi, `configHash` del piano identico, niente `plan["gapPolicy"]`, nessun riuso di WorkOutput v4 | `V5_hierarchy_manifest_differs_from_v4_only_by_the_definition_...` |
| 5. `ContractOf`: chiavi gerarchiche mancanti, `document-reconcile-v4`/`reconcile-alias-2`/`global-reconcile-v2`, policy diversa ⇒ `workflow_incompatible` | `A_v5_run_without_the_full_hierarchical_versions_...` (DocumentAnalysisWorkflowTests), `A_v5_analysis_with_incomplete_...` (routing) |
| 6. v2/v3/v4 e single-call v5 invariati, v4 oltre contesto con Gap ancora Completed | `A_v4_run_above_context_with_a_gap_still_completes_as_before` + regressione T136/T137 |
| 7. Confine con prompt `document-analysis-v4` e schema `alias-2` misurati (la misura è quella riportata dal fail-fast): budget = token ⇒ single-call; token − 1 ⇒ fail-fast con Gap, gerarchia senza Gap | `V5_boundary_with_a_gap_...`, `V5_boundary_without_a_gap_...` |
| 8. Prefisso in testa e messaggio ≤ 1000 caratteri con 8 Gap su 8 evidenze | `V5_fail_fast_keeps_the_typed_reason_first_...` |
| 9. Nessun percorso porta un v5 con Gap a Completed o a una chiamata gerarchica | modello «tripwire» (lancia se invocato) in tutti i casi Gap |

Fixture: `V5Prompts` in `DocumentAnalysisAliasWorkflowTests.cs` riceve le chiavi gerarchiche v4 e `reconcileGapPolicy` (nessuna asserzione T136 cambia). `ReconcileRig.RunAsync` accetta un `contextWindow` opzionale per i test di confine.

Esiti completi: vedi la riga di chiusura sotto (suite non MafReal).

Esito T143: filtro mirato 74/74 PASS dopo il codice (18 rossi prima); suite completa non MafReal con PostgreSQL reale e modello finto: 3222 totali, 0 falliti, 3196 superati, 26 ignorati (opt-in DGX/tracce assenti: non eseguiti, non superati). DGX, MafReal e browser NOT RUN.

## T139 — coverage-3 con valutazioni Gap single-call v5 (2026-11-07)

Versione del report scelta dall'origine della revisione AI (`WorkManifest.AlgorithmVersion` + `WorkOutput.SchemaVersion`): `analysis-single-v3` con un solo WorkOutput `document-analysis-alias-2` ⇒ coverage-3; gerarchico `global-reconcile-v1`/`reconcile-alias-1`, storico o senza dati di lavoro ⇒ coverage-2; origini discordanti ⇒ 409 `integrity_conflict`, nessun report. `Find()` per `CheckerVersion` della versione scelta. Valutazioni lette dal WorkOutput committato e verificate (OutputHash, catalogHash, aiOutputHash, gapAssessmentsHash, forma, alias, punti aperti con id `canon-ids-1` nel DTO e nella revisione AI); incoerenza ⇒ 409 `work_output_invalid`. Nessuna tabella/migrazione; AnalysisContent 1.1.0 invariato.

Prove (PostgreSQL reale, nessun modello, nessun DGX/MafReal): `DocumentCoverageCheckerTests` (6 nuovi) e `DocumentCoverageEndpointTests` (11 nuovi/parametrizzati). Rosso comportamentale prima del codice: 18 falliti (coverage-2 restituito per v5, nessun 409 su hash manomesso). Dopo: filtro mirato 38/38; suite non MafReal 3241 totali, 0 falliti, 3215 superati, 26 ignorati (opt-in DGX/tracce assenti). Limite noto: `canonicalHash` del WorkOutput non è ricalcolato dalla proiezione (richiederebbe la ricomposizione dell'input completo). DGX, MafReal, browser NOT RUN.

## T140 — vista coverage-3 con valutazioni Gap (2026-11-07)

Frontend soli file esistenti (`evidence-api.service.ts` per i tipi, `evidence-view.component.ts/.html/.scss`): la valutazione Gap si legge **solo** se `checkerVersion === 'coverage-3'`, mai dalla presenza dei campi. Coverage-v1/coverage-2 (anche con `gapAssessments`/contatori ostili, anche vuoti) e un coverage-3 con campi mancanti o incoerenti (contatori non interi, lista assente, `assessedGaps` diverso dalla lista) mostrano «non disponibile: non significa che non ci siano Gap», mai «zero Gap». Coverage-3 mostra contatori (attesi, valutati, risolti nello snapshot, aperti, non valutati), esito in parole con simbolo (non solo colore), motivo (Risposta presente/parziale/ambigua, Nessuna risposta), motivazione come testo, riferimenti apribili (pulsanti che usano il comando di dettaglio evidenze esistente, focus sulla riga; fuori elenco: solo ID con nota), etichetta «Valutazione AI» distinta da supporto e conferma PM, nota sulla revisione AI originaria diversa dalla corrente, badge «Modifica del PM» per `gap_open_reference_removed_by_pm`. Nessun nuovo POST, editor o lifecycle: modifica/conferma non chiudono Gap o conflitto (dichiarato in UI).

Prove: Vitest rosso prima del codice (15 falliti su 38 nel file), poi `test:ci` 22 file / 378 test PASS, `build` PASS. Browser OFFLINE (Playwright, fake-stack con rotte evidenze/coverage simulate in uno spec temporaneo non committato; log e screenshot in `diagnostics-local/`): 4/4 PASS a 1440 e 390 (coverage-3 con tastiera e focus sulla riga, nessun overflow orizzontale, axe wcag2a/aa/21aa senza violazioni nella sezione, nessuna scrittura oltre il POST del report; coverage-2 ostile = non disponibile). LIMITI: dati simulati, non il backend T139 reale; E2E con stack reale+PostgreSQL non eseguito; nessun DGX.

## T141 — routing e freeze v5 per le nuove richieste Analizza (aggiunto 2026-11-07)

Implementato in `Documents/SourceSnapshotStore.cs` (`AnalysisDefinition` = `project-analysis-documents-v5`; `AnalysisPromptVersions()` con le dodici chiavi di AI §13.1 e §13.6: single-call v5 `document-analysis-v4`/`document-analysis-alias-2`/`analysis-single-v3`/`document-analysis-validation-v3`, gerarchia v4 invariata `document-reconcile-v3`/`reconcile-alias-1`/`global-reconcile-v1`/`reconcile-alias-catalog-1`, e `reconcileGapPolicy` = `hierarchical-gap-incomplete-1`), `AI/AnalysisWorkflow.cs` (la v5 va al workflow documentale accanto a v2/v3/v4) e `AI/DocumentAnalysisWorkflow.cs` (`ContractOf` v5 verifica anche `analysisSchema` = `1.1.0`, l'unica chiave del freeze v5 non ancora controllata; v3/v4 storiche restano invariate). Non toccati: `DocumentAnalysisAliasPlan`, `DocumentReconcilePlan`, Pack gerarchico, codice T135–T137/T139/T140/T143. Nessuna decisione single-call/gerarchico/Gap al POST (si decide a runtime da catalogo congelato e misura, T143). Nessun fallback semantico a v4 per nuove richieste: una v5 con una chiave ignota o mancante è `workflow_incompatible` senza invio. Frontend e contratto HTTP invariati (la definizione non è esposta nelle viste): `test:ci` non rieseguito, nulla è cambiato lì.

**Rosso comportamentale prima del codice** (`diagnostics-local/t141-red.log`, `t141-red2.log`): con il codice 4b13fcd e i test aggiornati, il POST reale congelava ancora la v4 e il dispatcher non instradava la v5. Falliti 13 su 107 nelle quattro suite richieste (routing, snapshot, small flow, limiti) e 5 su 7 fra i test di routing aggiunti dopo (single-call con Gap, retry informato + coverage-3 sul percorso instradato, retry ripetuto, nessun riuso del v4, freeze v5 completo, ottenuti rimuovendo temporaneamente le sole modifiche di `backend/src`); passavano già, come atteso, i due test di conservazione sul v3/v4 storici.

**Test negativi esistenti riesaminati (nessun indebolimento):**
- `DocumentWorkflowRoutingTests`, caso `project-analysis-documents-v5` + chiavi v3, e il teorema T143 `A_v5_analysis_with_incomplete_hierarchical_versions_or_another_gap_policy_...`: restano NEGATIVI (`workflow_incompatible`, zero invii). Prima lo erano anche perché la v5 non era instradata (cadevano nel fallthrough 001); ora li rifiuta `ContractOf`. Per provarlo hanno una nuova asserzione sul messaggio (contiene «documentale», presente solo nel guard del workflow documentale) e un gemello positivo con tutte le chiavi (POST reale).
- Nuovo teorema a 24 casi (12 chiavi × ignota/mancante) sul freeze completo: una sola chiave alterata ⇒ `workflow_incompatible`, nessun invio, nessun WorkOutput. I due casi su `analysisSchema` erano rossi (Completed al posto di Failed) finché `ContractOf` non l'ha controllata.
- Caso `project-analysis-documents-v4` + chiavi senza riconciliazione e definizione v6 con chiavi v5 complete: negativi invariati.

**Test aggiornati perché il POST reale ora produce v5 (fixture/aspettative; nessuna asserzione di sicurezza allentata):**
- `DocumentSourceSnapshotTests` (freeze v5 con elenco esatto delle chiavi); `DocumentSmallFlowTests` (grafo v5, definizione v5, coverage-3 con contatori Gap a 0 al posto di coverage-2: T139 sceglie la versione dall'origine della revisione AI single-call); `DocumentLimitsSnapshotTests` (modello finto single-call con DTO alias-2; run v5 con snapshot limiti e chiave Gap persistiti; aggiunto `project-analysis-documents-v5` ai casi del CHECK); `LargeDocumentHierarchyTests`/`LargeDocumentRealFlow`/`DocumentSmallFlowMafRealTests` (solo la definizione attesa v5; gold e fatti invariati; i due MafReal NON eseguiti).
- Le suite che caratterizzano il comportamento STORICO (alias-1: `DocumentAnalysisAliasWorkflowTests` e `DocumentAnalysisGuidedRetryTests`; gerarchia: `DocumentReconcileAliasWorkflowTests` v4 e `DocumentReconcileGapGateTests`) creano il run con il POST reale e lo riportano alle chiavi che l'endpoint congelava prima (`HistoricalAnalysisV4`, in `ReconcileRig.cs`): stesse asserzioni, nessun corpo delle prove del gate T138 modificato. Il test di freeze di `DocumentAnalysisAliasWorkflowTests` ora verifica il v5. In `DocumentReconcileAliasWorkflowTests` i test T143 non fanno più il pin v5: usano il run nativo del POST (con asserzione dell'identità esatta delle chiavi), incluse le prove di confine con prompt/schema v5 misurati.

**Prove nuove di T141** (PostgreSQL + SDK MAF reali, modello FINTO, dispatcher/endpoint reali, nessun DGX, nessun MafReal):

| Verifica (tasks.md T141) | Test |
|---|---|
| POST reale ⇒ run v5 con tutte le chiavi, snapshot limiti/config, nessuna chiamata al POST | `A_new_analyze_request_is_frozen_as_v5_...`, `Analyze_with_ready_documents_...` e `Analyze_with_only_ready_documents_...` (snapshot), `The_real_Analizza_POST_persists_the_snapshot_...` |
| (a) single-call v5 con Gap: una chiamata, WorkOutput alias-2, coverage-3 (1/1/0/1/0) | `A_routed_v5_run_with_a_gap_completes_in_one_call_and_exposes_coverage_3_...` |
| (a) senza Gap: lista vuota, Completed, coverage-3 a 0 | `Analyze_with_only_documents_runs_the_document_analysis_graph_and_completes`, `The_nominal_small_flow_...` |
| (b1) oltre contesto con Gap ⇒ fail-fast a zero invii, nessun WorkOutput/revisione | `V5_above_context_with_a_gap_fails_fast_...` (run nativo del POST), `V5_fail_fast_..._takeover_replay_...`, `A_v5_run_with_gaps_and_a_regime_classification_...` |
| (b2) oltre contesto senza Gap ⇒ come v4, coverage-2, manifest v5, `configHash` identico | `V5_above_context_without_a_gap_runs_the_v4_equivalent_...`, `V5_hierarchy_manifest_differs_...`, `LargeDocumentHierarchyTests` (gold) |
| (c) v2/v3/v4 storici invariati | `A_historical_v4_run_still_runs_...`, `A_historical_v3_run_...`, `A_run_frozen_with_v2_...`, `A_v4_run_above_context_with_a_gap_still_completes_as_before`, intere suite alias-1/gerarchia v4 |
| (d) confine appena dentro/appena oltre con prompt e schema v5 misurati | `V5_boundary_with_a_gap_...`, `V5_boundary_without_a_gap_...` (run nativi) |
| Integrazione T137 + T139 sul percorso instradato (dispatcher, POST reale) | `A_routed_v5_run_whose_first_output_omits_the_gap_assessment_gets_the_informed_retry_and_then_coverage_3_...` (2 invii, marker `analysis_retry`, un solo WorkOutput Committed, coverage-3 1/1/0), `A_routed_v5_run_whose_retry_still_omits_...` (un solo retry, mai Completed) |
| Lookup versionato/hash/replay: nessun riuso del v4 sulle stesse fonti | `A_v5_run_never_reuses_the_work_of_a_v4_run_...` (manifest `Hash` e `OutputHash` distinti; replay senza invii); ripresa transitoria con riavvio: `A_document_run_resumes_the_document_graph_after_a_transient_failure_and_an_application_restart` |
| Regressioni T129/T134/Chat/Genera | `DocumentWorkflowRoutingTests` (Chat v2/v3, Genera v2/v3, 001), suite intere |

**Esiti** (PostgreSQL 15432 reale, variabili DGX non usate, `BPROJECTS_TEST_DGX_ALLOW` mai impostata):
- suite mirata (routing, snapshot, small flow, limiti, alias, gerarchia alias, gate Gap, grandi documenti): 282 individuati, 281 PASS e 1 FAIL (nome di colonna sbagliato nel test nuovo sul non-riuso, corretto: `OutputHash`); poi routing 73/73 PASS. I filtri hanno sempre individuato test (nessun filtro vuoto contato come PASS).
- suite completa non MafReal (`diagnostics-local/t141-full2.log`): 3274 totali, 0 falliti, 3248 superati, 26 ignorati (gli stessi 26 del baseline T143: opt-in DGX, tracce V7, parser reale, symlink; non eseguiti, non superati). Prima esecuzione completa con i soli test aggiornati: 49 falliti, tutti test che dipendevano dal POST v4 (elencati sopra), poi sistemati.
- DGX, MafReal, browser: NOT RUN. Frontend: nessuna modifica, `test:ci` non rieseguito.

Limite dichiarato: il rosso comportamentale del single-call in `DocumentLimitsSnapshotTests` è assente (il modello finto v5 non discrimina l'endpoint); il rosso di quel file sta nell'aggiunta sul POST reale.

## T142 — prima esecuzione N4 reale su DGX (2026-11-08): FAIL, T142 NON chiuso

Test `DocumentSmallFlowMafRealTests.T142_N4_v5_gap_e2_answered_by_e3_and_open_conflict_e1_e3_with_provenance` (commit `17edc55`, una sola esecuzione, seriale; N4 originale invariato). Modello `Qwen3.6-35B-Smart` (router llama.cpp b10703), runtime n_ctx 131072, 1 slot libero, `enable_thinking=false` inviato per ogni fase (template Qwen che inserisce `<think></think>` vuoto), budget input analisi 113664, output analisi 16384. PostgreSQL 18.6 e parser reali. Durata del test 59 s; 7 invii al modello (3 estrazioni, 2 chat, 1 analisi, 1 bozza), nessun retry, nessun `finish=length`; analisi: prompt 3998 / completion 290 token, 6,7 s. Risultato: 55 PASS / 3 FAIL.

- **PASS**: run `project-analysis-documents-v5`; conflitto unico Open 45 Declared (messaggio PM) contro 30 Documented (scheda A), senza risoluzione né terza alternativa; citazioni e provenienze esatte; E2 assente dalle fonti; conferma e bozza con 30 e 45 e marcatori validi.
- **FAIL 1 (a monte dell'analisi)**: E2 non è mai diventata evidenza Gap. L'estrazione del messaggio «Cosa dice la scheda A sul termine di pagamento?» ha restituito `evidences: []` con motivo «domanda informativa». `extraction.md` definisce Gap solo come «informazione che il PM dice di non sapere o di non aver deciso» e non prevede domande; spec FR-007/008/015 e i test deterministici T136/T141 presuppongono invece un Gap da domanda. Di conseguenza `gapAssessments` è vuoto (0 attesi, 0 valutati) e la valutazione E2/E3 non è stata esercitata.
- **FAIL 2 (analisi)**: gli stessi valori 30 e 45 compaiono sia come voci ordinarie (`scopeIncluded`, `constraints`) sia come alternative del conflitto (4 voci, «come da documento»), contro FR-010/012 (alternative non duplicate nelle sezioni ordinarie). `findings` vuoto.
- **NOT VERIFIED**: valutazione Gap E2 `AnsweredInSnapshot`, supporto E3, retry informato v5 (non attivato: una sola chiamata di analisi valida), stabilità su tre N4, avversi.

Diagnostica grezza (richieste/risposte) solo locale, fuori da Git. Nessun gold, criterio o asserzione modificati; nessun nuovo invio DGX dopo il FAIL. Decisione necessaria prima di nuove inferenze: come E2 deve diventare un Gap (estrazione) e se il prompt v5 deve vietare la duplicazione nelle sezioni ordinarie.

## T142 — seconda esecuzione N4 reale su DGX dopo le correzioni (2026-11-08): FAIL, T142 NON chiuso

**Correzioni applicate prima della prova** (commit `9ac9e10` e `084a2dc`): `extraction-v3` (le domande esplicite del PM su informazioni, requisiti o decisioni di progetto diventano `Gap`, senza cercarne la risposta; saluti, richieste generiche e domande retoriche esclusi) con `extraction-v2` invariato byte per byte (hash fissato nei test) e scelta del prompt dalla configurazione congelata del lavoro; `document-analysis-v5` con la sola procedura di composizione in cinque passi, `document-analysis-v4` invariato e ancora valido per i run già congelati, le nuove richieste congelano v5. Diagnosi dell'output originale: i duplicati erano ripetizioni semantiche degli stessi due fatti con le stesse evidenze (`scopeIncluded` 30/E2 = alternativa 30; `constraints` 45/E1 = alternativa 45), non fatti distinti. Nessun validatore esteso: una riformulazione non è un duplicato oggettivo senza euristiche semantiche (caratterizzazione nel test `T142_a_reworded_repetition_...`).

**Verifiche offline prima del DGX** (modello finto: dimostrano versioni, struttura e validazione, NON il comportamento del DGX): nuovi `T142ExtractionPromptTests` e `T142AnalysisPromptTests` 21/21 PASS, 2 test di composizione T142 PASS (una sola alternativa 45 e una sola 30 nel conflitto Open, E2 `AnsweredInSnapshot`/`Answered`, nessun item ordinario con i valori contesi); DET 1798 PASS / 0 FAIL / 1769 IGNORED (Integration e MafReal senza ambiente); regressione mirata su Postgres reale (routing, snapshot, riconciliazione, estrazione, chat, evidenze, small flow, workflow analisi) 79 + 260 PASS / 0 FAIL dopo l'aggiornamento delle attese di freeze a v5; il caso DGX T142 è ancora scoperto. Suite T108 non ripetuta. Il test reale ora asserisce anche prompt di analisi `document-analysis-v5` e estrazioni `extraction-v3` (gold e criteri invariati).

**Prova reale** (una sola esecuzione, seriale, stesso profilo: `Qwen3.6-35B-Smart`, finestra 131072, `enable_thinking=false`, budget e parametri invariati): durata 3 min 30 s; 6 invii al modello (3 estrazioni 47,1/5,8/9,2 s, 2 chat, 1 analisi 52,9 s, prompt 4382 / completion 464), nessun `finish=length`, nessun retry. Risultato 17 PASS / 1 FAIL (il test si ferma al FAIL dell'analisi, quindi le verifiche successive non sono state eseguite).

- **PASS (correzione A)**: l'estrazione del messaggio E2 ha restituito un'unica evidenza `Gap` con citazione letterale della domanda («Cosa dice la scheda A sul termine di pagamento?»), `polarity Undefined`, `certainty ToClarify`; chat con risposta e citazione esatte; preparazione, estrazione e chat Ordinary entro i tempi.
- **FAIL (analisi)**: il run è `Failed work_output_invalid`. La risposta dell'analisi v5 valuta E2 come `Open`/`NoAnswer` con `supportingEvidence: []` e senza `openItem` né `openConflict` («nessuna evidenza fornisce il contenuto della scheda A»), mentre E3 (30 giorni) risponde: il validatore ha scartato il record (Open richiede esattamente uno fra openItem e openConflict). Non c'è stato retry informato: il guasto non rientra nell'unico retry tipizzato (assessment mancante), e non è stato provocato.
- **Duplicazione ancora presente, ma ridotta**: il valore 30 è ripetuto (`requirements` i1 con E3 e alternativa i2 con testo, classificazione ed evidenza identici); il 45 compare solo come alternativa del conflitto (i3, E1). Il conflitto ha due alternative, senza terza né risoluzione. Il run è stato scartato per la sola violazione sul record Gap: il duplicato identico non è stato oggetto di una violazione riportata.
- **NOT VERIFIED**: E2 `AnsweredInSnapshot`/`Answered` con E3 su un run valido, conflitto Open nel report di copertura, provenienze della revisione, retry informato, stabilità su tre N4, avversi.

Causa probabile (da non trattare con nuovi tentativi automatici): il modello non applica a E2 la regola di `gapAssessments` (rileggere tutte le altre evidenze) e non applica il passo 3 della procedura per il valore 30; il Gap ora esiste, quindi il difetto residuo è nella sola analisi v5 con il profilo corrente. Diagnostica grezza solo locale, fuori da Git. Nessun gold, criterio o asserzione modificati; nessun ulteriore invio DGX dopo il FAIL. Decisione necessaria prima di nuove inferenze: come far rispettare al modello la valutazione del Gap e l'esclusione dei duplicati (ulteriore modifica del prompt, ammissibilità di un controllo deterministico del duplicato identico già presente, o profilo di inferenza).

## T142 — terza esecuzione N4 reale su DGX con `document-analysis-v6` (2026-11-08, commit `aa76916`): FAIL, T142 NON chiuso

**Ipotesi verificata** (diagnosi della seconda prova): al modello arriva E3 con `origine=Document` ma senza il nome del documento; per questo ha scritto che nessuna evidenza fornisce il contenuto della «scheda A». Correzione: `document-analysis-v6` (v4 e v5 invariati, v5 fissato per hash nei test) con la regola sulle domande che nominano un documento e un esempio contro la duplicazione. Verifiche offline prima della prova: build 0 errori, suite backend completa con PostgreSQL reale 3571 totali, 10 falliti per le sole attese di freeze v5 nei test di riconciliazione (aggiornate a v6, 48/48 PASS dopo la correzione), 83 ignorati.

**Prova** (una sola esecuzione, seriale; `Qwen3.6-35B-Smart`, finestra 131072, parametri e gold invariati, parser `bprojects-rc-parser`): 60 PASS / 5 FAIL. Il run **non** è più `work_output_invalid`: l'analisi è valida e pubblicata (miglioramento rispetto alla seconda prova).

- **PASS**: estrazione di E2 come Gap; un solo conflitto 30/45 Open con due alternative e nessuna terza; nessuna risoluzione; provenienze delle alternative.
- **FAIL 1 (Gap)**: E2 valutato `Open`/`Ambiguous` con supporto E3+E1 e `openConflict` c1, invece di `AnsweredInSnapshot`/`Answered` con il solo E3. Il modello conta come «risposta» anche il messaggio del PM (E1), che non è il contenuto della scheda A. Conseguenze: supporto non solo documentale; punto/conflitto aperto collegato a E2.
- **FAIL 2 (duplicazione)**: `requirements` contiene i1 (30, E3, Documented) e i2 (45, E1, Declared), e il conflitto contiene le alternative i4 (30, E3) e i5 (45, E1): gli stessi due fatti con le stesse evidenze e la stessa classificazione, con testi diversi (per questo il controllo di testo identico passa). Inoltre `openQuestions` i3 cita E2 (il Gap compare fra le fonti dell'analisi).
- **Conclusione**: le istruzioni di procedura (v5) e l'esempio (v6) non fanno rispettare al profilo corrente il passo di esclusione dei duplicati. È una debolezza del modello con questo prompt, non una violazione sintattica: il validatore non la rileva.

**Opzioni (nessuna applicata, vedi decisione)**: (a) controllo deterministico *strutturale*: un elemento di `items` con lo stesso insieme di evidenze e la stessa classificazione di una alternativa di conflitto è un duplicato oggettivo → rifiuto del WorkOutput; da solo fa fallire il run salvo retry; (b) lo stesso rifiuto con il retry informato esteso (nuovo retry tipizzato, richiede decisione perché modifica il contratto T137); (c) esclusione deterministica dell'elemento duplicato in composizione (trasforma un output errato in uno valido: non applicata); (d) profilo di inferenza (thinking) — modifica del profilo congelato, non applicata.
