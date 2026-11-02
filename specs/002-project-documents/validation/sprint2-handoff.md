# Sprint 2 — handoff operativo (Slice 002, I2 → I3 → I4/T067)

Non è un piano: la fonte dei task è `tasks.md`. Solo stato per riprendere. Nessun segreto.

## Stato (sessione sospesa su richiesta dell'utente)
- Branch `feature/002-implementation`. I2 chiuso. **Milestone corrente: I3** (T033–T046 spuntati; ultimo commit verificato `410673e`, poi un commit WIP di checkpoint sotto).
- **WIP NON VERIFICATO (T118/T047/T119 aperti)**: agente interrotto a metà. Nel checkpoint WIP: `AI/DocumentDeadlineGuard.cs`, `AI/DocumentRegimeTransitions.cs`, modifiche a AiDispatchService/ExecutionOptions/DocumentWorkStore/PostgresCheckpointStore/ExecutionLeaseStore/DocumentJobStore/committer (AnalysisRunCompletion, DocumentRunCompletion, ChatTurnCompletion), migration `20261102060756_DocumentRegimeTransition` (+ snapshot), test `DocumentDeadlineGuardTests`, `DocumentDeadlineDispatchTests`, `DocumentPromotionTransitionTests`, `DocumentRegimeRoots`. La build compila (0 errori); i test NON sono stati eseguiti/verificati; non so quali tre task siano a che punto (T118 probabilmente più avanti, T119 probabilmente appena iniziato). Prima azione alla ripresa: eseguire quei 3 file di test mirati (PG), leggere il diff, completare nell'ordine T118→T047→T119 senza duplicare file/migration (riusare la migration esistente se coerente; se va rifatta, non toccare quelle storiche).
- Seam temporaneo: `IBatchCallReservation`/`RootCallCounterReservation` (T044) da sostituire con la CAS del root (T047/T118).
- Dopo: T048 (campagna concorrenza), T049–T055 (agente estrazione, dispatcher ExtractUnit, aggregazione ledger, endpoint Evidence, ecc.), poi I4 (T056–T067, T120–T121, T123). T055/T067 richiedono DGX (variabili BPROJECTS_TEST_DGX_* presenti nell'ambiente: usarle solo per T055/T067/MafReal).
- Rosso iniziale non osservato per T037, T038, T042, T045 (dichiarato). Regola: una sola migration alla volta, un solo agente su Persistence/Migrations.
- Ambiente: `bprojects-002-test-pg` (porta 55432) può essere uscito: `docker start` e attendere `pg_isready`. Stack Docker I2 fermato.
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
