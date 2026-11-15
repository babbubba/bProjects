# Handoff — Reconciliation Hardening M3 + M4 (sessione interrotta il 2026-11-14)

> **SUPERATO il 2026-11-15:** M3 e M4 sono completate e committate (T171–T177); lo stato, i numeri e le divergenze aperte sono in «Evidenze M3» e «Evidenze M4» di `specs/002-project-documents/validation/reconciliation-hardening/assessment.md`. Questo handoff resta come storico della sessione interrotta.

Branch `feature/003-retrieval-evidence-search`, worktree `C:\source\repos\babbubba\bProjects`. Nessun push, merge o rebase. v11 NON è default. 003 e DGX non toccati. Nessun test DGX/T040.

## Stato

| Task | Stato | Note |
|---|---|---|
| RH-030 (T171) | FATTO, committato `dde9020` | `ExtraTokenMemo` in `AI/ContextBudget.cs`, `_extraTokens` in `DocumentReconcilePlan` (5 chiamate), `DocumentPreflightMemoTests` (10 test verdi) |
| RH-031 (T172) | FATTO, committato `dde9020` | tabella in «Evidenze M3» di `assessment.md`: v10 senza P-1 1.418 richieste, con P-1 1.136, v11 con P-1 1.136 (−282 `/tokenize` = 284 misure con schema − 2 distinti) |
| Docs M3 | committati `392c9b7` | registro ufficiale T171–T172 in `specs/002-project-documents/tasks.md`, evidenze M3, RH-030/031 spuntati nel `tasks.md` RH |
| RH-040 | scritto, NON committato, test 5/5 verdi (secondo l'agente) | `backend/tests/bProjects.IntegrationTests/Reconciliation/Representative/` (7 file). 169 evidenze, 26 fatti gold, 2 conflitti di versione, 2 Gap PM |
| RH-041 | scritto, NON compilato né eseguito | `DocumentReconcileV11FlowTests.cs` (scenari A, B, C); modifica a `ReconcileRig.RunAsync` (parametro opzionale `safetyMarginTokens`, default 20) |
| RH-042 | NON iniziato | |
| RH-043 | BOZZA scritta, NON committata | `specs/002-project-documents/contracts/ai-workflow.md` (+25 righe, solo aggiunte, etichetta «BOZZA RH-043» da togliere) |
| RH-044 | NON iniziato | |

Modifiche non committate: `ReconcileRig.cs`, `ai-workflow.md`, `DocumentReconcileV11FlowTests.cs`, cartella `Representative/`. I file `benchmarks/` e `docs/roadmap/*HANDOFF.md` preesistenti restano non tracciati e fuori dai commit.

## Decisioni e scoperte da ricordare

1. **Gap stage e budget (RH-040).** Con il tokenizer del runtime di RH-004 (caratteri/3,5) lo stadio Gap della fixture da 169 evidenze misura 21.810 token contro 15.360 di budget: le due condizioni di RH-040 («160–190 evidenze» e «lo stadio Gap entra») non reggono insieme. Per la v11 (senza recupero) un ledger Gap oltre il budget è un fail-fast `GapLedgerExceedsInput` (`DocumentAnalysisWorkflow.cs` ~riga 947): è comportamento corretto e va riportato. Il modello finto di `ReconcileRig` conta PAROLE, quindi nei test PostgreSQL il ledger intero potrebbe entrare: NON verificato. Se non entra, scenario A va ridotto a un sottoinsieme del ledger (tutti i fatti del gold, estremi dei conflitti, i 2 Gap, righe multi-fonte) senza cambiare budget né policy, e il caso completo va asserito come fail-closed tipizzato. Dichiarare comunque la differenza di tokenizer nel rapporto.
2. **Memo P-1**: «processo nuovo» costa come il primo avvio (memo non persistito, per scelta di RH-030). I golden di RH-004 restano la baseline: `MeasureAsync(..., memo: false)` rimuove il memo per riflessione.
3. **Ambiguità della bozza RH-043** (riportate dall'agente): commenti obsoleti in `ReconcileAliasContracts.cs:211-217` («non ancora implementato»); §13.8 dice avviso D3 «spento per definizioni ≠ v6» mentre il codice lo applica da v6 a v11 (`EvidenceCoverage.AppliesStructuralRepetition`); la spec dice «estremi non Gap solo alternative» ma il codice aggiunge l'eccezione `ToClarify` (D2b-2); la precondizione §13.7 è provata solo con modello finto.
4. Convenzione T-ID: T173 = RH-040, T174 = RH-041, T175 = RH-042, T176 = RH-043, T177 = RH-044 (da registrare in `specs/002-project-documents/tasks.md` in coda, blocco «Delta Reconciliation Hardening M4», come per M3; spuntare RH-04x in `validation/reconciliation-hardening/tasks.md`; aggiungere «Evidenze M4» in `assessment.md` §12).
5. Nessun `Co-Authored-By` nei commit (CLAUDE.md di progetto prevale). Commit atomici, Conventional Commits con riferimento a RH/T.
6. Hook GateGuard: ogni primo tocco di un file richiede di enunciare i fatti e riprovare; non raggruppare in parallelo i primi tocchi (un edit bloccato in un batch lascia gli altri applicati).

## Prossimi passi (in ordine)

1. `cd backend; dotnet build bProjects.sln --no-restore -v q` e correggere gli errori di compilazione di `DocumentReconcileV11FlowTests.cs` (mai compilato; possibili: firme di `LoadContextAsync`, `ClaimNextWorkAsync`, proprietà di `ExtractedEvidence`, `EnqueueAsync`, uso di `Message`/`Count`/`Execute`). Il file replica `EvidenceTestSeed.CompleteAsync` e `NewRetrievalDocument` di `RetrievalGapFlowTests`.
2. Avviare PostgreSQL di test (vedi memoria `reference-local-postgres-tests`: `docker start bprojects-test-pg`, variabile `BPROJECTS_TEST_POSTGRES` costruita senza stampare la password; MAI `BPROJECTS_TEST_DGX_ALLOW`). Eseguire `--filter-class "*DocumentReconcileV11FlowTests"` e iterare: formato reale del messaggio di confronto (lo script dice `Open` se gli enunciati dei due estremi sono nel testo), estremi del conflitto nella foglia B/cross/espansione, presenza di `source_range_invalid` (artefatti di fixture dei blocchi), eventuali fallimenti della precondizione Gap. STOP di RH-041: se un ramo atteso non si raggiunge, segnalare senza cambiare budget né policy.
3. Eseguire anche `RepresentativeFixtureTests` e il gruppo offline; commit RH-040 (fixture), poi RH-041.
4. RH-042 (estensione di RH-041): crash dopo il commit di una foglia A, a metà espansione, prima del commit di un confronto; takeover con nuovo lease; replay senza inferenza; deadline a metà gerarchia; limite chiamate esaurito ⇒ fail-closed; nessun effetto duplicato. Modello per i crash: `FakeChatModel.Responder` che lancia alla N-esima richiesta (vedi `A_crash_in_the_middle_of_the_hierarchy…` in `DocumentReconcileAliasWorkflowV11Tests.cs`); limite chiamate via `DocumentOptions.MaxAiCallsPerRun`.
5. Rileggere la bozza RH-043 contro codice e golden, togliere «BOZZA», decidere le divergenze del punto 3 sopra, commit.
6. RH-044 solo dopo i gate offline: pinnare golden di `configHash` v11 (rig di `RetrievalGapFlowTests`), hash del profilo `by-stage-1`, SHA del prompt v6, tabella `leaf-projection-1` (JSON canonico), JSON canonico di `stageContract`. Poi suite completa con PostgreSQL (~22–25 min, in background, leggere il risultato una volta), verificare golden v2–v10 invariati, registrare i test non eseguibili (parser reale) come «non eseguiti».
7. Rapporto finale (10 punti del mandato) e GO/NO-GO per M5. Non avviare M5/M6. Non dichiarare la v11 validata semanticamente: RH-050/051 con modello reale restano obbligatori; «Completed» col modello finto è prova strutturale, non semantica.
8. Non eseguita: verifica che il commit RH-020 (`096d567`) compili da solo; da registrare come dipendenza fra commit se non compila. Il fallimento ambientale isolato di `RetrievalGapCampaignTests` va riportato se si ripresenta.

## Test eseguiti finora

`DocumentPreflightMemoTests` 10/10; `ReconcileMeasurementBudgetTests`, `ReconcileDefinitionV11PlanTests`, `ReconcileExpansionCostSimulationTests`, `DocumentContextBudgetTests` 66/66 (build verde dopo RH-030). Suite completa NON rieseguita; PostgreSQL non usato in questa sessione.
