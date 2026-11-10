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
- Tentativi di richiesta ai modelli solo dopo la readiness (zero invii in caso contrario), **con l'unica eccezione di §3.1**.
- Il risultato della readiness (modello, artefatto, dimensione, `sha256Status`, versione servizio) è scritto nel registro e nel rapporto di valutazione.
### 3.1 Eccezione: attivazione on-demand con una sola sonda funzionale minima (2026-11-11)

**Evidenza** (smoke test reale `RetrievalEmbeddingSmokeRealTests`, DGX, una esecuzione): in router mode `bge-m3-Q5` risultava `sleeping` (⇒ `ModelUnloaded`); UNA richiesta ordinaria di embedding (`POST /v1/embeddings`, 1 input, 200, ~1,1 s) lo ha caricato e la readiness lo ha poi riconosciuto `Ready` (artefatto atteso, dimensione 1024). Senza alcuna richiesta, un modello `sleeping` non diventa mai pronto da solo (causa più probabile del fallimento d'indice del Run 2 di T040).

**Regola.** Nel solo percorso che richiede effettivamente il modello (`EnsureEmbeddingReadyAsync`, `EnsureRerankerReadyAsync`, `GetEmbeddingSnapshotForUseAsync` della pipeline, quindi costruzione dell'indice, ricerca e riordino), se il modello configurato e identificato è `ModelUnloaded`:

1. UNA richiesta funzionale minima al modello configurato (embedding: il testo «probe»; riordino: query «probe» e due candidati) — la stessa sonda funzionale della readiness, mai testo di contenuto;
2. invalidazione della cache e **nuova sonda completa**: identità, artefatto e, per l'embedding, dimensione 1024; si prosegue con dati reali **solo** se lo stato è `Ready`;
3. **single-flight per modello** (embedding e riordino hanno slot indipendenti, stessa politica); la cancellazione di un chiamante non interrompe l'attivazione condivisa; durata massima = `Retrieval:TimeoutSeconds`;
4. **nessun retry**: dopo un'attivazione non riuscita non se ne tenta un'altra finché non scade la cache (`RuntimeCheckSeconds`), poi al più una per chiamata;
5. esito sempre diagnosticabile: `LastEmbeddingActivation`/`LastRerankerActivation` (stato prima/dopo, esito, codice tipizzato, durata) e nota nel messaggio dell'errore tipizzato (`embedding_unavailable`, `reranker_unavailable`, …); nessuna chiave né testo di contenuto.

**Limiti (invariati).** Stati `ModelMissing`, `ArtifactMismatch`, `DimensionMismatch`, `Unverified`, `ModelLoading` e `Unreachable`: **nessuna attivazione**. Le interrogazioni passive (`GetEmbeddingSnapshotAsync`, `GetRerankerSnapshotAsync`, `WaitUntil*ReadyAsync`, `Current*`) restano passive: non inviano mai la sonda di attivazione. Nessun endpoint amministrativo di caricamento/scaricamento, nessun download, nessun fallback verso altri modelli. `ModelUnloaded` non è mai equivalente a `Ready`.

**Compatibilità con D-3.** D-3 vieta *caricamento forzato/unload* e *cambio di modello per aggirare un errore*. La sonda minima è una richiesta ordinaria al modello configurato: il caricamento resta una scelta del router (preset, tempo di inattività), il prodotto non lo comanda né lo scarica, non cambia modello e non aumenta budget. È quindi un'eccezione esplicita e limitata alla *prima richiesta* di un modello già configurato, non un'altra politica di degradazione. Resta DGX-provata solo per la parte osservata dallo smoke test; riordino e ritorno a `sleeping` sono provati DET (router finto).

- Prova funzionale «scaricato → disponibile», `loading`, `unreachable`, modello assente: **DGX**, collegata a `002:T126` e ai gate T019/T026; fino ad allora la readiness 003 è DET (server finto) e **non** va dichiarata «provata» su infrastruttura reale.
