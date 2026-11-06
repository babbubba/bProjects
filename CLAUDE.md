# bProjects — istruzioni per Claude Code

## Documenti autorevoli (in ordine di precedenza)

1. `.specify/memory/constitution.md` — prevale su tutto, incluse le regole di ECC. Non modificarla senza approvazione esplicita.
2. `AI-Project-Documentation-MVP-Plan.md` — fonte dei requisiti funzionali approvati; non amplia automaticamente il perimetro della slice corrente.
3. `specs/<slice-corrente>/` — specifica, piano, task, modello dati, contratti, ricerca e verifiche pertinenti; preservare i contratti storici delle slice precedenti.

`specs/<slice-corrente>/tasks.md` è l'unica baseline operativa della slice: niente pianificazione parallela. Spunta una checkbox solo se il task è completato e verificato. Se i documenti si contraddicono, segnalalo: non riscriverli per aggirare un test. Non rigenerare Specify/Clarify/Plan/Tasks della 001 per avviare nuove feature. Dopo un mandato esplicito, le nuove slice seguono Spec Kit nei propri artefatti; verificare il contesto feature degli script, che non deriva dal solo branch. Procedura brownfield in [AGENTS.md](AGENTS.md).

La 001 è [accettata con riserve per il pilota controllato](specs/001-project-draft/acceptance/decisione-accettazione.md); **T052/T053 restano aperti, non PASS**. Consultare [copertura, componenti riutilizzabili e incoerenze](docs/roadmap/implementation-coverage.md), oltre a backlog e HANDOFF della 001.

Prossimo incremento previsto: `002-project-documents` (PDF testuali, DOCX, Markdown, TXT; parsing, provenienza e documenti oltre contesto). KB condivise e RAG esclusi. Non avviare ancora skill Spec Kit o implementazione: questa è solo preparazione del repository. I divieti di allegati/Docling presenti nella 001 ne delimitano il perimetro storico, non tutte le feature future.

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
dotnet test --solution bProjects.sln --filter-trait "Category=MafReal"       # inferenza REALE: richiede BPROJECTS_TEST_DGX_ENDPOINT/_MODEL/_API_KEY e l'opt-in BPROJECTS_TEST_DGX_ALLOW=1
```

Senza le variabili d'ambiente i test di integrazione risultano *ignorati*, mai superati. Endpoint, modello e chiave DGX nell'ambiente **non bastano**: senza `BPROJECTS_TEST_DGX_ALLOW=1` i test reali sono ignorati con messaggio esplicito e nessuna inferenza viene eseguita (`DgxGuardTests`). Impostare l'opt-in solo per un'esecuzione DGX autorizzata.

Frontend (Node ≥ 22.22.3 o ≥ 24.15, richiesto da Angular 22):

```bash
npm --prefix frontend ci
npm --prefix frontend run build
npm --prefix frontend run test:ci
npm --prefix frontend run test:e2e      # Playwright; E2E_BASE_URL per uno stack già avviato
```

Operazioni esplicite sul database (mai all'avvio del server; `bootstrap-accounts` non tocca gli account esistenti):

```bash
docker compose -f infra/compose.yaml run --rm app migrate
docker compose -f infra/compose.yaml run --rm -e Bootstrap__Accounts__0__UserName=<pm> -e Bootstrap__Accounts__0__Password=<password-12+> app bootstrap-accounts
```

Stack Docker: copiare `infra/.env.example` in `infra/.env`, valorizzare `POSTGRES_PASSWORD` e `AUTH_SIGNING_KEY`, poi `docker compose -f infra/compose.yaml up -d --build`.

## Commit

Piccoli e coerenti, Conventional Commits (`feat(backend):`, `test:`, `build:`, `docs:`) con riferimento alla slice e al task quando applicabile (gli ID Txxx sono locali alla slice). Niente merge su `main` né push senza autorizzazione dell'autore. Branch previsto per il prossimo incremento: `feature/002-project-documents`; verificare sempre quello effettivo. Non creare cloni/worktree e non aggiungere `Co-Authored-By`. I file locali non tracciati in `docs/acceptance-trace/` vanno conservati ed esclusi dai commit; usare staging per percorsi espliciti.
