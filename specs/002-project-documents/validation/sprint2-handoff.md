# Sprint 2 — handoff operativo (Slice 002, I2 → I3 → I4/T067)

Non è un piano: la fonte dei task è `tasks.md`. Solo stato per riprendere. Nessun segreto.

## Stato
- Branch `feature/002-implementation`. I2 chiuso. **Milestone corrente: I3**; spuntati T033–T047, T118, T119. Aperto/prossimo: **T048** (campagna `DocumentRegimeConcurrencyTests.cs`, non esistente), poi T049–T055.
- T118/T047/T119 PASS: guard 19/19, CAS 34/34, dispatch/timer 8/8 (rosso osservato solo per T119: sweep era stub); 309 mirati + 169 PASS, 0 FAIL. Migration `20261102060756_DocumentRegimeTransition` (RegimeTransitions append-only, unique RootKind+RootId+ToRegime).
- Seam contatore: `RootCallCounterReservation` ora sotto guard con `maxCalls` opzionale (`document_limit_exceeded`); `maxCalls` NON ancora passato da DocumentOptions (manca DI produzione → T049/T050); `CallTimeout` esiste ma nessun percorso di inferenza lo usa ancora.
- Sweep: chiude ChatTurn/Run documentali scaduti anche con lease valido (fence++), Preparation e figli con `parent_deadline_exceeded`; root 001 intatti. Monitor separato dal ciclo di lavoro (`DeadlineSweepIntervalSeconds`=1). Due guard SQL grezze non unificate (ChatTurnEvents, selezione Analysis in EvidenceLedgerStore): equivalenti per i CHECK.
- T048 deve coprire: gare a 119.999/120/179.999/180/3600 con due worker e fence scaduto; retry dopo promozione a 110 e restart; preflight tardivo; limiti esatti chiamate/byte; finale al massimo contatore non fallisce; fixture A–G da verificare vs QS.
- Poi I3: T049–T055 (T055 richiede DGX; variabili BPROJECTS_TEST_DGX_* nell'ambiente), poi I4 (T056–T067, T120–T121, T123).
- Rosso iniziale non osservato per T037, T038, T042, T045, T047, T118. Una sola migration alla volta.
- Ambiente: `bprojects-002-test-pg` può risultare uscito: `docker start` + `pg_isready`.
- `docs/acceptance-trace/` non tracciato: non toccare.

## Decisioni I3
- OriginKind: DEFAULT 'Message' nel DB (CK impediscono ramo documentale nascosto). Key evidenza globale `unitOrdinal+key locale` (32 char). AnalysisContent 1.1 regole strutturali in T034; validatore di revisione = T060.
- Budget (AI/): `DocumentPreflight` (Resolve/Measure/Ensure), `CreateBatchAgent`/`RunBatchAsync` (sessione nuova, no history), errori `runtime_unverified`/`tokenizer_mismatch`. `PayloadCalibration` non ha origine di config in produzione: senza `/apply-template` il ramo documentale è Unavailable (scelta aperta per T046/T049). Ipotesi thinking incluso in max_tokens da validare su DGX in T055.
- ContentSecurityTests aggiornato (route documentali I2 = 47 protette; ParserClient unico HttpClient tipizzato).
- Flake osservato una volta: `MigrateAsync` parallelo (property-bag DocumentPreparation), non riprodotto in 5 rerun.

## Test / evidenze (non ripetere senza modifiche)
- I3 mirati: schema/validazione/migrazione Evidence 130+33+37 PASS; non-regressione 001 ampia 1193 PASS dopo fix ContentSecurity (67/67); budget 13/13; AI/Conversations/Inference/Checkpoint con PG PASS (solo MafReal/DGX ignorati).
- Backend mirati (classi Document*/ParsedManifest*/ParserClient*/SourceSelection*): 425 totali, 423 PASS, 2 SKIP (DGX), 0 FAIL. Parser container offline: 141 PASS. Baseline suite completa I1: 1337/1296/41/0.
- I2 gate: Vitest 289/289, build OK; E2E fake parsing 10/10, reale (PG+parser, LLM off) 6/6; I1 rieseguito fake 8/8 e reale 10/10. NOT VERIFIED: stato Ready su stack reale (arriva con I3), retry di Failed reale/rete persa (solo fake/DET). Bug corretto: compose non passava Parser__BaseUrl/Secret ad app.
- Aperti noti: crash `0xC0000409` di `ExecutionFailureTests` (usare `--crashdump`), deadlock `MigrateAsync` non riprodotto. MafReal/DGX: NOT VERIFIED.
- NOT VERIFIED parser: documenti reali/grandi, tabelle PDF TableFormer, limite 250 MB reale, solo linux/amd64, licenze non riviste legalmente.

## Decisioni
- 408 `upload_timeout` approvato. Backend→parser: header `X-Internal-Secret`; nomi manifest allineati al parser (vedi parser.md). Job «incompleto» (T116) = Completed con preparazione Incomplete.
- `{g}` delle route preparations accetta generazione, id o `latest` (http-api.md da allineare). `reason` delle issue esposto come dal parser (decisione aperta: testo fisso?).
- Agenti non committano; il coordinatore committa per percorsi espliciti, senza Co-Authored-By.

## Prossimo
I3: T033 (+T038) eseguibili subito.

## Comandi
- Backend (da `backend/`): `dotnet test --solution bProjects.sln` con `BPROJECTS_TEST_POSTGRES` (container `bprojects-002-test-pg`, porta 55432, credenziali da `docker inspect`), senza `BPROJECTS_TEST_DGX_*`. Parser reale: `BPROJECTS_TEST_PARSER_ENDPOINT`/`_SECRET`.
- Frontend: `npm --prefix frontend run test:ci`, `npm --prefix frontend run build`.
