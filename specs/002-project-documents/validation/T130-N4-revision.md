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
