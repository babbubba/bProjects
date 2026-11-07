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
