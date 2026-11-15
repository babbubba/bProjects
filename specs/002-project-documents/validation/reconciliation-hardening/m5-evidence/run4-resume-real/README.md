# RH-051 — prosecuzione reale della quarta run (2026-11-15/16)

**Esito: INCOMPLETE.** La ripresa dal checkpoint ha funzionato (30 output riusati, `expand 30` recuperato senza chiamate), ma la run si è fermata con `work_output_invalid`
in un batch `expand` nuovo: il modello ha citato un alias (`E109`) che non è nel catalogo del batch. Gap, revisione finale e confronto col gold NON sono stati raggiunti.
Non è un PASS di M5.

## Identità
| Voce | Valore |
|---|---|
| Test | `rh051-run4-resume-real` (una sola prosecuzione reale, copia isolata del database della 4ª run) |
| Finestra UTC | 2026-11-15T22:54:19Z → 23:10:45Z (985 s) |
| Git | branch `feature/003-retrieval-evidence-search`, HEAD `60b5815` (harness già committato; worktree senza modifiche ai sorgenti) |
| Workflow | `project-analysis-documents-v11` (manifest `gap-stage-1`), `reconcile-alias-1`, `reconcile-validation-v2`, `expansion-grid-1`, prompt `document-reconcile-v6`, contratto `reconcile-stage-contract-1` |
| Modello | `Qwen3.6-35B-Smart` (Qwen3.6-35B-A3B UD-Q4_K_XL), llama.cpp `b10703-0b5be7e4a`, W=65536, O=8192 (`max_completion_tokens`), M=1024, thinking spento, temperatura 0,1 |
| Estrazione | `ExtractionOutputExpansion=24` (solo sperimentale); nessuna riestrazione |
| Manifesto | Hash `f43d13dae5ef3bf4…`, ConfigHash `7d616f1695887c2b…`, SourceHash `ea3ba048ad267e79…`, PolicyHash `ea09e0a3eeae78e7…` |
| Run | `7f430f39-a1af-4323-adc0-6a2d48be802f`, tentativo 1 → 2, fence 1 → 2 |
| Backup iniziale | `rh051-run4-resume-real-PRE-20261115T234751.dump` (SHA-256 `AC41CCA9…ACE208`) |
| Backup finale | `rh051-run4-resume-real-POST-20261116T001046.dump` (SHA-256 `0BD12024…873E4C`) |
| Dump originali | `rh051-finale-20261115T190111.dump` `524623E70BBEC58B…`; `rh051-pre-analizza-20261115T184412.dump` `FD8E025FC7F3D7B7…` — invariati |

## Contenuto
`execution-summary.md` · `checkpoint-recovery.md` · `validation-report.md` · `gold-assessment.md` · `performance.md` · `lessons-learned.md` ·
`timeline.jsonl` · `batch-ledger.jsonl` · `inference-ledger.jsonl` · `run-report.json` · `identity.json` · `manifest.json` · `run-row.json` · `performance.json` ·
`reopen-operation.sql` · `preflight.txt` · `run-script.log` · `probe-db-state-at-stop.txt` · `artifacts-manifest.json` (artefatti con hash e posizioni).

Fuori da Git (archivio durevole `%USERPROFILE%\bprojects-rh051-evidence\run4-resume-real\`, percorsi e hash nel manifesto): dump PostgreSQL prima/dopo, `work-outputs.jsonl`
(payload dei WorkOutput), `inference-calls-db.jsonl`, `app-log.txt`, `dgx-props-*.json` e `run\raw\` (richiesta e risposta grezze di ognuna delle 43 chiamate).

## Riproduzione
1. `pg_restore` di `rh051-finale-20261115T190111.dump` (o del backup PRE) in un database nuovo del container `bprojects-test-pg`.
2. Variabili: `BPROJECTS_RH051_REAL_DB` (stringa di connessione del database), `BPROJECTS_RH051_REAL_EVIDENCE` (cartella di output), `BPROJECTS_TEST_POSTGRES` e, solo per l'esecuzione autorizzata, `BPROJECTS_TEST_DGX_ALLOW=1`.
3. `dotnet test --project tests/bProjects.IntegrationTests/bProjects.IntegrationTests.csproj --no-build --filter-class "*Rh051ResumeRealTests"` (test `Rh051ResumeRealTests`, tetto 200 chiamate reali complessive applicato prima dell'invio).
4. Il test esegue l'unica operazione `reopen-operation.sql` sulla copia. **Non rilanciare senza una nuova autorizzazione.**

## Probe non generativo (chiuso)
Il probe con modello finto (tetto 480) è stato interrotto in modo controllato; le sue risposte sintetiche sono confinate al database scratch `rh051_resume_engine` (eliminato dopo l'archiviazione del dump in `probe\`). La prosecuzione reale è ripartita dal dump originale, non dal probe: nessuna contaminazione (0 tentativi oltre il 1° prima della ripresa, 46 chiamate persistite, verificato in `preflight.txt`).
