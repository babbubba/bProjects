# Sprint 2 — handoff operativo (Slice 002, I2 → I3 → I4/T067)

Non è un piano: la fonte dei task è `tasks.md`. Solo stato per riprendere. Nessun segreto.

## Stato
- Branch `feature/002-implementation`. **I2 chiuso** (T020–T032, T116–T117 spuntati; evidenza in `validation/I2.md`, `parser.md`).
- Milestone corrente: **I3** (T033–T055, T118–T119). Nessun task I3 ancora iniziato.
- Piano esecuzione I3: catena Evidence/migrazioni serializzata (T033→T037, poi T040) e ContextBudget (T038→T039) in parallelo; poi MAF/B1/guard.
- `docs/acceptance-trace/` non tracciato: non toccare. Stack Docker I2 fermato.

## Test / evidenze (non ripetere senza modifiche)
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
