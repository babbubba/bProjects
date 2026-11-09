# Baseline operativa della 002 per la 003 (T001)

**Data**: 2026-11-09. **Commit di riferimento**: `e739a06d43d09e66f6869db661a07cd31f5cd201` (branch `feature/003-retrieval-evidence-search`).
Stato letto dalle checkbox **effettive** di `specs/002-project-documents/tasks.md` su questo branch; nessuna modifica ai file della 002. FR-025, FR-031.

| Task 002 | Checkbox | Nota |
|---|---|---|
| `002:T126` readiness bounded del runtime (`ModelRuntimeMonitor`) | `[ ]` aperto | Il codice `RuntimeReadiness`/`ReadinessWait` esiste in `AI/ModelRuntimeMonitor.cs` ed è riusabile; la prova DGX «scaricato → disponibile» non è chiusa. La readiness 003 (T008) è DET e **non** va dichiarata provata su infrastruttura reale |
| `002:T138` gerarchia v5 con semantica Gap | `[ ]` aperto | Si chiude solo dopo T144–T149 (e T150 se richiesto) |
| `002:T148` definizione `project-analysis-documents-v6` (freeze, routing) | `[x]` completato | Pre-requisito di M3 soddisfatto |
| `002:T150` gate DGX «SC-004-Gap» | `[ ]` aperto | **M3 non inizia con T150 aperto** (salvo deroga esplicita del committente) |
| `002:T067` prova DGX US1 piccolo | `[ ]` aperto | Gate DGX/PM |
| `002:T082` prova DGX documento singolo grande | `[ ]` aperto | Gate DGX/PM |
| `002:T083` prova DGX analisi aggregata | `[ ]` aperto | Gate DGX/PM |

M1 (T001–T020) non dipende da nessuno dei task aperti: usa solo blocchi, evidenze e snapshot già persistiti.

## Ambiente effettivo

- **Ultima migrazione EF presente** in `backend/src/bProjects.App/Persistence/Migrations/`: `20261106120000_DocumentLimitsSnapshot`.
- **Microsoft Agent Framework**: 1.26.0 (pin esatto `[1.26.0]` in `bProjects.App.csproj`).
- **Immagine PostgreSQL di `infra/compose.yaml` al momento della baseline**: `postgres:18` (senza pgvector). T003 la sostituisce con `pgvector/pgvector:pg18` fissata per tag e digest.
- Nessuna modifica concorrente ai file di routing/freeze della 002 (`SourceSnapshotStore.cs`, `AnalysisWorkflow.cs`, `GapStageInput.cs`) rispetto al commit citato.
