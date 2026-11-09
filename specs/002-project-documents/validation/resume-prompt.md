# Prompt di ripresa (nuova sessione Claude Code)

Lavori su `babbubba/bProjects`, branch `feature/002-implementation`. Parti SOLO dopo aver letto, nell'ordine: `CLAUDE.md`, `.specify/memory/constitution.md` (non modificarla), `specs/002-project-documents/validation/sprint2-handoff.md` (sezione finale «HANDOFF 2026-11-04 (fine sessione)»), i task T131/T132/T126/T082/T083 in `specs/002-project-documents/tasks.md` e `contracts/ai-workflow.md` §10–§11.

Modello: Sonnet Medium; ECC; subagent a contesto limitato (un solo agente alla volta sugli stessi file); niente log o diff interi nel dialogo.

1. Git: `git status`, `git log --oneline -10`. Non fare reset/rebase/cherry-pick. Non committare `docs/acceptance-trace/` né `frontend/src/app/projects/project-events.service.spec.ts`. Nessun `Co-Authored-By`, nessun push/merge senza autorizzazione.
2. Ambiente: avvia Docker Desktop, `docker start bprojects-002-test-pg2 bprojects-t055-parser`; password PG e secret parser da `docker inspect`; per i test non-DGX `unset BPROJECTS_TEST_DGX_*`.
3. Verifica T132 (commit 2d91d52, 67ce6bf, non spuntato): suite `dotnet test --solution bProjects.sln --filter-not-trait "Category=MafReal"` due volte; spunta solo se verde.
4. Implementa T131 (riconciliazione gerarchica per i run v4/alias) in TDD secondo AI §11; il motore gerarchico resta condiviso; v2/v3 storici invariati; prova anche tramite il POST reale `Analizza`.
5. Poi T126 sul DGX a modello scaricato, quindi T082/T083 con inferenze serializzate. Qwen3.6-35B-Smart, W=131072, reasoning OFF.
6. Regole: nessun nuovo retry per N4 (resta regressione semantica nota); non allentare validatori, non toccare gold; T067 resta aperto; non spuntare prove DGX non eseguite; I4 non è accettato. Distingui IMPLEMENTED / VERIFIED / FAILED / NOT VERIFIED. Aggiorna `tasks.md` e handoff dopo ogni task, commit piccoli e separati.
