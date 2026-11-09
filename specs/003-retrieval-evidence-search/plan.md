# Implementation Plan: Recupero di evidenze documentali con ricerca ibrida e provenienza verificabile

**Branch**: `feature/003-retrieval-evidence-search` | **Date**: 2026-11-09 | **Spec**: [spec.md](spec.md)

**Input**: specifica con le cinque decisioni del committente del 2026-11-09 (D-1…D-5). Artefatti: [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md), [tasks.md](tasks.md). **Nessun codice è stato scritto; nessuna verifica runtime è stata eseguita.** Le misure del laboratorio sono evidenze sperimentali su dati sintetici, non requisiti.

## Summary

La 003 sostituisce **soltanto la sorgente dei candidati** dello stadio Gap della 002 (§13.7): quando il ledger non entra nel budget, invece del fail-fast `gap_ledger_exceeds_input`, i candidati sono prodotti da un **recupero ibrido sui blocchi figli originali** (PostgreSQL: ricerca lessicale italiana + pgvector, fusione RRF, riordino BGE) con **registro dei candidati congelato nel run**, `searchBasis = retrieval` e `retrievalComplete` calcolato. Contratto d'ingresso, prompt, DTO, validatore, assembly e coverage dello stadio restano della 002. Servizi indisponibili ⇒ comportamento tipizzato senza fallback silenzioso (D-3). Una valutazione continua (gold del laboratorio promossi a test) misura il recupero con soglie sul gold sintetico congelato (D-1) e l'accettazione piena richiede validazione indipendente (D-2).

## Technical Context

**Language/Version**: C# su .NET della solution esistente (`backend/global.json`); Angular 22 per la sola UI di coverage; PostgreSQL 18 con estensione pgvector.
**Primary Dependencies**: Microsoft Agent Framework **1.26.0** (pin esatto) già in uso — il recupero **non** è un agente ed è un passo deterministico del piano; EF Core/Npgsql esistenti; **pgvector** (nuova dipendenza di infrastruttura, ADR proporzionato T002); client HTTP per `/v1/embeddings` e `/v1/rerank` come tipi concreti. Nessun altro pacchetto previsto (verificare con `--locked-mode`).
**Storage**: PostgreSQL esistente; 2 tabelle derivate (indice e stato) e registro nel JSONB di `WorkOutput` ([data-model.md](data-model.md)).
**Testing**: Microsoft.Testing.Platform (`dotnet test --solution`, `--filter-trait`); livelli DET/PG/MAF-PG/DGX/E2E-OFFLINE/UI/PM/GATE; trait nuovo `Category=RetrievalEval`; test DGX solo con `BPROJECTS_TEST_DGX_ALLOW=1`.
**Target Platform**: stack Docker esistente; modelli di embedding/riordino sul router llama.cpp del DGX Spark (interno).
**Project Type**: web-service (monolite modulare) + frontend; nessun nuovo servizio.
**Performance Goals**: **nessun obiettivo assunto** (SC-009): si misurano latenza per Gap a indice pronto e costo a freddo; riferimenti di laboratorio (1,1–2,2 s/query con riordino; ~120–130 s a freddo T082) non sono garanzie.
**Constraints**: LLM e modelli solo interni (Costituzione V); filtri prima del ranking; passaggi interi nel pacchetto; nessun troncamento; nessun aumento automatico di budget né cambio di modello; v2–v6 della 002 byte-identici; nessuna interfaccia con una sola implementazione.
**Scale/Scope**: documenti da centinaia di migliaia a milioni di caratteri per progetto; conteggi esatti dei passaggi da misurare in M1 (T019).

## Constitution Check

*GATE: prima di Phase 0 e dopo Phase 1. Verificato sul testo di `.specify/memory/constitution.md` v1.0.0.*

| Principio | Valutazione | Evidenza nel piano |
|---|---|---|
| I. KISS/YAGNI | **Conforme con una deroga dichiarata** | Nessun servizio nuovo, nessuna interfaccia monomorfa (FR-032): servizi concreti, test double sul trasporto HTTP. Padri, riordino e v7 sono richiesti da FR/A e ognuno è dietro una misura (M2/M4). Deroga: un modulo `Retrieval/` nuovo nel monolite (Complexity Tracking). |
| II. MAF come riferimento | **Conforme** | Stadio, checkpoint, ripresa e `MaxAiCallsPerRun` restano MAF 1.26.0 della 002; il recupero è un passo deterministico che precede lo stadio. Nessuna primitiva MAF nuova richiesta: la "RAG" della Costituzione II qui è un indice PostgreSQL non-agentico (da dichiarare in T030). |
| III. Primitive native e verifica .NET | **Conforme, con verifica residua** | Nessuna alternativa custom a primitive native è introdotta; T030 verifica con compilazione e test su MAF 1.26.0 come il recupero entra nel piano (nuovo `Stage` vs `PrepareAsync`). Nessuna API MAF inventata. |
| IV. Responsabilità applicative | **Conforme** | Originali, evidenze e snapshot autorevoli invariati; indice derivato e ricostruibile (SC-006); registro congelato nel run non è autorità sui fatti. |
| V. LLM esclusivamente interni | **Conforme** | Embedding e riordino su DGX; host interno validato all'avvio (T005); nessun fallback cloud; nessuna nuova chiamata LLM. |
| VI. Vertical slice | **Conforme** | M1 consegna valore tecnico verificabile da solo; il valore PM (US1) arriva in M3; nessuna funzionalità futura implementata (Chat, bozza, ricerca per il PM esclusi, D-4). |
| VII. Operazioni esterne | **Conforme** | Nessuna operazione esterna irreversibile; gli step DGX/PM sono task con autorizzazione esplicita. |
| VIII. Integrazioni critiche reali | **Conforme per piano** | pgvector reale (PG), MAF reale (MAF-PG), modelli reali (DGX, T019/T026/T040) — **non ancora eseguiti**; mock e simulazioni non bastano e il rapporto distingue le modalità. |
| IX. ADR proporzionati | **Conforme** | Un solo ADR (pgvector/immagine DB, T002); nessun ADR per decisioni ordinarie. Qdrant: nessun ADR perché escluso salvo misura. |

**Gate pre-research**: PASS. **Gate post-design**: PASS con i punti aperti non normativi di §Rischi (nessuna violazione ingiustificata). Nessun requisito della Costituzione è modificato.

## Project Structure

### Documentation (this feature)

```text
specs/003-retrieval-evidence-search/
├── spec.md              # specifica (decisioni D-1…D-5 risolte)
├── plan.md              # questo file
├── research.md          # Phase 0: motore d'indice, mapping, modelli, degradazione, valutazione
├── data-model.md        # Phase 1: indice derivato, registro, invarianti
├── quickstart.md        # Phase 1: verifica per milestone
├── contracts/
│   ├── candidate-source.md      # candidati → stadio Gap (§13.7 invariato), routing v7, degradazione
│   ├── index-and-registry.md    # indice derivato, registro, motivi tipizzati, API
│   ├── model-readiness.md       # readiness embedding/riordino, pinning
│   └── evaluation.md            # C7: gold, metriche, soglie, accettazione
├── checklists/requirements.md
├── validation/          # (da creare) evidenze di milestone: baseline-002, M1-real, …
└── tasks.md             # speckit-tasks
```

### Source Code (repository root)

Struttura brownfield esistente (monolite modulare `backend/src/bProjects.App/` con moduli reali `AI`, `Analysis`, `Documents`, `Evidence`, `Persistence`, …; frontend Angular). Nessuna nuova soluzione/progetto.

```text
backend/src/bProjects.App/
├── Retrieval/                      # NUOVO modulo (cartella) del monolite
│   ├── RetrievalOptions.cs         # T005
│   ├── RetrievalEntities.cs        # T006 (indice derivato, stato)
│   ├── EmbeddingClient.cs          # T007
│   ├── RerankClient.cs             # T021
│   ├── RetrievalModelReadiness.cs  # T008/T021 (riusa lo schema di AI/ModelRuntimeMonitor)
│   ├── IndexText.cs                # T009
│   ├── RetrievalIndexBuilder.cs    # T010/T011
│   ├── HybridSearch.cs             # T012/T013
│   ├── Rrf.cs                      # T014
│   ├── BlockEvidenceMapper.cs      # T015
│   ├── CandidateRegistry.cs        # T015/T023
│   ├── RetrievalPolicy.cs          # T022
│   ├── CandidatePacker.cs          # T025
│   ├── RetrievalOutcome.cs         # T024
│   ├── RetrievalCandidateSource.cs # T029
│   └── ParentContext.cs            # T041 (M4)
├── AI/            # modifiche mirate solo in M3: DocumentReconcilePlan/AnalysisWorkflow (routing v7), GapStageInput (solo lettura del record)
├── Documents/     # SourceSnapshotStore.cs (chiavi di freeze v7, M3)
├── Evidence/      # EvidenceCoverage.cs (campi additivi, M3)
└── Persistence/   # migrazione additiva (T006)

backend/tests/bProjects.IntegrationTests/
├── Retrieval/Gold/            # fixture promosse dal laboratorio + PROVENANCE.json (T004)
├── Retrieval/Baseline/        # baseline dichiarate (T019/T026)
└── Retrieval*Tests.cs         # T016, T018, T036, T038, …
frontend/src/app/projects/     # vista coverage esistente (T034)
tests/e2e/retrieval-coverage.spec.ts  # T035
infra/compose.yaml             # immagine PostgreSQL con pgvector (T003)
docs/adr/                      # ADR pgvector (T002)
```

**Structure Decision**: un'unica cartella-modulo `Retrieval/` nel monolite esistente, perché indice, ricerca, registro e client dei modelli condividono dati e vita utile e non sono riutilizzabili altrove oggi (D-4); si separano i file per responsabilità (non le interfacce). Le modifiche ai moduli della 002 sono confinate a M3 e additive.

## Riuso dei componenti consolidati della 002 (nessuna duplicazione)

| Componente 002 | Riuso nella 003 |
|---|---|
| Blocchi `DocumentBlock` con `Ref`, `Ordinal`, `ParentId`, `HeadingPathJson`, `TextHash`, range UTF-16 | Unità di ricerca e fonte dei padri; nessun nuovo chunker |
| Evidence ledger e fonti (`BlockId`, `Start`, `End`, `Quote`) | Mappatura blocco→evidenza; provenienza verificabile |
| Snapshot congelato (`SourceSnapshot*`) | Filtri di versione/esclusione e riferimento del registro |
| Alias `alias-catalog-1`, formato VOCE `gap-stage-message-1`, `GapStageInput` | Proiezione dei candidati verso il modello, invariata |
| Stadio Gap §13.7 (DTO `gap-stage-alias-1`, `document-gap-v1`, `GapAssessmentRules`, assembly, coverage-3) | Riuso integrale senza modifiche |
| Readiness T126 (`ModelRuntimeMonitor`, `RuntimeReadiness`, `ReadinessWait`) | Schema di stati e comportamento (deadline, TTL, una sonda in volo) |
| `ContextBudget` | Misura del pacchetto |
| Checkpoint MAF 1.26.0, `DocumentWorkStore`, claim/fencing, `MaxAiCallsPerRun` | Persistenza e ripresa del batch di recupero; nessun consumo del tetto LLM |
| `DgxGuardTests`/opt-in `BPROJECTS_TEST_DGX_ALLOW` | Disciplina dei test reali |

## Milestone incrementali

| # | Milestone | Valore verificabile | Task | Avvio |
|---|---|---|---|---|
| **M1** | **PRIMO IMPLEMENTABILE** — indice derivato dei figli + lessicale + denso + RRF + **registro candidati** + valutazione sintetica | Dato un corpus, il registro ordinato dei candidati con provenienza, isolamento di progetto/versione, ricostruzione dell'indice e prima baseline di Recall (simulata in CI, reale su opt-in). **Nessun LLM, nessun reranker, nessun padre, nessuna modifica al run** | T001–T020 | Subito: dipende solo da dati già persistiti dalla 002 (blocchi, evidenze, snapshot) |
| M2 | Riordino + readiness + modalità degradata (D-3) + politica/budget | Hit@1 ≥ 95% su gold; comportamento tipizzato con servizi ko; pacchetto a passaggi interi | T021–T027 | M1 chiusa |
| M3 | Integrazione stadio Gap: definizione v7, sorgente `retrieval`, WorkOutput/checkpoint, coverage/API/UI, regressione 002 | **Valore PM (US1)**: Analizza non fallisce più per `gap_ledger_exceeds_input`; US2 (ispezione), US4 (nessuna regressione) | T028–T040 | M2 chiusa **e** `002:T150` PASS (o deroga) |
| M4 | Padri selettivi con budget | Titolo/ambito recuperati (N4) entro budget, adottati solo se la misura lo giustifica | T041–T043 | M3 chiusa |
| M5 | CI di valutazione, validazione indipendente, accettazione | «Accettata»/«accettata con riserva» (D-2) | T044–T050 | M3 chiusa |

**Perché M1 per prima**: è l'unica porzione senza prerequisiti aperti della 002, è verificabile da sola (registro e metriche), sposta il rischio principale (qualità del lessicale PostgreSQL, della mappatura blocco→evidenza e del costo di indicizzazione) **prima** di toccare i file di routing della 002, e produce la baseline su cui si misura la regressione (D-1). Se M1 fallisce la misura (T020), M2–M5 non iniziano.

## Dipendenze dalla 002 e punti di blocco

Fonti: `specs/002-project-documents/tasks.md` e contratti (sola lettura). Stato letto il 2026-11-09 (`git log`: T149 completato e verificato offline; T150 aperto) — T001 ne registra la riverifica.

| Elemento 002 | Stato | Effetto sulla 003 |
|---|---|---|
| **T150** gate DGX «SC-004-Gap» (stadio Gap con sorgente esaustiva) | **Non eseguito** (richiede autorizzazione e `BPROJECTS_TEST_DGX_ALLOW=1`) | **Blocca M3** (non M1/M2): non si sostituisce la sorgente di uno stadio mai verificato con modello reale. Deroga solo esplicita del committente |
| **T138** Gap oltre contesto | Aperto (si chiude con evidenza, T150) | Non blocca lo sviluppo di M1/M2; blocca la dichiarazione di accettazione 003 e va citato nel rapporto (T049) |
| **T148** definizione v6 e routing | `[x]` (offline, modello finto) | Prerequisito di T028; i file `SourceSnapshotStore.cs`, `AnalysisWorkflow.cs` possono ancora cambiare: rileggerli all'avvio di M3 |
| **T126** readiness bounded dei modelli | Aperto (prova DGX mancante) | Lo schema è riusato (M1); la prova reale della readiness 003 non può dirsi «analoga e provata» finché T126 non è chiuso; dichiarato nei rapporti |
| **T082 / T083 / T090** | Aperti | Non bloccano: la 003 usa generatore e gold in sola lettura; i corpora di valutazione non dipendono dai loro run |
| **T067 / T142 / T104–T114** | Aperti | Non bloccano; la 003 non attende l'accettazione della 002, ma **non la dichiara** né la sostituisce |
| Migrazioni | In evoluzione | T006 verifica l'ultima migrazione e si serializza con quelle della 002 |
| Conflitti di file in M3 | `SourceSnapshotStore.cs`, `DocumentWorkStore.cs`, `GapStageInput.cs`, `EvidenceCoverage.cs`, vista coverage | Modifiche additive; serializzare con la 002 e rieseguire l'intera suite 002 (T037) |

Nessun merge con la 002 né con `research/slice-003-lab` è previsto da questo piano: la 003 vive sul proprio branch e dipende dal **codice** della 002 presente nella sua base; la strategia di integrazione dei branch è decisione del committente.

## Integrazione con MAF e checkpoint (da verificare in T030)

Opzioni da provare con compilazione/test su 1.26.0 prima di scegliere: (A) nuovo valore di `Stage` `retrieval` per un batch deterministico che precede lo stadio `gap` (WorkOutput `retrieval-output-1`); (B) esecuzione nel `PrepareAsync` dello stadio Gap con registro congelato nell'`inputHash`. Criteri: ripresa senza ripetere chiamate ai servizi, takeover, nessun consumo di `MaxAiCallsPerRun`, nessuna nuova tabella, `DocumentBatchWorkflow` invariato. Si preferisce (A) se i test della 002 lo consentono senza modifiche ai workflow. La scelta si registra in `validation/`; non è una decisione normativa.

## Benchmark e valutazione continua (C7)

Specificato in [contracts/evaluation.md](contracts/evaluation.md): **promozione** di gold/query/script del laboratorio a test (T004, T017, T018), non task applicativi che li ricreano. Baseline = prima esecuzione ufficiale sul gold congelato (T019, T026); la regressione ≤ 2 punti si calcola sullo **stesso insieme di query** (D-1); CI simulata sempre, reale su opt-in (T044). La validazione indipendente (corpus anonimizzato + ≥ 20 query di un secondo autore) è T048 e condiziona l'esito «accettata» (D-2, T049).

## Rischi e punti aperti non normativi

1. **Lessicale PostgreSQL italiano non misurato** (stemming/stopword/numeri/sigle): misura in T019; se < soglia, alternative solo con misura (R-1).
2. **Perdita di mappatura blocco→evidenza** (fatto presente ma non estratto): quantificata in T019; blocca M3 se > 0 sul gold (T020).
3. **Costo di indicizzazione a freddo** (~2 min per T082 nel laboratorio, dominato dall'embedding) e deadline N/E della 002: indice costruito in modo asincrono dopo `Ready`; indice non pronto ⇒ errore tipizzato (R-5).
4. **pgvector**: nuova immagine/estensione di database, backup/restore e `migrate` (T002/T003).
5. **Evidenze `Message` sempre incluse** potrebbero non entrare nel budget: `retrievalComplete = false` (R-3).
6. **Lettura conservativa di D-3** (stadio non invocato in degrado): da confermare nell'analisi; l'alternativa richiede una nuova decisione (R-7).
7. **Lab non ha eseguito analisi/bozza bProjects**: l'effetto sulla valutazione Gap completa è dimostrato solo su pilota (4/10 esiti completi con figli, 2/10 con padri); T040 è il gate reale.
8. **La 002 non è accettata**: la 003 si appoggia a un'implementazione che può cambiare; mitigato da T001 e T037.

## Complexity Tracking

| Violazione / deroga | Perché serve | Alternativa più semplice scartata perché |
|---|---|---|
| Nuova cartella-modulo `Retrieval/` (non nell'elenco di CLAUDE.md `Identity/Projects/Conversations/Analysis/AI/Persistence`, che è già divergente dalla struttura reale `Documents/Evidence`) | Isola indice, client dei modelli e registro con un confine di test chiaro | Mettere i file in `AI/` o `Documents/` mescolerebbe responsabilità e renderebbe più difficile la futura riutilizzabilità senza interfacce (D-4). Non è un servizio né un progetto nuovo |
| Dipendenza di infrastruttura pgvector (immagine DB) | Segnale denso con filtri nella stessa query; unica alternativa senza servizi nuovi | Qdrant = servizio nuovo; embedding in memoria applicativa = non filtrabile/ricostruibile/persistente e duplica il motore. ADR T002 |
| Definizione v7 invece di modificare la v6 | FR-025/FR-031: contratti storici non riscritti | Modificare v6 riscriverebbe un contratto storico implementato e testato |
