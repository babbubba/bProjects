# T112 — Verifica di riuso e confini verso la 001 (prima della campagna T108)

Nessun LLM e nessun DGX. Test nuovi: `backend/tests/bProjects.IntegrationTests/DocumentLegacyRegressionTests.cs` (3 test; il terzo richiede `BPROJECTS_TEST_POSTGRES`).

## Riscontri

| Criterio | Evidenza |
| --- | --- |
| Vecchi checkpoint eseguiti con propria definizione/config | Costanti storiche congelate e distinte da quelle documentali (`project-analysis-v3`, `project-document-v1`, `conversation-v2`, `analysis-v4`, `document-v2`) nel test nuovo; instradamento in `AnalysisWorkflow.RunAsync`/`DocumentWorkflow.RunAsync` (le definizioni `*-documents-*` vanno al workflow documentale, ogni altra versione passa dal guard 001 che fallisce `workflow_incompatible`). Ripresa da checkpoint 001 e definizione incompatibile: `AnalysisWorkflowTests` (suite storica, passata). |
| 1.0 edit/conferma/generazione | `AcceptanceTests`, `AnalysisContentTests`, `AnalysisSchemaTests`, `DraftSchemaTests` (suite storiche, passate). |
| coverage-v1 | `EvidenceCoverage.CheckerVersion == "coverage-v1"` (nuovo); `EvidenceCoverageTests`, `DocumentCoverageTests` (passate). |
| Nessun documento/analisi al GET | Test nuovo su DB 001 migrato: progetti, messaggi, analisi, run, revisione, coverage, evidenze, documenti letti via HTTP con client AI che lancia se costruito; 22 tabelle di lavoro identiche prima/dopo. |
| Nessun secondo engine/ledger/RAG/embedding/KB/stime AI | Test nuovo: nessun pacchetto embedding/vettori/KB/orchestrazione concorrente nei csproj; MAF 1.26.0 esatto, 3 soli pacchetti; nel modello EF nessuna tabella o proprietà per parola (embed/vector/knowledge/rag/kb/chunk/estimat), una sola tabella `Evidences`, un solo `WorkflowCheckpoints`, un solo `InferenceCalls`. |

## Esecuzione pertinente (una sola volta, codice finale)

16 classi (workflow/dispatch/contenuto/schema/accettazione 001, coverage, small flow, recovery cross-root, regressione nuova, upgrade T105): 358 individuati, 358 superati, 0 falliti, 0 ignorati. Log grezzo in `diagnostics-local/` (non tracciato).

## Limiti e note

- I FAIL storici N2/N4/N5 (HANDOFF 001, esecuzioni reali su DGX) non sono ripetibili senza DGX e restano separati: questa verifica non li riesamina né li chiude.
- Non inclusi nel GET del test: `context-usage` (per un progetto esistente sonda i metadati del modello tramite il monitor, non esegue inferenza né analisi) ed `evidence-selection` (la vista congelata della fixture T037 è un segnaposto, non una vista reale).
- La ripresa di un checkpoint 001 e la sua propria configurazione sono provate dalle suite storiche, non rieseguite con un nuovo scenario.
- Nessuna modifica a `specs/001-project-draft/`; T052/T053 della 001 restano aperti.
