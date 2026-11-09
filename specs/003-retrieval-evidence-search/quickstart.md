# Quickstart: verifica della slice 003 per milestone

**Stato**: guida di validazione **non eseguita**: nessun codice esiste. Ogni scenario indica livello, prerequisiti ed esito atteso; non implica che il comando oggi funzioni. Comandi dal manuale del repository (CLAUDE.md); dettagli dei dati in [data-model.md](data-model.md) e dei contratti in [contracts/](contracts/).

## Prerequisiti comuni

```bash
dotnet restore backend/bProjects.sln --locked-mode
dotnet build backend/bProjects.sln --no-restore
# PostgreSQL con pgvector (dopo T003):
export BPROJECTS_TEST_POSTGRES="<connection string del database di test>"
docker compose -f infra/compose.yaml run --rm app migrate
```

Senza variabili d'ambiente i test PG/DGX risultano *ignorati*, mai superati. I test reali richiedono anche `BPROJECTS_TEST_DGX_ENDPOINT/_MODEL/_API_KEY` (generativo) e gli endpoint dei modelli di retrieval (nomi in `Retrieval:*`, T005) **e** `BPROJECTS_TEST_DGX_ALLOW=1`, impostato solo per un'esecuzione autorizzata.

## M1 — Indice, ricerca ibrida e registro dei candidati (PRIMO IMPLEMENTABILE)

1. **Gold congelato (DET)**: `dotnet test --solution backend/bProjects.sln --filter-trait "Category=RetrievalEval"` → i hash di `Retrieval/Gold/` coincidono con `PROVENANCE.json` (T004). Atteso: PASS; un file modificato → FAIL.
2. **Indice e ricostruzione (PG)**: costruire l'indice del corpus sintetico (T082/T083/N4), poi cancellarlo e ricostruirlo. Atteso: stesso insieme di (`BlockId`, `TextHash`) (SC-006); embedding non disponibile → stato `Failed` tipizzato, nessuna voce `Ready` parziale (T010/T011).
3. **Isolamento (PG)**: due progetti simili + versione esclusa → nessun passaggio fuori ambito in lessicale, denso e registro (SC-005, T016).
4. **Registro (DET+PG)**: per una query, il registro riporta ranghi lessicale/denso/RRF/finale, provenienza completa, evidenze mappate, blocchi senza evidenza, `retrievalComplete` secondo [candidate-source §3](contracts/candidate-source.md#3-retrievalcomplete) (T015).
5. **Valutazione simulata (PG)**: rapporto con modalità «simulata», provenienza dei dati e limiti (T017/T018).
6. **Misura reale (DGX, autorizzata)**: baseline `baseline-m1.json` e `validation/M1-real.md` con Recall@k, confronto lessicale PG vs BM25 del laboratorio, esatto vs HNSW, Recall blocco vs evidenza, costo a freddo (T019). **Gate T020**: Recall@10 ≥ 95% e perdita di mappatura = 0, altrimenti STOP.

## M2 — Riordino, modalità degradata e budget

1. **Degradazione (DET+PG)**: matrice embedding/riordino/indice (T024). Atteso: embedding ko → `embedding_unavailable`, 0 invii; riordino ko → registro RRF `Diagnostic`, `retrievalComplete=false`, nessun pacchetto; indice non pronto → `retrieval_index_not_ready`.
2. **Budget (DET)**: passaggi interi, esclusi elencati con motivo, nessun troncamento (T025).
3. **Misura reale (DGX, autorizzata)**: Hit@1 con riordino ≥ 95% e Recall@10 ≥ 95%, latenza p50/p95, regressione ≤ 2 punti rispetto a M1 (T026/T027).

## M3 — Integrazione nello stadio Gap

*Solo con `002:T150` PASS (o deroga del committente).*

1. **Routing v7 (DET+PG+MAF-PG)**: ledger entro budget → sorgente esaustiva come 002; ledger oltre budget con Gap → recupero; senza Gap → gerarchia v4-equivalente; v2–v6 byte-identici (T028).
2. **Percorso offline completo (MAF-PG, modello finto)**: progetto con ledger oltre budget e Gap annotati; nessun `gap_ledger_exceeds_input`; una sola chiamata dello stadio; coverage con `searchBasis=retrieval`, `absenceProven=false` (T036, T032).
3. **Errori tipizzati**: embedding ko → `Failed`, 0 chiamate LLM; riordino ko → nessuno stadio, registro diagnostico consultabile (T031, T039).
4. **UI (Vitest + axe, E2E-OFFLINE)**: candidati ordinati, «assenza non provata», messaggio di mancato completamento (T034/T035).
5. **Regressione 002 (US4)**: `dotnet test --solution backend/bProjects.sln` invariato e verde; golden v5/v6 invariati (T037).
6. **Gate reale (DGX, autorizzato, T040)**: criteri SC-001/SC-003/SC-004; esiti non favorevoli conservati.

## M4 / M5

Padri selettivi: misura con/senza (T042) e decisione (T043). CI di regressione e rapporto di accettazione (T044, T049): «accettata» solo con corpus anonimizzato e ≥ 20 query di un secondo autore (T048); altrimenti **«accettata con riserva»** con la lacuna evidenziata, non equivalente all'accettazione piena.

## Cosa NON dimostra

Dati sintetici e query di un solo autore (fino a T048); hash dei pesi «non verificato» (fino a T046); nessuna garanzia di completezza; PDF non provato; latenze di laboratorio non rappresentative di .NET/pgvector (si rimisurano in T019/T045).
