# Contratto: indice derivato e registro dei candidati

**Feature**: `003-retrieval-evidence-search` | **Data**: 2026-11-09 | Spec: FR-004–FR-013, FR-007 | Entità: [data-model.md](../data-model.md)

## 1. Indice derivato

- **Unità**: blocco figlio (`DocumentBlock`). Testo indicizzato = `index-text-1(CanonicalText)` (NFC, spazi normalizzati, **senza titolo ripetuto**).
- **Segnali**: lessicale (`tsvector` configurazione `italian`) e denso (`vector(1024)`, coseno, L2-normalizzato) sugli **stessi** blocchi. Fusione **RRF**, k = 60, pesi pari, tie-break per `BlockId` ascendente.
- **Filtri prima del ranking** (stessa query SQL): `ProjectId = @p`, `DocumentVersionId IN (versioni incluse dello snapshot)`, esclusioni scelte dal PM. Un passaggio escluso non compete mai al ranking (FR-005).
- **Identità**: `IndexConfigHash`; cambio di modello, artefatto, dimensione, configurazione FTS, `index-text-1` o parser ⇒ nuove voci. Run storici non dipendono dall'indice (il registro è congelato).
- **Costruzione**: lavoro idempotente dopo `Ready`; stato per (versione, config). `Ready` solo con una voce per blocco e `TextHash` coerente. Embedding non disponibile ⇒ `Failed` tipizzato, mai `Ready` parziale.
- **Ricostruzione**: dai soli `DocumentBlock`; insieme dei passaggi indicizzati identico (SC-006); comando esplicito, mai all'avvio.
- **Non autorevole**: nessun dato esiste solo nell'indice; cancellazione dell'indice non perde nulla.

## 2. Registro dei candidati (`retrieval-registry-1`)

Congelato nel `WorkOutput` `retrieval-output-1` del run, riferito allo snapshot congelato. JSON canonico, ordinamento stabile, hash di output.

```jsonc
{
  "contract": "retrieval-registry-1",
  "snapshotHash": "…",
  "indexConfigHash": "…", "policyVersion": "retrieval-policy-1", "policyHash": "…",
  "searchBasis": "retrieval",
  "outcome": "Package",            // Package | Diagnostic | Failed
  "retrievalComplete": true,
  "degraded": false,
  "causes": [],                    // codici tipizzati, ordinati
  "readiness": { "embedding": {"model":"…","artifact":"…","dimension":1024,"sha256Status":"NotVerified"},
                 "reranker":  {"model":"…","artifact":"…","sha256Status":"NotVerified"} },
  "gaps": [ {
    "gapEvidenceId": "…",           // identificativo canonico del Gap nel run (non esposto al modello)
    "query": "…",                   // testo canonico del Gap
    "candidates": [ {
      "blockId": "…", "documentName": "…", "documentVersion": 2, "section": "A > B",
      "range": [start, end], "textHash": "…",
      "lexicalRank": 3, "denseRank": 1, "rrfRank": 1, "finalRank": 1,
      "evidenceIds": ["…"], "origin": "ranked",
      "inPackage": true, "excludedReason": null
    } ]
  } ],
  "packageEvidence": ["…"],         // ordine finale deduplicato, quello consegnato allo stadio
  "excluded": [ {"evidenceId":"…","reason":"budget"} ]
}
```

Regole: (1) `finalRank` univoco per Gap; (2) `outcome = Diagnostic` ⇒ `degraded = true`, `retrievalComplete = false`, nessun `packageEvidence` consegnato; (3) `outcome = Failed` ⇒ almeno una causa; (4) nessun punteggio del riordino presentato come probabilità; (5) il registro è sufficiente a riprodurre l'input dello stadio (FR-010); (6) mai UUID, hash o offset verso il modello: la proiezione VOCE è di `GapStageInput`; (7) il registro non contiene testi dei passaggi oltre ciò che serve alla consultazione (si ricavano da blocchi/evidenze per `blockId`).

## 3. Motivi tipizzati

`embedding_unavailable`, `embedding_model_mismatch`, `embedding_dimension_mismatch`, `embedding_input_too_long`, `embedding_malformed_response`, `reranker_unavailable`, `reranker_malformed_response`, `retrieval_index_not_ready`, `retrieval_index_build_failed`. Costanti in un'unica classe di eccezione tipizzata accanto a `DocumentWorkException`; azione utente leggibile; prefisso testuale stabile in `ErrorMessage` (come 002).

## 4. API di lettura (additiva)

Estensione dell'endpoint di coverage/run esistente per Gap: elenco ordinato di candidati `{documentName, documentVersion, section, range, finalRank, inPackage, excludedReason}`, `searchBasis`, `retrievalComplete`, `degraded` e cause. Autorizzazione di progetto identica alla coverage; report v5/v6 invariati (campi assenti/omessi). Nessun campo tecnico (UUID, hash, offset assoluti) verso la UI oltre ai range già mostrati dalla vista delle fonti della 002.
