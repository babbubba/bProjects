# Sprint 2 — handoff operativo (Slice 002, I2 → I3 → I4/T067)

Non è un piano: la fonte dei task è `tasks.md`. Contiene solo lo stato per riprendere il lavoro. Nessun segreto.

## Stato
- Branch: `feature/002-implementation`; ultimo commit all'avvio: `774755e` (fine hardening I1).
- Milestone corrente: **I2** (T020–T032, T116–T117). Ultimo task completato: T019 (I1) + hardening I1.
- Completati e verificati (fake HTTP, PG reale, test mirati): T027, T028 (migration `20261101223202_DocumentParsing`). T029 implementato, test con parser fake PASS (20), ma **resta aperto**: manca verifica con parser reale (NOT VERIFIED).
- T030 implementato (API passages 21/21 PG, Vitest 30/30, build OK) ma **aperto**: manca prova per formato PDF/DOCX/MD/TXT (con T031/T032). `{g}` accetta generazione, id preparazione o `latest` (contratto http-api.md da allineare). `reason` issue esposto come dal parser (decisione aperta: testo fisso?).
- Completati: T020–T026 (parser: 141 test in container offline, vedi validation/parser.md). NOT VERIFIED: prestazioni/documenti reali grandi, tabelle PDF TableFormer, limite 250 MB reale, solo linux/amd64.
- **MISMATCH da risolvere (prima di T031)**: il parser usa header `X-Internal-Secret`, il backend `Authorization: Bearer`; verificare anche tutti i nomi campi JSON del manifest (validatore backend vs parser reale). Compose: servizio `parser` con profilo `parser`, rete interna `parser_net` anche su `app`.
- In corso: allineamento backend↔parser (T031). Modifiche non committate: solo `docs/acceptance-trace/` (da non toccare né committare).
- Prossimo task eseguibile: T020 e T021 in parallelo (parser Python, cartella `parser/`); T027 dopo T022.

## Ambiente (verificato all'avvio)
- .NET 10.0.401, Node 24.22.1, Docker 29.7.2, Python **3.13** locale (il parser richiede 3.12 + Docling 2.129.0 → build/test nel container).
- PostgreSQL di test: container `bprojects-002-test-pg`, porta host 55432 (credenziali: ispezionare l'env del container, non scriverle qui).
- Nell'ambiente sono impostate `BPROJECTS_TEST_DGX_*`: per le suite non-DGX **disimpostarle** nel processo di test.
- Cartella `parser/` non esiste ancora; fixture in `tests/fixtures/documents/` presenti (T002).

## Test / evidenze
- Baseline backend (fine hardening I1, 2026-11-01): 1337 totali, 1296 PASS, 41 SKIP, 0 FAIL. Frontend 250/250, build OK. **Non ripetere** senza modifiche al codice.
- E2E Playwright non rieseguiti dopo hardening I1 (rinviati al gate I2/T032).
- Aperti noti: crash `0xC0000409` di `ExecutionFailureTests` (usare `--crashdump` se riappare), deadlock `MigrateAsync` non riprodotto.
- MafReal/DGX: NOT VERIFIED.

## Decisioni
- 408 `upload_timeout` approvato nel contratto (I1).
- Gli agenti non committano: il coordinatore committa per percorsi espliciti, senza `Co-Authored-By`.

## Contratto JSON inferito dal backend (da allineare nel parser)
`document.serializationVersion`, `offsetUnit="unicode-codepoint"`, `issues[].id` + `inventory[].issueRefs`, `ranges[].textHash` (SHA-256 hex UTF-8), `blocks[].locator`, `blocks[].table.cells`. Auth: `Authorization: Bearer` con `Parser:Secret`; env `Parser__BaseUrl`. Form: declaredFormat, documentVersionId, originalSha256, configHash (da /version), maxExpandedBytes, maxPages, maxBlocks, maxOutputBytes, timeoutSeconds. Job «incompleto» (T116) = job Completed con preparazione Incomplete. Preparazione Complete→Extracting, porzioni Pending (T057 le gestisce).

## Blocker
Nessuno.

## Comandi essenziali
- Backend (da `backend/`): `dotnet test --solution bProjects.sln` con `BPROJECTS_TEST_POSTGRES` impostata e senza `BPROJECTS_TEST_DGX_*`; filtri `--filter-trait "Category=Integration"`.
- Frontend: `npm --prefix frontend run test:ci`, `npm --prefix frontend run build`.
