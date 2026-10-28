# bProjects

Assistente AI per trasformare il dialogo con un Project Manager in una bozza di documento Markdown verificabile: chat → analisi strutturata correggibile e confermabile → generazione del documento. Progetto personale open source.

## Stack

- **Backend**: ASP.NET Core / .NET 10, EF Core + Identity + Npgsql, Microsoft Agent Framework 1.26.0
- **Frontend**: Angular 22, Bootstrap 5.3.8, SCSS
- **Dati**: PostgreSQL 18
- **Esecuzione**: Docker Compose (web nginx, app, db); LLM esclusivamente interni (DGX Spark)
- **Test**: xUnit v3 (integrazione PostgreSQL/DGX), Playwright (E2E)

## Stato

Sviluppo spec-driven della feature `001-project-draft` (53 task). Completati i task di setup T001–T004 (solution, frontend, harness di test, Docker Compose); la feature non è ancora utilizzabile.

## Struttura

```text
backend/        solution .NET (src/bProjects.App, tests/bProjects.IntegrationTests)
frontend/       applicazione Angular
tests/e2e/      test Playwright
infra/          Docker Compose e configurazione nginx
specs/          specifiche Spec Kit (spec, piano, task, contratti)
.specify/       Constitution e strumenti Spec Kit
```

Convenzioni e comandi di sviluppo: [CLAUDE.md](CLAUDE.md). Specifiche: [specs/001-project-draft](specs/001-project-draft/).

## Licenza

Non ancora definita.
