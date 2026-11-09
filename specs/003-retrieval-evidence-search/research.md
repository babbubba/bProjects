# Research: Recupero di evidenze documentali con ricerca ibrida e provenienza verificabile

**Feature**: `003-retrieval-evidence-search` | **Data**: 2026-11-09 | **Spec**: [spec.md](spec.md) | **Piano**: [plan.md](plan.md)

Fonti: [transition §F–H](../../docs/roadmap/slice-002-003-transition.md); [AI §13.7–13.8](../002-project-documents/contracts/ai-workflow.md) della 002; codice della 002 letto in sola lettura (`Documents/PreparationEntities.cs` `DocumentBlock`, `Evidence/DocumentEvidenceAggregate.cs`, `AI/GapStageInput.cs`, `AI/ModelRuntimeMonitor.cs`); branch `research/slice-003-lab` @ `f005c8d` (`report-finale.md`, `results/*`, in sola lettura). Le cifre del laboratorio sono **evidenze sperimentali su dati sintetici con query di un solo autore, non requisiti né accettazioni**. Nessuna misura nuova è stata eseguita per questo documento: dove serve una misura, la research la prescrive come task di milestone (R-1, R-3, R-6).

Legenda: **Decisione** = adottata ora (vincolata da spec/costituzione/evidenze); **Da misurare** = scelta di default dichiarata + prova che può cambiarla; nessuna alternativa è scelta senza misura.

---

## R-1 — Motore d'indice (D7 della transizione)

**Decisione.** Baseline = **PostgreSQL 18** già presente (`infra/compose.yaml`, `postgres:18`): ricerca lessicale con `tsvector` + configurazione `italian` (indice GIN) **più** ricerca vettoriale con l'estensione **pgvector** (indice HNSW o scansione esatta, vedi sotto) sulla stessa tabella derivata di passaggi figli; fusione **RRF** (k = 60) calcolata nel backend sulle due classifiche complete. Nessun nuovo servizio, nessun motore dedicato.

**Rationale.** (a) Costituzione I (KISS/YAGNI) e IV (indici non autorevoli) e FR-032: un monolite modulare e nessun servizio nuovo. (b) Il filtro di progetto/versione/esclusione **prima** del ranking (FR-005) è una `WHERE` nella stessa query: in un motore separato servirebbe sincronizzare permessi e snapshot. (c) Scala dei corpora di validazione: documenti da centinaia di migliaia a milioni di caratteri (T083 generato con parametro 1.300.000; i conteggi esatti di passaggi sono nei file `indexes/*` del laboratorio). Che una scansione esatta filtrata per progetto basti, senza indice approssimato, è **un’ipotesi da misurare** (vedi sotto), non un dato. (d) Il laboratorio ha misurato **in memoria** (BM25 Python senza stemming, coseno senza BLAS): **non ha confrontato né scelto motori di persistenza**; la baseline PostgreSQL FTS italiano è quindi **un'ipotesi da misurare**, non un risultato.

**Da misurare (task di M1, prima di dichiarare il default).** Sullo stesso gold sintetico del laboratorio (stessi passaggi, stesse query): (1) Recall@10/Hit@1 del lessicale `ts_rank_cd`/`websearch_to_tsquery('italian')` rispetto al BM25 del laboratorio (78,9% Recall@10 su T082; 100% su T083 passaggi brevi): una perdita > 2 punti rispetto al riferimento del laboratorio è una **regressione da spiegare**, non un fallimento della decisione, perché le baseline sono diverse (SC-002 è misurato sulla *configurazione ibrida*, non sul solo lessicale); (2) scansione esatta (`<=>` con filtro) vs HNSW: latenza p50/p95 e Recall@10 a parità di gold; HNSW solo se la scansione esatta non rispetta il tempo dichiarato e la perdita di Recall è nulla sul gold; (3) tempo di indicizzazione a freddo (laboratorio: 122,5 s / 311.964 token per T082 righe, dominato dall'embedding, non dal database).

**Alternative considerate (non scelte).**
| Alternativa | Perché non ora |
|---|---|
| **Qdrant** (o altro vector DB) | Escluso salvo misura sul corpus bProjects che dimostri che PostgreSQL+pgvector non rispetta Recall@10/latenza dichiarati: nuovo servizio, nuova superficie operativa e di sicurezza (FR-032, IX: ADR proporzionato solo se la misura lo giustifica). Nessuna evidenza sperimentale lo richiede. |
| BM25 applicativo (libreria .NET o porting del lab) | Duplica il motore già nel database; il lab usa BM25 solo come baseline in memoria. Resta **riferimento di confronto** nei test di valutazione, non componente di prodotto. |
| Elasticsearch/OpenSearch | Servizio nuovo; nessuna evidenza che serva. |
| Solo denso o solo lessicale | Evidenza contraria: BM25 solo = 78,9% Recall@10 su T082; ibrido 100% (A-2). |
| Chunk grandi come unità | Evidenza contraria: Recall@10 44,7% / 33,3% (A-1). |

**Dipendenza strutturale da dichiarare.** L'estensione **pgvector richiede un'immagine PostgreSQL che la includa** (l'immagine `postgres:18` standard non la contiene) e una migrazione `CREATE EXTENSION vector`. È una **dipendenza di infrastruttura**: sottoposta a ADR proporzionato (Costituzione IX) come task di M1 (T002) e verificata con l'immagine reale nei test PG (Costituzione VIII). `infra/` non è toccata da questo piano; il task la tocca in implementazione.

**Rischio dichiarato.** La qualità dell'FTS italiano su contratti (stemming, stopword, numeri, sigle) è ignota: la misura di M1 la quantifica prima di costruire sopra. Se il lessicale PG fosse debole (Recall@10 ibrido < 95% sul gold), si valutano — **sempre con misura** — pre-normalizzazione di numeri/unità, configurazione `simple` + trigram, o un lessicale applicativo; **non** si cambia motore per ipotesi.

---

## R-2 — Unità di ricerca, unità di contesto e riuso della 002

**Decisione.** Unità di ricerca = **blocco figlio** originale (`DocumentBlock`: `Ref`, `Ordinal`, `ParentId`, `Type`, `CanonicalText`, `TextHash`, `HeadingPathJson`, `LineStart/LineEnd`, già in DB e immutabile). Unità di contesto = padre selettivo (R-6) ricavato da `ParentId`/`HeadingPath`, non indicizzato.

**Rationale.** Riuso integrale (A-1, A-5, A-6): i blocchi e gli intervalli UTF-16 `[start,end)` sono già persistiti dalla 002 con hash e provenienza; nessuna nuova fonte di verità. Il laboratorio ha già misurato il recupero su blocchi + padri (`parent-comparison.md`): Recall@10 100% su T082/T083.

**Verificato nel codice (lettura, non esecuzione).** Le fonti delle evidenze di documento hanno `BlockId`, `Start`, `End`, `Quote` verificata letteralmente sul testo del blocco (`DocumentEvidenceAggregate.cs` righe 42, 159, 314–315): il legame blocco → evidenza è **deterministico**.

**Alternative.** Indicizzare le *evidenze* (enunciato + citazione) invece dei blocchi: più compatto ma dipende dalla qualità dell'estrazione LLM (un fatto non estratto sarebbe irrecuperabile) e perderebbe il testo originale dei passaggi senza evidenza; scartato come unico indice. Può restare un **segnale aggiuntivo da misurare** in M1 solo se il Recall sui blocchi non basta (non pianificato: YAGNI).

---

## R-3 — Mappatura blocco → evidenza (il contratto dello stadio Gap è una lista di *evidenze*)

**Problema.** Il contratto d'ingresso §13.7 contiene **evidenze candidate** (VOCI con alias `E` di `alias-catalog-1`), non blocchi. Il laboratorio ha valutato il recupero su blocchi. Serve una regola deterministica per passare dal rango dei blocchi alla lista di evidenze.

**Decisione.** Il recupero ordina i **blocchi**; ogni blocco recuperato è risolto nelle **evidenze del ledger congelato che citano quel blocco** (`BlockId` in una fonte dell'evidenza), ordinate per rango del blocco migliore e poi per ordine di catalogo (tie-break deterministico). Un'evidenza con più blocchi prende il rango del migliore. I blocchi senza evidenza restano nel registro con motivo `no_evidence_for_block` (non entrano nel contesto: il contratto dello stadio resta «evidenze», FR-003/FR-025). Le evidenze `Message` (dichiarazioni PM) **non hanno blocchi**: non sono recuperabili per blocco; per non perdere il lato PM di un conflitto (FR-006) il pacchetto candidati include **sempre** le evidenze `Message` e Declared non-Gap dello snapshot entro budget, dichiarate come `origin = always_included` (regola già raccomandata dalla transizione §C «conflitto documento/PM non perso»); se non entrano nel budget → `retrievalComplete = false`.

**Rationale.** Preserva il contratto §13.7 invariato, usa la provenienza esistente, evita sorgenti di verità nuove. Il rischio principale è **la perdita per estrazione** (un fatto presente nel blocco ma non estratto come evidenza): la 002 garantisce estrazione esaustiva con copertura tracciata (FR-022), quindi il rischio è ridotto ma non nullo.

**Da misurare (task di M1).** Sul gold (T082/T083/N4), per ogni fatto: (a) Recall@k **a livello di blocco** e (b) **a livello di evidenza** dopo la mappatura, con la differenza imputata a «blocco senza evidenza»; il risultato dice se la mappatura introduce perdita. SC-002 è definito sui passaggi figli; la perdita di mappatura è riportata a parte e, se > 0 sui gold, diventa un finding che blocca l'ingresso in M3 (non una scelta di ripiego sull'indicizzare altro).

**Alternative.** Estendere il contratto dello stadio con passaggi grezzi: vietato (FR-025, §13.7: «sostituire solo la sorgente»). Indicizzare direttamente le evidenze: vedi R-2.

---

## R-4 — Modelli di embedding e riordino; pinning (D-5)

**Decisione.** Embedding `bge-m3` (artefatto GGUF Q5_K_M osservato dal laboratorio: `bge-m3-Q5_K_M.gguf`), **1.024 dimensioni**, normalizzato L2, coseno; reranker `bge-reranker-v2-m3` (artefatto `bge-reranker-v2-m3-Q5_K_M.gguf`), pooling `rank`, risposta `results[index, relevance_score]`. Entrambi su **infrastruttura interna** (router llama.cpp del DGX), mai servizi cloud (Costituzione V, FR-033). Nome, artefatto e dimensione vettori sono **configurazione fissata** e verificati dalla readiness (contratto in [model-readiness.md](contracts/model-readiness.md)); una difformità (modello assente, dimensione diversa, artefatto diverso) blocca l'uso con errore tipizzato. SHA-256 dei pesi: campo registrato al provisioning; vuoto ⇒ «non verificato» in ogni rapporto (il laboratorio ha confermato che le API non espongono l'hash).

**Rationale.** Sono i modelli misurati (A-2, A-3); il cambiamento di modello è un cambio di versione dell'indice (R-5). **Non** si adottano modelli alternativi senza misura.

**Da verificare in readiness (non assunto).** Chiamate `/v1/models`, `/props`, `/v1/embeddings`, `/v1/rerank` come osservate dal lab; batch HTTP di 4 input e limite di token per input (nessun troncamento lato client: input oltre il limite ⇒ errore tipizzato, non troncamento silenzioso); comportamento con `ModelUnloaded/Loading` (analogia T126).

---

## R-5 — Indice derivato, ricostruzione, invalidazione

**Decisione.** Una tabella derivata di *voci d'indice* per blocco (vedi [data-model.md](data-model.md)): chiave = (`BlockId`, `IndexConfigHash`), colonne `tsvector`, `vector(1024)`, `TextHash` del blocco indicizzato e metadati di filtro denormalizzati (`ProjectId`, `DocumentId`, `DocumentVersionId`). `IndexConfigHash` = hash di (nome+artefatto+dimensione del modello embedding, configurazione FTS, versione del testo da indicizzare `index-text-1`, versione parser). Cambio di uno qualsiasi ⇒ nuove voci; le vecchie sono eliminabili ma **non servono ai run storici** (il registro candidati contiene già tutto, FR-010). Ricostruzione = rilettura dei blocchi + embedding; il risultato per il testo è identico (SC-006); i vettori sono identici solo a parità di modello/artefatto.

**Rationale.** FR-007, A-6, Costituzione IV. Il testo indicizzato è il `CanonicalText` **senza titolo ripetuto** (A-5: il titolo ripetuto su ogni riga ha peggiorato BM25 78,9% → 73,7%); `HeadingPath` si usa come metadato e per il padre, non come testo ripetuto; eventuale prefisso di titolo è **un'opzione da misurare** (non adottata).

**Quando si costruisce.** Alla chiusura della preparazione di una versione (hook sul punto in cui la 002 passa la fonte a `Ready`) o a richiesta alla prima analisi che ne ha bisogno. **Da decidere con misura**: l'indicizzazione a freddo di T082 era ~ 120–130 s (embedding in gran parte); se avviene nella pipeline di preparazione allunga il tempo-a-pronto della fonte, se avviene alla richiesta di Analizza viola le deadline N/E della 002. Default del piano: **costruzione asincrona dopo `Ready` come lavoro idempotente separato**, con lo stato `IndexReady` verificato **prima** di instradare un run al recupero (indice non pronto ⇒ errore tipizzato `retrieval_index_not_ready`, zero invii LLM; mai fallback al ledger intero perché oltre budget). Non si tocca la macchina a stati delle fonti della 002 (Ready resta Ready): l'indice è una proiezione.

---

## R-6 — Cutoff, budget e padri selettivi (C3)

**Decisione.** (a) Per ogni Gap: interrogazione = testo del Gap (enunciato canonico, non UUID); candidati lessicale e denso fino a `N_pool` ciascuno (default da misurare; il laboratorio ha riordinato i primi 50 RRF); RRF k = 60; il riordino agisce sui primi `N_rerank` (default 50) e produce l'ordine finale; il pacchetto per lo stadio è composto **scendendo per rango** finché il budget di input dello stadio (`ContextBudget`, già in 002) non è esaurito, **con passaggi interi** (FR-012); gli esclusi per budget sono registrati con motivo. (b) **Unione fra Gap**: lo stadio Gap della 002 valuta tutti i Gap in una chiamata con un'unica lista di candidati: la lista è l'**unione deduplicata** dei candidati dei Gap, ordinata deterministicamente (rango migliore fra i Gap, poi ordine di catalogo), con il registro che conserva per ciascun Gap il proprio ordine. (c) **Padri**: l'espansione al padre è attivata *solo* quando il blocco recuperato ha `HeadingPath` non vuoto o `Type` che lo indica come frammento di clausola (da definire con misura) e **solo entro un budget di caratteri dedicato** (default da misurare; riferimento lab: l'espansione completa moltiplica ~14× il testo mediano dei primi 10, 2.028 → 28.963 caratteri su T082; ~3,7–4,2× sui chunk Docling); sopra budget non si espande e si registra `parent_expansion_skipped:budget`.

**Da misurare.** `N_pool`, `N_rerank`, cutoff e soglie di attivazione del padre sono **parametri da calibrare sul gold congelato** in M2/M4, poi congelati in una versione (`retrieval-policy-1`); cambiarli = nuova versione. **Nessun valore è dichiarato qui come ottimo.** I punteggi del riordino sono usati **solo come ordinamento** (FR-009): nessuna soglia di verità sui punteggi, nemmeno per il cutoff (il cutoff è per **numero/budget**, non per punteggio).

**`retrievalComplete`.** `false` se: errori del recupero; servizio degradato (riordino indisponibile); candidati con rango entro `N_rerank` esclusi per budget; evidenze `always_included` escluse per budget; blocchi del pool senza evidenza **non** rendono `false` (sono registrati). Mai `true` solo perché la lista è non vuota; mai convertito in «assente» (FR-019). *Sorgente `retrieval` ⇒ `absenceProven = false` sempre* (§13.7 già lo impone: `absenceProven` richiede `ledger_exhaustive`). **Conseguenza di prodotto da dichiarare**: con sorgente recupero un Gap non può mai risultare «assente provato»; solo «assenza non provata».

---

## R-7 — Degradazione dei servizi (D-3, modificata)

**Decisione normativa del committente.** Embedding indisponibile ⇒ errore tipizzato, zero invii LLM, nessun fallback silenzioso. Riordino indisponibile ⇒ ranking RRF **diagnostico**, `retrievalComplete = false`, nessuna valutazione Gap dichiarata completata.

**Lettura conservativa adottata dal piano (da confermare in tasks/analyze).** Poiché in modalità degradata non si possono emettere esiti definitivi e «nessun risultato parziale è presentato come valido», il piano **non invoca affatto lo stadio Gap LLM** quando il riordino è indisponibile: il run termina con causa tipizzata (`retrieval_degraded`/`reranker_unavailable`), **registro RRF diagnostico persistito e consultabile**, nessun `gapAssessments`, nessuna coverage di completamento. Così non esiste alcun esito da etichettare «provvisorio». Alternativa non adottata (richiederebbe una nuova decisione del committente): invocare lo stadio con candidati in ordine RRF producendo esiti marcati non definitivi; introdurrebbe un sottotipo di esito e di UI «provvisorio» non previsto dal §13.2.

**Nessun aumento automatico di budget né cambio di modello** (D-3): né per i budget del recupero né per i modelli (nessun "modello di embedding alternativo" configurato).

**Ripresa.** Se il servizio torna disponibile, un **nuovo** run (o la ripresa secondo la regola del tipo di errore, task tipizzato) riesegue la fase; il run fallito non cambia esito retroattivamente.

---

## R-8 — Integrazione con la 002: definizione, chiavi di freeze, routing

**Decisione.** La novità sta dietro una **nuova definizione congelata** `project-analysis-documents-v7` (la v6 della 002 resta com'è, con fail-fast `gap_ledger_exceeds_input`; la v5 e le precedenti invariate) con nuove chiavi di freeze `retrieval*` (elenco in [candidate-source.md](contracts/candidate-source.md)); `ContractOf` con insiemi chiusi come per v5/v6; ogni combinazione mista ⇒ `workflow_incompatible`. **Instradamento v7** = quello di §13.7 (a)/(b1)/(b2), con una sola differenza: nel ramo (b1), quando il ledger **non** entra nel budget dello stadio, invece del fail-fast si esegue il recupero (se `IndexReady` e servizi in readiness); quando il ledger entra, sorgente `ledger_exhaustive` come in v6 (FR-002). La scelta fra le due sorgenti è una **funzione deterministica** della misura già esistente (ledger + maggiorazione `gap-stage-ledger-1`) e dello stato dell'indice, registrata nel manifest.

**Perché una v7 e non una modifica della v6.** La v6 della 002 non è accettata ma è implementata e testata (T144–T149); modificarla riscriverebbe un contratto storico (FR-025/FR-031). La v7 aggiunge soltanto la sorgente; il resto (DTO `gap-stage-alias-1`, prompt `document-gap-v1`, `GapAssessmentRules`, assembly, coverage-3) è riusato **senza modifiche**.

**Punti in cui la 003 NON può partire prima di stabilizzazioni della 002** (elenco normativo del piano; dettagli in [plan.md §Dipendenze](plan.md#dipendenze-dalla-002-e-punti-di-blocco)):
1. **T150** (gate DGX «SC-004-Gap» dello stadio Gap con sorgente esaustiva): **non eseguito**. Spec 003 «Dipendenze»: lo stadio con sorgente esaustiva va verificato prima di sostituirne la sorgente. Blocca **M3** (integrazione nel run), non M1/M2.
2. **T138** (Gap oltre contesto): aperto; si chiude solo con evidenza (T150). Blocca la dichiarazione di accettazione della 003, non lo sviluppo di M1/M2.
3. **T126** (readiness bounded dei modelli): aperto, prova DGX «scaricato → disponibile» mancante. Il **riuso dello schema** di readiness è già in codice; la 003 definisce la propria readiness per embedding/riordino (M1/M2), ma non può dichiararla «analoga e provata» finché T126 non è chiuso: dichiarare la differenza.
4. **T082/T083/T090/T067/T142** (gate DGX/PM aperti): non bloccano la 003; la valutazione 003 sul **corpus T082/T083** usa il generatore e i gold esistenti in sola lettura e non dipende dall'esito dei loro run.
5. **Migrazioni PostgreSQL**: la 003 aggiunge una migrazione (indice derivato + registro candidati). Va serializzata con eventuali migrazioni della 002 ancora in arrivo; verificare l'ultima migrazione al momento di T003 della 003.

---

## R-9 — Valutazione continua C7: promuovere, non ricreare

**Decisione.** I **gold** (`corpus/T082-single/gold.json`, `corpus/T083-multi/gold.json`, `cases.json` e i 12 casi N4) e le **query** del laboratorio (`research/slice-003-lab`, @ `f005c8d`) sono **importati come fixture versionate di test** (copia con hash verificato, un file di provenienza che riporta commit e hash), **non riscritti** né ricreati come task applicativi; il laboratorio resta riferimento storico invariato. Le metriche (Recall@1/5/10/20/50, Hit@1/5/10, falsi `AnsweredInSnapshot`, conflitti persi, citazioni invalide) sono quelle del laboratorio (`experiment.py`/`audit.py`) **riscritte come funzioni di test deterministiche in C#** nel progetto di test esistente (nessun Python in CI di prodotto). Gli stessi dati servono sia a CI (embedding/riordino simulati e deterministici) sia a esecuzione reale su opt-in (`BPROJECTS_TEST_DGX_ALLOW=1`, stessa disciplina del Guard della 002). Contratto in [evaluation.md](contracts/evaluation.md).

**Baseline.** La baseline di regressione (D-1) è **la prima esecuzione ufficiale del gold congelato** sulla configurazione di riferimento, salvata con gli hash degli input; le soglie assolute (Recall@10 ≥ 95%, Hit@1 ≥ 95%, 0 falsi `AnsweredInSnapshot`) e la regressione ≤ 2 punti si confrontano **sullo stesso insieme di query**. Il riferimento del laboratorio (100%) non è la baseline: è un'evidenza di partenza su un motore diverso.

**Limiti dichiarati nel rapporto (FR-030).** Dati sintetici; riempitivo lessicalmente povero; query di un solo autore; mancano PDF, isolamento multi-progetto, versioni escluse e cutoff (la 003 li prova con test propri, SC-005); nessuna garanzia di completezza.

**Accettazione (D-2).** «Accettata» richiede il **corpus anonimizzato + ≥ 20 query di un secondo autore**; il piano prevede il task di valutazione indipendente come ultimo gate; senza di esso l'esito è «accettata con riserva» con lacuna esplicita (SC-012).

---

## R-10 — Altre scelte e scartati

| Tema | Decisione | Motivo |
|---|---|---|
| Pesi RRF | Parità (1/(k+rango)), k = 60 | Il lab non ha cercato iperparametri sui gold; pesi diversi solo con misura su gold separato (non il gold di accettazione). |
| Query per Gap | Testo canonico del Gap come query unica (lessicale + denso), con la stessa query al riordino | Il lab ha usato query naturali; query sintetizzate dall'LLM (HyDE/riscrittura) aggiungerebbero una chiamata LLM non prevista dal contratto (§13.7: **una** chiamata dello stadio): scartata, riconsiderabile solo con misura. |
| Numero di chiamate LLM | Nessuna nuova chiamata LLM della 003; il recupero usa solo embedding+riordino | Contratto 002 §13.7 invariato. |
| Sommari SKRAG, NLI come filtro/arbitro, selezione preventiva, knowledge graph, agenti | Esclusi (A-11) | Evidenze negative del lab. |
| Interfacce | Nessuna interfaccia con una sola implementazione: servizi concreti in `bProjects.App/Retrieval/` e client HTTP per i due modelli come tipi concreti; le sostituzioni nei test via test double dei client HTTP (stesso approccio di T126), non via nuove astrazioni | Costituzione I, FR-032, D-4. |
| MAF | Il recupero non è un agente: è un passo deterministico del piano che precede lo stadio; lo stadio e il checkpoint sono quelli MAF 1.26.0 della 002. Il checkpoint registra l'esito del recupero (batch/WorkOutput `retrieval-output-1`) come per ogni batch | Costituzione II–III: nessuna primitiva MAF nuova richiesta; verificare in M3 che il passo "recupero" sia ripreso/rieseguito senza duplicare costi (idempotenza su snapshot+indice). |
| PDF | Nessuna garanzia di qualità fuori perimetro | Spec edge case. |
