# Sprint 2 — handoff operativo (Slice 002, I2 → I3 → I4/T067)

Non è un piano: la fonte dei task è `tasks.md`. Solo stato per riprendere. Nessun segreto.

## Stato (aggiornato dopo T053; regola: aggiornare questo file dopo OGNI task, nello stesso commit)
- Branch `feature/002-implementation`. I2 chiuso. **Milestone corrente: I3**; spuntati T033–T054 + T118/T119. Commit verificati: `9ea3bc6` (T051), `ff73d49` (T052+T054), `2329f57` (T053). **T055 (DGX reale) in corso**: vedere `validation/I3-real.md` e `git status` (file non committati = lavoro dell'agente T055). Poi gate I3 e I4.
- **T051 FATTO**: il WIP era già corretto (`DocumentParseRunner.ScheduleExtractionAsync` crea unità ExtractUnit, assegna JobId, aggregatore unico Queued non eleggibile finché unità/porzioni non Completed); 14 test `DocumentPreparationCompletionTests` PASS. Limiti: concorrenza solo gare a due contendenti; `DocumentUnitPlan` statico (6000 car/unità), paragrafo singolo oltre budget → `document_incomplete`.
- **T052/T054 FATTI**: `viewVersion` 1.0.0 (legacy, solo Message) / 1.1.0 (envelope misto); documento in legacy → 406 `evidence_view_not_supported`; versione ignota → 400 `unsupported_view_version`; compare solo stessa Preparation o stesso ManifestHash, altrimenti 422 `versions_not_comparable`; nuova route `GET /documents/{d}/versions/{v}/evidence/versions` (route protette 48). Aperti: `nextCursor` non implementato; `http-api.md` da allineare; compare/version di Message con 1.1 restano legacy; «mapping strutturale verificabile» = stesso ManifestHash (interpretazione da confermare); rosso iniziale non osservato.
- **T053 FATTO**: frontend evidenza mista (badge origine, filtri, parti per `citationGroupId`, passaggi con range esatto), Ready solo con `currentPreparationId`+`currentEvidenceVersionId`. Vitest 298/298, build OK con warning budget (650.12 kB vs 650, causa non verificata); prettier ha reindentato `document-passage.component.html`/`project-documents.component.html`; `candidatePreparations` non ancora emesso dal backend; nessun E2E.
- **T055 PARZIALE, NON spuntato** (dettagli in `validation/I3-real.md`, test `DocumentExtractionMafRealTests.cs`): PASS runtime (W=32768, nessuna Calibration necessaria), thinking dentro max_tokens confermato, pdf-text/docx/md/txt/non-BMP/txt-long (14/14 gold)/zero-evidenze. **FAIL pdf-table-multipage**: 1ª run Ready ma perde esclusione R-05; 2ª/3ª `work_output_invalid` (violazione del validatore non dettagliata nel messaggio; controllo multi-blocco non raggiunto). OutputExpansion=8 invariato (ratio 2,65 su txt-long, 12–20 su testi brevi; il test ora lo annota + soglia completion ≤50% del tetto). DGX in router mode: warm-up prima della sonda (a freddo `runtime_unverified`). NOT VERIFIED: relazioni cross-unità (I5), perdita indotta in caso controllato, conflitto tra file, carico. Prossimo: indagare il fallimento pdf-table, poi rieseguire e spuntare T055. Ambiente: parser container `bprojects-t055-parser` su 127.0.0.1:58000.
- Storico (pre-T051): ultimo commit verificato `2d663c0` (T049/T050), poi commit WIP `04d6b30`.
- (SUPERATO: T051/T054 chiusi, vedi sopra) Ex WIP: agente interrotto a metà. Nel commit WIP (compila, 0 errori; test mai eseguiti): `Documents/DocumentBundleStore.cs`, `Documents/DocumentUnitPlan.cs`, `Evidence/DocumentEvidenceAggregate.cs`, modifiche a `Evidence/EvidenceLedgerStore.cs`, `Documents/DocumentParseWorker.cs`, `AI/AiDispatchService.cs`, `Persistence/ExecutionLeaseStore.cs`. Nessun test T051/T054 ancora scritto (`DocumentPreparationCompletionTests.cs` non esiste). Alla ripresa: leggere `git show --stat HEAD`, completare T051 (compreso il GAP: creare i DocumentJob ExtractUnit + assegnare DocumentPortion.JobId/UnitId dopo il commit del parse + unico aggregatore Queued per Preparation, non eleggibile finché le unità non sono valide), poi T054, T052, T053; poi T055 (DGX), poi I4. Non duplicare file, migration solo una alla volta.
- Prossimi dopo T051: T052 (endpoint Evidence envelope 1.0/1.1), T053 (frontend), T054 (test completamento), T055 (DGX reale, variabili BPROJECTS_TEST_DGX_* nell'ambiente), I3 gate = T048/T054 PASS + suite mirate; poi I4 (T056 test snapshot → T057 migration snapshot → T120/T121 → T058/T059/T060/T061 → … T066 regressione completa non-DGX UNA volta → T067 DGX).
- T049/T050 PASS (536/536 mirati). Decisioni da validare: unità fallita → preparazione Incomplete per document_incomplete/document_limit_exceeded/coverage_incomplete, altrimenti Failed, sorelle → parent_failed; OutputExpansion=8 (calibrare in T055); ordinale unità = prima porzione×10000+ordinale batch; `Documents:Calibration` alimenta PayloadCalibration (senza → runtime_unverified). Non fatto: replica header su split tabella oversize; alias Pn cross-unità. NOT VERIFIED: tokenizer/usage reali, DGX, formati reali.
- T048 PASS (59/59; clock DB congelato via schema `tclock`; bug lock del guard corretto con CTE MATERIALIZED). Residui: UPDATE singoli di ExecutionLeaseStore con stesso comportamento clock/lock (pochi ms); limite byte solo segnale (enforcement NOT VERIFIED); persistenza classificazione assente; CONTRADDIZIONE documenti T047 `call_limit` vs Plan («completo al max non fallisce»), non risolta.
- Crash host intermittenti (0xC0000409 / 0xC0000005) con classi isolate: noti, rilanciati passano.
- Rosso iniziale non osservato per T037, T038, T042, T045, T047, T118, T049.
- Ambiente dopo crash/riavvio: avviare Docker Desktop, `docker start bprojects-002-test-pg`, attendere `pg_isready`. `docs/acceptance-trace/` non tracciato: non toccare.

## Decisioni I3
- OriginKind: DEFAULT 'Message' nel DB (CK impediscono ramo documentale nascosto). Key evidenza globale `unitOrdinal+key locale` (32 char). AnalysisContent 1.1 regole strutturali in T034; validatore di revisione = T060.
- Budget (AI/): `DocumentPreflight` (Resolve/Measure/Ensure), `CreateBatchAgent`/`RunBatchAsync` (sessione nuova, no history), errori `runtime_unverified`/`tokenizer_mismatch`. `PayloadCalibration` non ha origine di config in produzione: senza `/apply-template` il ramo documentale è Unavailable (scelta aperta per T046/T049). Ipotesi thinking incluso in max_tokens da validare su DGX in T055.
- ContentSecurityTests aggiornato (route documentali I2 = 47 protette; ParserClient unico HttpClient tipizzato).
- Flake osservato una volta: `MigrateAsync` parallelo (property-bag DocumentPreparation), non riprodotto in 5 rerun.

## Test / evidenze (non ripetere senza modifiche)
- I3 mirati: schema/validazione/migrazione Evidence 130+33+37 PASS; non-regressione 001 ampia 1193 PASS dopo fix ContentSecurity (67/67); budget 13/13; AI/Conversations/Inference/Checkpoint con PG PASS (solo MafReal/DGX ignorati).
- Backend mirati (classi Document*/ParsedManifest*/ParserClient*/SourceSelection*): 425 totali, 423 PASS, 2 SKIP (DGX), 0 FAIL. Parser container offline: 141 PASS. Baseline suite completa I1: 1337/1296/41/0.
- I2 gate: Vitest 289/289, build OK; E2E fake parsing 10/10, reale (PG+parser, LLM off) 6/6; I1 rieseguito fake 8/8 e reale 10/10. NOT VERIFIED: stato Ready su stack reale (arriva con I3), retry di Failed reale/rete persa (solo fake/DET). Bug corretto: compose non passava Parser__BaseUrl/Secret ad app.
- Aperti noti: crash `0xC0000409` di `ExecutionFailureTests` (usare `--crashdump`), deadlock `MigrateAsync` non riprodotto. MafReal/DGX: NOT VERIFIED.
- NOT VERIFIED parser: documenti reali/grandi, tabelle PDF TableFormer, limite 250 MB reale, solo linux/amd64, licenze non riviste legalmente.

## Decisioni
- 408 `upload_timeout` approvato. Backend→parser: header `X-Internal-Secret`; nomi manifest allineati al parser (vedi parser.md). Job «incompleto» (T116) = Completed con preparazione Incomplete.
- `{g}` delle route preparations accetta generazione, id o `latest` (http-api.md da allineare). `reason` delle issue esposto come dal parser (decisione aperta: testo fisso?).
- Agenti non committano; il coordinatore committa per percorsi espliciti, senza Co-Authored-By.

## Prossimo
I3: T033 (+T038) eseguibili subito.

## Comandi
- Backend (da `backend/`): `dotnet test --solution bProjects.sln` con `BPROJECTS_TEST_POSTGRES` (container `bprojects-002-test-pg`, porta 55432, credenziali da `docker inspect`), senza `BPROJECTS_TEST_DGX_*`. Parser reale: `BPROJECTS_TEST_PARSER_ENDPOINT`/`_SECRET`.
- Frontend: `npm --prefix frontend run test:ci`, `npm --prefix frontend run build`.
