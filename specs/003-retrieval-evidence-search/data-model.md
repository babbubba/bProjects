# Data Model: Recupero di evidenze documentali

**Feature**: `003-retrieval-evidence-search` | **Data**: 2026-11-09 | [spec](spec.md) · [plan](plan.md) · [research](research.md)

Principio (FR-007, A-6, Costituzione IV): **nulla di autorevole nasce nella 003**. Originali, versioni, blocchi (`DocumentBlock`), evidenze (`Evidence*`), snapshot (`SourceSnapshot*`) restano della 001/002 e non cambiano schema. La 003 aggiunge **una proiezione derivata ricostruibile** (2 tabelle) e **un artefatto congelato nel run** (JSONB di `WorkOutput`, nessuna tabella).

## Entità persistite (nuove, derivate)

### RetrievalIndexEntry — voce d'indice per blocco (derivata)

| Campo | Tipo | Vincolo / regola |
|---|---|---|
| `Id` | uuid | PK |
| `BlockId` | uuid | NOT NULL; FK logica a `DocumentBlock.Id` (immutabile); **unico insieme a `IndexConfigHash`** |
| `IndexConfigHash` | text (64 hex) | NOT NULL; hash di (`index-text-1`, modello+artefatto+dimensione embedding, configurazione FTS `italian`, versione parser/manifest) |
| `ProjectId` | uuid | NOT NULL; denormalizzato dal blocco per il filtro **prima** del ranking |
| `DocumentId` | uuid | NOT NULL; denormalizzato |
| `DocumentVersionId` | uuid | NOT NULL; denormalizzato; i filtri di versione inclusa/esclusa usano lo snapshot, non una colonna di stato |
| `TextHash` | text | NOT NULL; `DocumentBlock.TextHash` al momento dell'indicizzazione; incoerenza con il blocco ⇒ voce stantia (ricostruire) |
| `Tsv` | tsvector | NOT NULL; `to_tsvector('italian', testo indicizzato)`; GIN |
| `Embedding` | vector(1024) | NOT NULL; L2-normalizzato; dimensione = `Retrieval:Embedding:Dimension`; indice HNSW solo se giustificato da T019 |
| `CreatedAt` | timestamptz | NOT NULL |

Regole: nessun testo del blocco duplicato (si legge da `DocumentBlock`); nessuna colonna di verità; cancellabile in blocco. Indici: `(ProjectId, IndexConfigHash)`, `(DocumentVersionId, IndexConfigHash)`, GIN su `Tsv`, vettoriale opzionale.

### RetrievalIndexState — stato per versione e configurazione (derivata)

| Campo | Tipo | Vincolo / regola |
|---|---|---|
| `DocumentVersionId` | uuid | PK composta con `IndexConfigHash` |
| `IndexConfigHash` | text | PK composta |
| `ProjectId` | uuid | NOT NULL |
| `Status` | text/enum | `Pending` → `Building` → `Ready` \| `Failed`; **`Ready` solo se tutte le voci dei blocchi della versione sono presenti e coerenti** (conteggio = blocchi della versione) |
| `BlockCount` / `EntryCount` | int | NOT NULL; `EntryCount = BlockCount` richiesto per `Ready` |
| `ErrorCode` | text? | tipizzato (`embedding_unavailable`, `embedding_dimension_mismatch`, …); null se non `Failed` |
| `BuiltAt` / `UpdatedAt` | timestamptz | — |

Transizioni: `Pending→Building→Ready|Failed`; `Failed→Building` (riprova idempotente); `Ready→(obsoleta)` quando cambia `IndexConfigHash` (nuovo stato per la nuova configurazione; il vecchio resta finché eliminato). Un `Failed` non lascia voci contrassegnate pronte.

## Artefatti congelati nel run (nessuna tabella)

### RetrievalRegistry — registro dei candidati (JSONB in `WorkOutput` `retrieval-output-1`)

Un registro per run; riferito allo **snapshot congelato** (non al ledger corrente). Schema normativo in [contracts/index-and-registry.md](contracts/index-and-registry.md). Campi di testa: `contract = "retrieval-registry-1"`, `snapshotHash`, `policyVersion` (`retrieval-policy-N`) e `policyHash`, `indexConfigHash`, `searchBasis = "retrieval"`, `retrievalComplete`, `degraded`, `outcome ∈ {Package, Diagnostic, Failed}`, `causes[]` tipizzate, `gaps[]`, `packageEvidence[]` (ordine finale deduplicato), `excluded[]`, `readiness` (modello, artefatto, dimensione, `sha256Status`, versione servizio). Per ogni Gap: `query` (testo canonico), `candidates[]`.

### CandidateEntry — voce del registro (per Gap)

| Campo | Regola |
|---|---|
| `blockId`, `documentId`, `documentVersion`, `documentName`, `section` (`HeadingPath` joined, vuota se assente), `rangeStart`/`rangeEnd` (UTF-16 `[start,end)`), `textHash` | provenienza completa (A-5, FR-004); sezione mai inventata |
| `lexicalRank`, `denseRank`, `rrfRank`, `finalRank` | 1-based; `null` se non presente nel segnale; il punteggio del riordino non è persistito come probabilità (al massimo come diagnostica etichettata «non calibrato») |
| `evidenceIds[]` | evidenze del ledger che citano il blocco (R-3); vuoto ⇒ `reason = no_evidence_for_block` |
| `origin` | `ranked` \| `always_included` (Message/Declared) |
| `inPackage` | bool: entrato nel contesto dello stadio |
| `excludedReason` | `budget` \| `no_evidence_for_block` \| `duplicate_across_gaps` \| `parent_expansion_skipped:budget` \| null |

Vincoli: `finalRank` univoco per Gap; `inPackage = false` ⇒ `excludedReason` valorizzato; nessun UUID/hash/offset negli output verso il modello (la VOCE usa gli alias `E` di `alias-catalog-1`).

## Entità invariate della 002 usate (sola lettura)

| Entità | Uso nella 003 |
|---|---|
| `DocumentBlock` (`PreparationEntities.cs`) | Unità di ricerca (testo, hash, `HeadingPathJson`, `ParentId`, `LineStart/End`) |
| Evidenze di documento e loro fonti (`Evidence/*`, `DocumentEvidenceAggregate`) | Mappatura blocco→evidenza tramite `BlockId`+`Start/End`+`Quote` |
| `SourceSnapshot`, `SourceSnapshotItem` | Versioni incluse/escluse (filtro prima del ranking) |
| `AliasCatalog` (`alias-catalog-1`) | Alias `E` delle VOCI; la 003 non crea alias |
| `WorkOutput` JSONB, `ProcessingRun` | Ospita `retrieval-output-1` e il registro |
| Coverage-3 (additivo) | Campi `searchBasis`, `retrievalComplete`, `absenceProven` già previsti |

## Migrazioni

Una migrazione additiva (pgvector + 2 tabelle + indici), eseguita da `migrate` esplicito. Nessun CHECK su `Stage`/`SchemaVersion` dei batch (verificato dalla 002, 2026-11-09). Nessuna modifica alle tabelle esistenti.
