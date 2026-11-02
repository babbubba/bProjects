# Sprint 2 — handoff operativo (Slice 002, I2 → I3 → I4/T067)

Non è un piano: la fonte dei task è `tasks.md`. Solo stato per riprendere. Nessun segreto.

## Stato
- Branch `feature/002-implementation`. I2 chiuso (vedi `I2.md`). **Milestone corrente: I3.**
- Completati e spuntati I3: T033–T041. Migrations `20261102012256_DocumentEvidence`, `20261102015659_DocumentWork` (tabelle append-only con trigger 23001; FK checkpoint per owner in SQL raw, non nello snapshot; `WorkManifest.SourceHash` = hash fonti congelate). T040/T041: 27 nuovi test + 275 non-regressione PASS. T042/T043 spuntati (grafo nativo `DocumentBatchWorkflow`/`Executors`, `IDocumentBatchPort` con fake; port di produzione su DocumentWorkStore/AgentFactory = T044/T049; ripresa dopo risposta salvata ma prima del commit rivalida la risposta del checkpoint senza reinferire — confermare in T044; rosso T042 non osservato, mutazione sul lookup di InvokeBatch → rosso). In corso: T045→T046 (agente regime, owner migrations). Prossimo: poi T044, T118, T047, T119, T048 (migration regime), T118, T047, T119, T048, T049–T055.
- Regola: una sola migration alla volta (un solo agente scrive in Persistence/Migrations).
- Rosso iniziale NON osservato per T037, T038 (test scritti dopo il codice). Dichiarato.
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
