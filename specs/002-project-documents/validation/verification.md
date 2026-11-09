# Campagna di verifica non-DGX (T108)

Data 2026-11-08, codice `0f1f065` piu la sola correzione di isolamento dello smoke E2E (sezione Difetti). **Nessun DGX, nessun modello reale**: le variabili `BPROJECTS_TEST_DGX_MODEL/_API_KEY/_ENDPOINT` (presenti nell'ambiente di sessione) sono state rimosse dal processo di ogni comando con `env -u`, `BPROJECTS_TEST_DGX_ALLOW` mai impostata, `Category=MafReal` mai eseguita (sempre `--filter-not-trait "Category=MafReal"`), nessun `E2E_MODEL_MODE=real`. Il solo modello e il finto (`FakeUnitModel`, `tests/e2e/support/fake-llm.ts`). Log grezzi solo in `diagnostics-local/T108/` (non versionato). Esecuzione seriale: nessuna build o Playwright concorrente.

I livelli sono distinti e i numeri **non si sommano**; uno skip ambientale dichiarato non e un PASS.

## Esiti per livello

| Livello | Comando | Scoperti | Superati | Falliti | Saltati |
| --- | --- | --- | --- | --- | --- |
| Restore + build backend | `dotnet restore bProjects.sln --locked-mode`; `dotnet build bProjects.sln --no-restore` | n/a | OK | 0 errori, 0 avvisi | n/a |
| Backend DET (senza PG) | `dotnet test --solution bProjects.sln --no-build --filter-not-trait "Category=MafReal"` | 3486 | 1774 | 0 | 1712 |
| Backend PG (+ parser reale) | stesso comando con `BPROJECTS_TEST_POSTGRES` (15432, `bprojects-002-test-pg2`) e `BPROJECTS_TEST_PARSER_ENDPOINT/_SECRET` (58300) | 3486 | 3481 | 0 | 5 |
| Parser offline (container `--network none`, read-only, cap-drop ALL, 2 CPU/4 GiB) | `python -m pytest -q -p no:cacheprovider` su immagine stage `test` ricostruita dai sorgenti correnti | 159 | 158 | 1 (preesistente) | 0 |
| Vitest | `npm --prefix frontend run test:ci` (dopo `npm ci`) | 429 in 23 file | 429 | 0 | 0 |
| Build frontend | `npm --prefix frontend run build` | n/a | OK | 0 errori; 1 avviso di budget (sotto) | n/a |
| Compose | `docker compose -f infra/compose.yaml config -q` | n/a | exit 0, nessun output | 0 | n/a |
| E2E offline fake-stack (2 progetti: 1440 e 390 px) | `npx playwright test` da `tests/e2e`, senza `E2E_BASE_URL` | 314 in 22 file | 242 | 0 | 72 |
| E2E stack reale + LLM finto (desktop-1440) | `npx playwright test -c playwright.real.config.ts` | 17 in 5 file | 17 | 0 | 0 |

Note sui conteggi:

- Backend: `--list-tests` individua 3463 casi non-MafReal (3520 con i 57 MafReal, non eseguiti); l'esecuzione ne riporta 3486 perche alcuni casi di teoria si espandono solo a runtime. Il totale e identico fra DET e PG; cambia solo la ripartizione superati/saltati, quindi il livello PG non e un sottoinsieme ne un duplicato del DET: e lo stesso insieme con l ambiente presente.
- Backend DET, 1712 skip, tutti ambientali dichiarati dal messaggio di skip: 1687 `BPROJECTS_TEST_POSTGRES non impostata`, 21 `BPROJECTS_TEST_PARSER_ENDPOINT non impostata`, 2 traccia V7 assente, 1 symlink su file non consentito all'utente Windows (il caso directory/junction e coperto).
- Backend PG, 5 skip: 1 `CompactionFlowTests` (opt-in DGX `BPROJECTS_TEST_DGX_ALLOW=1` mancante: nessuna inferenza, non e un PASS), 3 tracce V7 (`BPROJECTS_ACCEPTANCE_TRACE`/`BPROJECTS_REVIEW_OUT`, `docs/acceptance-trace/` locale non presente), 1 symlink su file.
- Il flake noto `DiagnosticsHarnessTests` (timeout 880 vs 900 ms) **non si e presentato** in nessuna esecuzione.
- E2E fake-stack, 72 skip, tutti ambientali: le prove marcate `[E2E reale]` che richiedono uno stack Compose esterno (`E2E_BASE_URL`, `E2E_USER/_PASSWORD`, `E2E_ADMIN_*`, `E2E_RESTART_COMMAND`; `document-archive` 10, `document-parsing` 6), le prove `E2E_MODEL_MODE=real` (`context-monitor-real`, `project-draft-success` N1-N5, `project-chat`, `evidence-ledger`, `document-small-flow-real` 2: richiedono DGX) e quelle senza credenziali dello stack 001. Non e stato avviato uno stack Compose: gli stessi percorsi reali del parsing/archivio sono provati dai livelli backend PG con parser reale e dal livello «stack reale + LLM finto» sopra.
- Parser: 56 `test_contract.py`, 62 `test_locators.py`, 8 `test_offline_conversion.py`, 33 `test_security.py`. L'immagine `bprojects-parser-test:t108` e stata ricostruita dai sorgenti correnti (strati caricati dalla cache, nessun download).

## Preesistente dichiarato, non corretto

- `parser/tests/test_locators.py::test_pdf_facts_resolve_to_native_page_and_bbox[pdf-split-f1]`: FAIL (`citazione assente nella pagina 1`). Noto e preesistente (stesso fallimento delle campagne precedenti); non toccato.
- Build frontend: avviso di budget `bundle initial exceeded maximum budget` (650,39 kB contro 650 kB, +386 byte); in I4 il bundle iniziale era 647,15 kB. E un avviso, non un errore: la build riesce. Crescita dovuta alle viste documentali successive a I4; non corretta ne riconfigurata qui (decisione di prodotto sul budget, da segnalare).

## Discovery non vuota per gate (prima/durante l'esecuzione)

Ogni gate sotto ha casi individuati e sono stati eseguiti con PG reale (`--filter-class` per classe, comando come sopra); nessuna categoria vuota e stata scambiata per PASS. Una prima invocazione con un nome di classe inesistente (`AcceptanceTests`, solo nome di file) ha selezionato 0 test ed e stata scartata come errore di filtro mio, non conteggiata: rieseguita sulla classe reale `AcceptanceDeterministicTests` (10/10).

| Gate | Sede | Scoperti | Superati | Falliti | Saltati |
| --- | --- | --- | --- | --- | --- |
| T018 archivio | `DocumentArchiveApiTests` | 26 | 26 | 0 | 0 |
| T026 parser | 4 file pytest (sopra) | 159 | 158 | 1 preesistente | 0 |
| T032 parsing | Vitest `project-documents.component.spec.ts` (nel run da 429); E2E fake `document-parsing` 16 (10 fake superati, 6 `[E2E reale]` saltati per stack Compose assente); backend `DocumentParsingRecoveryTests` | 19 (backend) | 19 | 0 | 0 |
| T048 regime/concorrenza | `DocumentRegimeConcurrencyTests` | 59 | 59 | 0 | 0 |
| T066 percorso piccolo | `DocumentSmallFlowTests` + routing `DocumentWorkflowRoutingTests` + `DocumentRootClassification{Api,Input,Snapshot,}Tests` | 5 + 73 + 6/10/9/23 | tutti | 0 | 0 |
| T066 browser | E2E fake `document-small-flow` | 6 | 6 | 0 | 0 |
| T080 riconciliazione | `DocumentReconciliationPersistenceTests` | 14 | 14 | 0 | 0 |
| T092 contesto lungo | E2E fake `document-long-context` 12; stack reale `document-long-context-stack` 4 | 12 + 4 | 12 + 4 | 0 | 0 |
| T103 versioni/recupero | E2E fake `document-version-recovery` 8; stack reale (stesso file, livello `E2E_STACK`) 5 | 8 + 5 | 8 + 5 | 0 | 0 |
| T112 legacy | `DocumentLegacyRegressionTests` | 3 | 3 | 0 | 0 |
| Parser reale (backend) | `ParsedManifestRealParserTests`, `DocumentParseRetryRealParserTests` | 10 + 2 | 12 | 0 | 0 |

## Regressioni legacy (suite esistenti, nessuna fixture o criterio 001 riscritti)

Eseguite per classe con PG, tutte 0 fallite e 0 saltate:

| Area legacy | Classi | Casi |
| --- | --- | --- |
| FIFO/schedulazione, dispatch | `AiSchedulingTests` 6, `AnalysisDispatchTests` 15, `ChatTurnDispatchTests` 36 | 57 |
| Chat recovery/streaming | `ChatRecoveryTests` | 10 |
| Ripresa da checkpoint e definizione incompatibile | `AnalysisWorkflowTests` | 16 |
| Anteprima/revisione, cutoff, edit/concorrenza | `PreviewRevisionTests` 15, `RevisionConcurrencyTests` 16 | 31 |
| Conferma/generazione/contenuto 1.0 | `AnalysisContentTests` 141, `AcceptanceDeterministicTests` 10 | 151 |
| coverage-v1 | `EvidenceCoverageTests` 37, `DocumentCoverageCheckerTests` 18, `DocumentCoverageEndpointTests` 20 | 75 |
| Upgrade 1.0.0 -> documentale | `DocumentFullUpgradeTests` | 2 |

Nessuna regressione. I FAIL storici N2/N4/N5 della 001 non sono ripetibili senza DGX e restano separati (non riesaminati).

## Difetti emersi e correzione

1. **Isolamento dello smoke E2E (`tests/e2e/real-stack-smoke.spec.ts`)**: eseguito nel run completo del livello reale dopo le altre prove falliva perche il database conteneva i documenti delle prove precedenti (le altre prove azzerano il database a inizio prova, lo smoke no; passa da solo, come in I7). Correzione minima: `stack.resetData()` a inizio prova. Nessuna asserzione ridotta o cambiata.
2. **Parser dello stack reale non allineato (ambientale)**: il container preesistente `bprojects-t055-parser` e basato su un'immagine precedente alla correzione T106 (immagine Markdown che mandava in crash la conversione). Il primo run del livello reale ha quindi dato 2 FAIL (`document-security-stack` egress: `Failed` invece di `Incomplete`; smoke: residuo del punto 1) e 2 non eseguite. Rieseguito per intero con un parser temporaneo ricostruito dai sorgenti correnti (`bprojects-parser:t108`, container `bprojects-t108-parser`, `E2E_STACK_PARSER_CONTAINER/_PORT`, rimosso a fine prova; il container preesistente non e stato toccato): **17/17 superati**. Il primo run (13 superati, 2 falliti, 2 non eseguiti) e conservato solo in `diagnostics-local/T108/pw-real.log`.

Il livello PG del backend ha usato il parser preesistente (58300), anteriore a T106; le sue classi con parser reale sono passate, ma non sono state rieseguite contro l'immagine corrente.

## Non eseguito / limiti

- Nessun livello DGX (`MafReal`, `*-real`, N1-N5 reali), ne T067/T082/T083/T104/T111: fuori perimetro T108. Le prove DGX restano consolidate da T114.
- Nessuno stack Docker Compose avviato (`up`, migrazioni): solo `config -q`. Le prove E2E `[E2E reale]` su Compose (archivio, parsing) risultano SKIP in questo run; l'evidenza di quando furono eseguite e in `I1.md`/`I2.md`.
- Nessuna revisione umana. **Dipendenza parziale T107**: la parte manuale/umana dell'accessibilita (revisione due PM e tecnico) e ancora aperta; qui sono stati riesercitati i soli controlli automatici (`document-accessibility` 12 fake, `document-accessibility-stack` 2 reali con LLM finto, Vitest). Questo non attesta la conformita WCAG.
- Non risolta la contraddizione documentale gia nota sulle candidate fallite (vedi I7).
- Questa campagna non chiude T052/T053 della 001 ne attesta l'accettazione della 002.
