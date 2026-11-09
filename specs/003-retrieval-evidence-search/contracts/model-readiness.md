# Contratto: readiness dei modelli di embedding e riordino

**Feature**: `003-retrieval-evidence-search` | **Data**: 2026-11-09 | Spec: FR-033, FR-034, FR-020, FR-035; decisione D-5 | Analoga a `002:T126` (`ModelRuntimeMonitor`, `RuntimeReadiness`)

## 1. Modelli e pinning (D-5)

| Modello | Nome | Artefatto atteso (osservato dal laboratorio) | Parametri fissati |
|---|---|---|---|
| Embedding | `bge-m3` | `bge-m3-Q5_K_M.gguf` | dimensione vettori **1024**, L2-normalizzato, coseno; batch HTTP 4; nessun troncamento lato client |
| Riordino | `bge-reranker-v2-m3` | `bge-reranker-v2-m3-Q5_K_M.gguf` | pooling `rank`; risposta `results[index, relevance_score]` |

Nome, artefatto e dimensione sono **configurazione fissata** (`Retrieval:*`); valori diversi dal runtime ⇒ non pronto. Il valore osservato dal laboratorio non è un requisito di prodotto: è il punto di partenza da confermare in T019/T026 contro l'endpoint reale. Servizi **solo** su infrastruttura interna (host interno validato all'avvio); nessun fallback cloud.

**SHA-256 dei pesi**: campo `Retrieval:*:Sha256`, registrato al provisioning del DGX (T046). Vuoto ⇒ `sha256Status = NotVerified` in readiness, registro e rapporti; non è causa di blocco (le API non lo espongono) ma va dichiarato. Se valorizzato e il runtime non può dimostrarlo, resta `NotVerified` (nessun falso «Verified»).

## 2. Stati

Riuso dello schema di T126 (non duplicato) più due stati specifici del contenuto del modello:

`Ready` · `ModelUnloaded` · `ModelLoading` · `Unreachable` · `ModelMissing` · `Unverified` (il server risponde ma nome/artefatto non dimostrabili) · **`ArtifactMismatch`** · **`DimensionMismatch`** (embedding: prima risposta di prova ≠ 1024).

Mappatura verso gli errori tipizzati del recupero: embedding `Ready` richiesto per costruire l'indice e per interrogare; qualsiasi altro stato ⇒ `embedding_unavailable`/`embedding_model_mismatch`/`embedding_dimension_mismatch`; riordino non `Ready` ⇒ `reranker_unavailable` (modalità degradata diagnostica, vedi [candidate-source §5](candidate-source.md#5-modalità-degradata-e-errori-tipizzati-d-3)).

## 3. Comportamento

- Sonda a richiesta con cache a TTL e una sola sonda in volo; deadline del chiamante rispettata; **nessun caricamento forzato/unload** dei modelli e nessun cambio di modello per aggirare un errore (D-3).
- Tentativi di richiesta ai modelli solo dopo la readiness (zero invii in caso contrario).
- Il risultato della readiness (modello, artefatto, dimensione, `sha256Status`, versione servizio) è scritto nel registro e nel rapporto di valutazione.
- Prova funzionale «scaricato → disponibile», `loading`, `unreachable`, modello assente: **DGX**, collegata a `002:T126` e ai gate T019/T026; fino ad allora la readiness 003 è DET (server finto) e **non** va dichiarata «provata» su infrastruttura reale.
