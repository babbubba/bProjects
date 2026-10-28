# bProjects — istruzioni per Claude Code

## Documenti autorevoli (in ordine di precedenza)

1. `.specify/memory/constitution.md` — prevale su tutto, incluse le regole di ECC. Non modificarla senza approvazione esplicita.
2. `specs/001-project-draft/` — `spec.md`, `plan.md`, `tasks.md`, `data-model.md`, `contracts/`, `research.md`, `quickstart.md`, `checklists/requirements.md`.
3. `AI-Project-Documentation-MVP-Plan.md` — piano storico: fonte dei requisiti funzionali, non amplia il perimetro della feature.

`tasks.md` è l'unica baseline di lavoro: niente altro sistema di pianificazione. Spunta una checkbox solo se il task è completato e verificato. Se i documenti si contraddicono, segnalalo: non riscriverli per aggirare un test. Non rieseguire Specify/Clarify/Plan/Tasks.

## Convenzioni

- KISS/YAGNI: un monolite modulare (`backend/src/bProjects.App/{Identity,Projects,Conversations,Analysis,AI,Persistence}`), nessuna classe o servizio per funzionalità future, nessun framework frontend oltre Angular + Bootstrap.
- Primitive native Microsoft Agent Framework 1.26.0 (pin esatto); non inventare API: verifica che compilino (T005).
- LLM solo su infrastruttura interna (DGX Spark): nessun LLM cloud, nessun fallback.
- Segreti mai nel repository: `infra/.env` (ignorato), variabili d'ambiente per i test.
- Nessun ADR o documento nuovo per decisioni ordinarie.

## Comandi

Backend (dalla cartella `backend/`; i test usano Microsoft.Testing.Platform, quindi `--solution` e `--filter-trait`):

```bash
dotnet restore bProjects.sln --locked-mode
dotnet build bProjects.sln --no-restore
dotnet test --solution bProjects.sln
dotnet test --solution bProjects.sln --filter-trait "Category=Integration"   # richiede BPROJECTS_TEST_POSTGRES
dotnet test --solution bProjects.sln --filter-trait "Category=MafReal"       # richiede BPROJECTS_TEST_DGX_ENDPOINT
```

Senza le variabili d'ambiente i test di integrazione risultano *ignorati*, mai superati.

Frontend (Node ≥ 22.22.3 o ≥ 24.15, richiesto da Angular 22):

```bash
npm --prefix frontend ci
npm --prefix frontend run build
npm --prefix frontend run test:ci
npm --prefix frontend run test:e2e      # Playwright; E2E_BASE_URL per uno stack già avviato
```

Stack Docker: copiare `infra/.env.example` in `infra/.env`, valorizzare `POSTGRES_PASSWORD`, poi `docker compose -f infra/compose.yaml up -d --build`.

## Commit

Piccoli e coerenti, Conventional Commits (`feat(backend):`, `test:`, `build:`, `docs:`) con riferimento al task (`T001`…). Niente merge su `main` né push senza autorizzazione dell'autore. Branch di sviluppo: `feature/001-project-draft`.
