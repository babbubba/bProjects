# Copertura implementativa — baseline brownfield

Rilevazione documentale e statica del 2026-11-01. Nessun test rieseguito per questa
baseline; gli esiti sotto sono quelli registrati, non nuove attestazioni.
Questo documento non sostituisce specifiche, contratti o task Spec Kit.

## 001-project-draft

**Accettata con riserve per MVP pilota controllato**, non per produzione generale:
[decisione](../../specs/001-project-draft/acceptance/decisione-accettazione.md).
Revisione PM obbligatoria delle bozze; conferma dell'analisi distinta dall'approvazione
cliente/documento. **T052 e T053 restano aperti e non superati**:
[task](../../specs/001-project-draft/tasks.md),
[residui e criticità](../../specs/001-project-draft/backlog-post-slice1.md).

| Capacità implementata / riutilizzabile | Riscontro nel repository | Confine |
| --- | --- | --- |
| Account locali, progetti, chat persistente e streaming | `backend/src/bProjects.App/Identity/`, `Projects/`, `Conversations/`; `tests/e2e/` | Originali salvati prima dell'inferenza; ambito progetto, FIFO e cutoff |
| Analizza → correzione → conferma PM → Genera Markdown | `backend/src/bProjects.App/Analysis/`, `AI/AnalysisWorkflow.cs`, `AI/DocumentWorkflow.cs`; `backend/tests/bProjects.IntegrationTests/PreviewRevisionTests.cs` | Snapshot/revisioni persistenti, concorrenza, bozza Proposed/NotApproved; niente pubblicazione |
| Agenti e infrastruttura AI | `backend/src/bProjects.App/AI/AgentFactory.cs`, `InternalModelClient.cs`, `AiDispatchService.cs`, `PostgresCheckpointStore.cs`; `Persistence/ExecutionLeaseStore.cs` | MAF 1.26.0, LLM interno, configurazione per fase, lease/fencing/retry e checkpoint; non un motore ingestion documentale |
| Registro atomico Evidence | `backend/src/bProjects.App/Evidence/EvidenceEntities.cs`, `EvidenceExtraction.cs`, `EvidenceLedgerStore.cs`, `EvidenceViews.cs`, `EvidenceEndpoints.cs`; `AI/EvidenceExtractor.cs` | Estrazioni versionate da messaggi PM, quote e offset UTF-16 validati, relazioni, diff/promozione, retry/backfill; fonti ancora ancorate ai messaggi |
| Copertura e vista Evidenze | `backend/src/bProjects.App/Evidence/EvidenceCoverage.cs`; `frontend/src/app/projects/evidence-view.component.ts`; `backend/tests/bProjects.IntegrationTests/EvidenceCoverageTests.cs` | Report versionati: errori deterministici, rischi semantici stimati e valutazioni PM; nessuna certificazione di fedeltà semantica |
| Budget e contesto lungo | `backend/src/bProjects.App/AI/ContextBudget.cs`, `AgentFactory.cs`, `AnalysisConsolidation.cs`; `backend/tests/bProjects.IntegrationTests/AnalysisConsolidationPlanTests.cs` | Token/budget per fase, compattazione nativa per chat/analisi e consolidamento incrementale con originali/evidenze interi; overflow esplicito se non trattabile |
| Generazione protetta e diagnostica | `backend/src/bProjects.App/AI/DocumentAgent.cs`, `InferenceCalls.cs`, `InferenceMetrics.cs`, `ModelRuntimeMonitor.cs` | Snapshot confermato non riassunto dal DocumentAgent; preflight senza omissioni; telemetria non sostituisce prove qualitative |

I percorsi relativi nella tabella partono dalla radice del repository. I test citati
sono riscontri disponibili, non dichiarazioni di esecuzione in questa rilevazione.

## Evidenze registrate e limiti

[HANDOFF §10.3–10.5](../../HANDOFF.md) riporta backend 1120 PASS / 0 FAIL / 39 SKIP,
Vitest 210/210 e prove PostgreSQL. Playwright offline: 189 PASS / 1 FAIL / 36 SKIP
nella prima esecuzione; rerun mirato persistenza 13 PASS / 1 SKIP dopo correzione
del comando del harness. Non presentare il primo FAIL come PASS della suite intera.
Estrattore reale T071: 6/6; vista Evidenze T069 reale: 1 PASS desktop, mobile SKIP.
Sono prove circoscritte, non l'intera campagna reale post-registro.

N1/N3 PASS su un campione ciascuno; N2/N4/N5 conservano FAIL (formato fonte,
tempi, fusione di fatti/correzione PM rifiutata, ripetizioni o collocazione semantica).
Il limite 180 s non è cambiato. Con un solo slot modello le estrazioni in corso
possono rallentare chat e Analizza. Ripetibilità e criteri SC-002/SC-003 non dimostrati.
Ultimo MafReal completo noto: 25 PASS / 7 FAIL; MafReal completo e Playwright reali
non rieseguiti dopo il registro. Mancano revisione di due PM e tecnico estraneo,
SC-004 manuale, misura tempo attivo PM e attestazione SC-001–SC-012.
Nessuna accettazione umana piena o chiusura T052/T053 è deducibile dai test automatici.

## Incoerenze documentali rilevate su T054–T073

- La decisione di accettazione §5 descrive T054–T073 come chiusi; `tasks.md`
  mantiene **T058, T061 e T062 `[ ]`**, mentre gli altri 17 task dell'estensione
  sono `[x]`. Codice e HANDOFF descrivono estrattore, vista deterministica e
  integrazione Analizza: implementazione riscontrabile, chiusura formale non uniforme.
  Non si sanano checkbox senza verifica puntuale dei criteri.
- Alcuni percorsi dei task sono destinazioni progettate poi accorpate:
  validatore in `EvidenceExtraction.cs`, vista/comparatore in `EvidenceViews.cs`,
  copertura in `EvidenceCoverage.cs`, comandi in `EvidenceEndpoints.cs`,
  completamento estrazioni in `EvidenceLedgerStore.cs`. L'assenza del singolo
  filename previsto non dimostra da sola una funzione mancante.
- Premessa e coda di `tasks.md` parlano ancora di nessuna implementazione/prova e
  nessun task completato: testo della generazione iniziale, incompatibile se letto
  come stato corrente. Anche branch `restart/speckit-clean` in spec/plan è storico.
- Gli stati ancora aperti in HANDOFF §10.3/10.4 sono aggiornamenti intermedi:
  §10.4 chiude T065/T066/T068/T070/T073, §10.5 chiude T069. La nota in `tasks.md`
  spiega che T069 reale è successivo a T070 offline, senza cambi di codice nel mezzo;
  non equivale a una nuova esecuzione di tutta la suite reale.

Gli artefatti della 001 restano invariati; queste discrepanze non cambiano i
criteri storici né i FAIL. Le tracce locali `docs/acceptance-trace/` restano non
versionate e non sono necessarie per consultare questa baseline.

## Incremento 002-project-documents — stato al 2026-11-08

Branch `feature/002-implementation`, baseline `03514b0`: 130/143 task spuntati; aperti T126, T067, T142, T082, T083, T138, T090, T104, T107, T109, T110, T111, T114.
Stato per area (IMPLEMENTED e TECHNICALLY_VERIFIED; nessuna area è FUNCTIONALLY_VERIFIED o ACCEPTED): documenti e versioni, parser offline con provenienza (`parser/`, `AI/Document*`), Evidence documentali, chat/analisi con fonti, oltre contesto con fail-fast per Gap (T143), readiness runtime (T126: `AI/ModelRuntimeMonitor.cs`, `DocumentRootClassification.cs`; test `DocumentRuntimeReadinessTests`). BLOCKED: T138 (valutazione Gap gerarchica) e T142 (N4). NOT_VERIFIED: DGX/PM e revisione umana di accessibilità.
Tracciabilità: requisiti in `specs/002-project-documents/spec.md`, task in `tasks.md`, evidenze in `specs/002-project-documents/validation/`, esiti e conteggi in [slice-002-technical-closure.md](slice-002-technical-closure.md). KB condivise e RAG restano fuori; PDF scansionati/OCR fuori dal perimetro.

Altre capacità del prodotto ancora future rispetto alla 001: KB e retrieval condiviso,
ricerca web controllata, editor JSON visuale avanzato, revisione AI per sezione,
export PDF/DOCX, amministrazione estesa e infrastruttura completa di backup e
osservabilità del piano MVP. Non sono automaticamente incluse nella 002.
