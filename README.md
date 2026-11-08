# bProjects

Assistente AI per trasformare il dialogo con un Project Manager in una bozza di documento Markdown verificabile: chat → analisi strutturata correggibile e confermabile → generazione del documento. Progetto personale open source.

## Stack

- **Backend**: ASP.NET Core / .NET 10, EF Core + Identity + Npgsql, Microsoft Agent Framework 1.26.0
- **Frontend**: Angular 22, Bootstrap 5.3.8, SCSS
- **Dati**: PostgreSQL 18
- **Esecuzione**: Docker Compose (web nginx, app, db); LLM esclusivamente interni (DGX Spark)
- **Test**: xUnit v3 (integrazione PostgreSQL/DGX), Playwright (E2E)

## Stato

La slice `001-project-draft` implementa chat persistente, analisi correggibile e confermabile, generazione Markdown e registro atomico delle evidenze. È stata [accettata con riserve per il solo pilota controllato](specs/001-project-draft/acceptance/decisione-accettazione.md): **T052 e T053 restano aperti, non PASS**. Limiti, prove registrate e discrepanze sullo stato di T054–T073 sono nella [baseline di copertura](docs/roadmap/implementation-coverage.md).

Incremento in corso: `002-project-documents` (PDF testuali, DOCX, Markdown e TXT; parsing offline, provenienza, documenti oltre contesto), in implementazione sul branch `feature/002-implementation`. KB condivise e RAG esclusi. Restano aperti i gate DGX/PM, la parte umana di T107 e T114: nessuna accettazione della 002 è attestata. Il comportamento senza documenti resta quello della 001. Istruzioni operative del delta (limiti, parser offline, backup e `migrate`, livelli di test, diagnosi): [operations.md](specs/002-project-documents/validation/operations.md).

## Struttura

```text
backend/        solution .NET (src/bProjects.App, tests/bProjects.IntegrationTests)
frontend/       applicazione Angular
tests/e2e/      test Playwright
infra/          Docker Compose e configurazione nginx
specs/          specifiche Spec Kit (spec, piano, task, contratti)
.specify/       Constitution e strumenti Spec Kit
```

Istruzioni Codex e uso brownfield di Spec Kit: [AGENTS.md](AGENTS.md). Convenzioni e comandi: [CLAUDE.md](CLAUDE.md). Ogni nuova slice avrà i propri artefatti in `specs/`; [la 001](specs/001-project-draft/) conserva specifiche e contratti storici.

## Licenza

Non ancora definita.
