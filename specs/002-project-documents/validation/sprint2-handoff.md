# Sprint 2 — handoff operativo (Slice 002, I2 → I3 → I4/T067)

Non è un piano: la fonte dei task è `tasks.md`. Solo stato per riprendere. Nessun segreto.

## Stato
- Branch `feature/002-implementation`. Ultimo commit verificato: `1fdfa3f` (T029/T031/T116).
- Milestone corrente: **I2**. Completati e spuntati: T020–T029, T031, T116 (+ I1 e hardening).
- Aperti I2: T030 (manca prova per formato PDF/DOCX/MD/TXT sul frontend), T117, T032.
- In corso/non committato (sessione interrotta dalla quota): lavoro parziale dell'agente T117/T032 — modifiche a `frontend/src/app/projects/*` (api service, project-documents, passage spec), `infra/compose.yaml`, `infra/.env.example`, `tests/e2e/support/fake-stack.ts`, nuovi `tests/e2e/document-parsing.spec.ts`, `tests/e2e/support/fake-parsing.ts`. Stato effettivo NON verificato. Stack Docker `bprojects-i2-*` (db, parser) lasciato attivo: fermarlo a fine gate.
- `docs/acceptance-trace/` non tracciato: non toccare.

## Test / evidenze (non ripetere senza modifiche)
- Backend mirati (classi Document*/ParsedManifest*/ParserClient*/SourceSelection*): 425 totali, 423 PASS, 2 SKIP (DGX), 0 FAIL. Parser container offline: 141 PASS. Baseline suite completa I1: 1337/1296/41/0.
- E2E Playwright dopo hardening I1: NOT VERIFIED (parte del gate T032).
- Aperti noti: crash `0xC0000409` di `ExecutionFailureTests` (usare `--crashdump`), deadlock `MigrateAsync` non riprodotto. MafReal/DGX: NOT VERIFIED.
- NOT VERIFIED parser: documenti reali/grandi, tabelle PDF TableFormer, limite 250 MB reale, solo linux/amd64, licenze non riviste legalmente.

## Decisioni
- 408 `upload_timeout` approvato. Backend→parser: header `X-Internal-Secret`; nomi manifest allineati al parser (vedi parser.md). Job «incompleto» (T116) = Completed con preparazione Incomplete.
- `{g}` delle route preparations accetta generazione, id o `latest` (http-api.md da allineare). `reason` delle issue esposto come dal parser (decisione aperta: testo fisso?).
- Agenti non committano; il coordinatore committa per percorsi espliciti, senza Co-Authored-By.

## Prossimo
Completare T117 → prova per formato T030 → T032 (E2E reale+fake, `validation/I2.md`), poi I3 da T033.

## Comandi
- Backend (da `backend/`): `dotnet test --solution bProjects.sln` con `BPROJECTS_TEST_POSTGRES` (container `bprojects-002-test-pg`, porta 55432, credenziali da `docker inspect`), senza `BPROJECTS_TEST_DGX_*`. Parser reale: `BPROJECTS_TEST_PARSER_ENDPOINT`/`_SECRET`.
- Frontend: `npm --prefix frontend run test:ci`, `npm --prefix frontend run build`.
