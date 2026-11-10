# Handoff M3 offline (slice 003) — 2026-11-10

Branch `feature/003-retrieval-evidence-search` (main della PR #6 integrato). Deroga del committente registrata in `specs/003-retrieval-evidence-search/tasks.md` (sviluppo offline; T027 e 002:T150 aperti, nessun DGX, M3 non accettata).

## Stato (implementato ≠ accettato; nessuna checkbox di tasks.md ancora spuntata)
- T028 v7: `ContractOf` a insiemi chiusi, freeze `AnalysisPromptVersionsV7`, POST v7 solo con `Retrieval:Enabled`. Test `RetrievalDefinitionV7Tests` verdi; classi 002 Gap/V6 verdi.
- T029/T030: `GapStageBuilder.BuildFromRegistry`, batch deterministico `retrieval` (WorkOutput `retrieval-output-1`) in `DocumentReconcilePlan` (Prepare/ExecuteRetrieval), DI in `Program.cs`. Nessuna chiamata LLM, nessuna promozione del batch.
- T031: errori tipizzati (embedding_unavailable, retrieval_index_not_ready, reranker_unavailable) verificati da `RetrievalGapFlowTests`.
- T036: `RetrievalGapFlowTests` 8 test MAF-PG verdi (commit 0253969, fix f957587).
- T034/T035: UI + Vitest + E2E (8 test desktop/mobile con axe) verdi.
- T032/T033: codice scritto e COMPILA, NON testato, ultimo commit: `EvidenceCoverage.cs` (sezione `retrieval`, absenceProven=false, `LimitsRetrieval`), `Retrieval/RetrievalEndpoints.cs` (GET `/analyses/{id}/retrieval-registry`).

## Da fare
1. Test T032 (DET+PG): report v5/v6 byte-identici (golden esistenti `DocumentCoverageTests`, `DocumentGapStageAssemblyTests`), proprietà absenceProven=false con searchBasis retrieval, coverage di un run v7 (estendere `RetrievalGapFlowTests`). Lanciare le classi 002 coverage per escludere regressioni.
2. Test T033: autorizzazione per progetto (404 su progetto diverso), 404 `retrieval_registry_not_available`, run Diagnostic consultabile.
3. T038 (alias non forniti/citazioni su candidati recuperati), T039 (proprietà assenza), T037 (regressione mirata; suite completa solo se autorizzata).
4. Spuntare i task verificati in tasks.md; T040 resta DGX (non autorizzato).
5. Limite noto: IndexConfigHash richiede un'unica versione di parser nello snapshot; costi di pacchetto misurati per ogni evidenza (N+1 misure).

Ambiente test: `BPROJECTS_TEST_POSTGRES="Host=localhost;Port=15432;Username=postgres;Password=testpw"` (container bprojects-test-pg); `dotnet test --solution bProjects.sln --no-build -- --filter-class "*Nome"`.
