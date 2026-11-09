# M1 — prima misura reale (T019)

- Data: 2026-11-09. Autorizzazione del committente: solo embedding, solo T019 (nessun LLM generativo, riordino, load/unload esplicito, cambio di modello).
- Stato: misure (a)-(e) eseguite una volta, `baseline-m1.json` scritta. La valutazione di T020 è in fondo a questo documento (gate tecnico, non accettazione del prodotto).
- Id del modello nel router: **`bge-m3-Q5`** (configurazione `Retrieval:Embedding:ModelId`; nel router non esiste la sezione `[bge-m3]`). Artefatto atteso e osservato `bge-m3-Q5_K_M.gguf`, dimensione 1024, SHA-256 dei pesi NotVerified. Il default di prodotto `Retrieval:Embedding:ModelId` è stato allineato a `bge-m3-Q5` dopo la misura (decisione del committente).
- Primo tentativo con l'id `bge-m3`: HTTP 500 «failed to load» (id inesistente nel router); registrato per completezza.
- Comando: `BPROJECTS_TEST_DGX_ALLOW=1 BPROJECTS_EVAL_WRITE_BASELINE=1 dotnet test --project tests/bProjects.IntegrationTests --filter-class "*RetrievalEvaluationRealTests"` (5 min 34 s).
- Configurazione: `IndexConfigHash` 1c74f8a47666cce29fd987509caaa42810b2dadbc888f892b4fefe04b2d01a54; FTS italian (OR dei termini, ts_rank_cd); pool 50; RRF k=60; nessun riordino; hash gold 401ab635fe3ac1691683af773a030787938d5b2812bec227182657db35890a8a; hash del rapporto 32cf94f97e0a104b6461cd7e129c2969522878779553febe60adbdbf1bdd9976.
- Esito automatico del rapporto (regola D-2): soglie dichiarate del gate M1 (Recall@10 ibrido ≥ 95% e perdita di mappatura = 0) rispettate sul gold sintetico, con riserva per assenza di corpus anonimizzato e query indipendenti. Non è un'accettazione.
- 002:T138 e 002:T150 aperti.

## N4-fixtures (4 documenti, 14 blocchi, 12 query)

| Livello / segnale | Query | Recall@1 | @5 | @10 | @20 | @50 | Hit@1 | Hit@5 | Hit@10 | Tutti i riscontri @10 | Primo riscontro (media) | Mai trovato |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Blocco, lessicale | 12 | 91.7% | 100.0% | 100.0% | 100.0% | 100.0% | 91.7% | 100.0% | 100.0% | 100.0% | 1.17 | 0 |
| Blocco, denso | 12 | 66.7% | 100.0% | 100.0% | 100.0% | 100.0% | 66.7% | 100.0% | 100.0% | 100.0% | 1.67 | 0 |
| Blocco, RRF | 12 | 75.0% | 100.0% | 100.0% | 100.0% | 100.0% | 75.0% | 100.0% | 100.0% | 100.0% | 1.33 | 0 |
| Evidenza, RRF (dopo mappatura) | 12 | 75.0% | 100.0% | 100.0% | 100.0% | 100.0% | 75.0% | 100.0% | 100.0% | 100.0% | 1.33 | 0 |

Perdita di mappatura blocco→evidenza: 0. Citazioni invalide: 0. Riscontri Message sempre inclusi: 0/0. Conflitti valutati: 0; persi (almeno un lato fuori dai primi 10): 0.

## T082-single (1 documenti, 3116 blocchi, 19 query)

| Livello / segnale | Query | Recall@1 | @5 | @10 | @20 | @50 | Hit@1 | Hit@5 | Hit@10 | Tutti i riscontri @10 | Primo riscontro (media) | Mai trovato |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Blocco, lessicale | 19 | 36.8% | 68.4% | 68.4% | 73.7% | 81.6% | 42.1% | 68.4% | 68.4% | 68.4% | 5.00 | 3 |
| Blocco, denso | 19 | 86.8% | 100.0% | 100.0% | 100.0% | 100.0% | 94.7% | 100.0% | 100.0% | 100.0% | 1.05 | 0 |
| Blocco, RRF | 19 | 60.5% | 97.4% | 100.0% | 100.0% | 100.0% | 68.4% | 100.0% | 100.0% | 100.0% | 1.37 | 0 |
| Evidenza, RRF (dopo mappatura) | 19 | 60.5% | 97.4% | 100.0% | 100.0% | 100.0% | 68.4% | 100.0% | 100.0% | 100.0% | 1.37 | 0 |

Perdita di mappatura blocco→evidenza: 0. Citazioni invalide: 0. Riscontri Message sempre inclusi: 0/0. Conflitti valutati: 2; persi (almeno un lato fuori dai primi 10): 0.

## T083-multi (3 documenti, 3163 blocchi, 25 query)

| Livello / segnale | Query | Recall@1 | @5 | @10 | @20 | @50 | Hit@1 | Hit@5 | Hit@10 | Tutti i riscontri @10 | Primo riscontro (media) | Mai trovato |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Blocco, lessicale | 23 | 41.3% | 73.9% | 82.6% | 95.7% | 100.0% | 52.2% | 73.9% | 82.6% | 82.6% | 5.35 | 0 |
| Blocco, denso | 23 | 63.0% | 100.0% | 100.0% | 100.0% | 100.0% | 73.9% | 100.0% | 100.0% | 100.0% | 1.43 | 0 |
| Blocco, RRF | 23 | 63.0% | 100.0% | 100.0% | 100.0% | 100.0% | 73.9% | 100.0% | 100.0% | 100.0% | 1.48 | 0 |
| Evidenza, RRF (dopo mappatura) | 23 | 63.0% | 100.0% | 100.0% | 100.0% | 100.0% | 73.9% | 100.0% | 100.0% | 100.0% | 1.48 | 0 |

Perdita di mappatura blocco→evidenza: 0. Citazioni invalide: 0. Riscontri Message sempre inclusi: 4/4. Conflitti valutati: 3; persi (almeno un lato fuori dai primi 10): 0.

## Limiti noti

- Dati sintetici del laboratorio (nessun dato cliente), riempitivo povero, query di un solo autore: nessuna indipendenza delle query, nessun corpus anonimizzato.
- Parser finto a paragrafi (TXT, Markdown con titoli, DOCX): ~3,1k blocchi per corpus, analoghi alla variante «line» del laboratorio, NON i ~460 blocchi Docling di T082 del laboratorio: il confronto con le cifre del laboratorio non è a parità di granularità. Nessun PDF.
- Una evidenza per blocco (proxy di un ledger completo): la mappatura blocco→evidenza è 1:1 per costruzione, quindi una perdita pari a 0 NON misura la copertura dell'estrazione reale (R-3).
- I riscontri Message (dichiarazioni del PM) sono sempre inclusi e non classificati: esclusi da Recall@k; verificati come presenti nel pacchetto.
- Il segnale lessicale è PostgreSQL FTS italiano reale (OR dei termini), diverso dal BM25 del laboratorio: il confronto è riportato nel supplemento (R-1).
- Nessun riordino, nessun padre, nessun LLM (M1). SHA-256 dei pesi del modello non verificato (NotVerified); nessuna garanzia di completezza: il Recall è misurato solo sul gold annotato.
- Embedding quantizzati (Q5_K_M) serviti da un router condiviso: i valori denso/RRF possono variare in modo non deterministico fra esecuzioni; la baseline dichiarata è questa misura, non una garanzia di riproducibilità bit a bit.

Misure non deterministiche (fuori dall'hash): indicizzazione a freddo 241,3 s; ricerca per query (media) 122,2 ms. Tempi con embedding reali su PostgreSQL di test e router condiviso: indicativi, non un requisito prestazionale.

## (e) Readiness reale

- Sonda iniziale: stato ModelUnloaded; modello bge-m3-Q5; artefatto atteso bge-m3-Q5_K_M.gguf, osservato n/d; dimensione osservata n/d; SHA-256 NotVerified.
- prima richiesta di embedding dopo «ModelUnloaded»: 1,3 s (caricamento on-demand del router incluso).
- Sonda finale: stato Ready; modello bge-m3-Q5; artefatto atteso bge-m3-Q5_K_M.gguf, osservato bge-m3-Q5_K_M.gguf; dimensione osservata 1024; SHA-256 NotVerified; verificata alle 2026-11-09 21:18:18Z.
- Servizi: PostgreSQL 18.6 (Debian 18.6-1.pgdg12+2) on x86_64-pc-linux-gnu; pgvector 0.8.7; embedding: router llama.cpp interno (host non riportato), modello bge-m3, formato GGUF Q5_K_M, richieste a batch di 4

## (a) Lessicale PostgreSQL FTS italiano vs BM25 del laboratorio (e confronto denso/RRF)

Livello blocco. Il laboratorio usa varianti di chunking diverse (N4 line_text, T082 **docling_text ~460 blocchi**, T083 line_context): il confronto NON è a parità di granularità per T082 (qui ~3,1k blocchi a paragrafi). Differenze in punti percentuali (reale − laboratorio).

| Corpus | Segnale | Recall@1 (PG / lab / Δ) | Recall@10 (PG / lab / Δ) | Hit@1 (PG / lab / Δ) |
|---|---|---|---|---|
| N4-fixtures | lessicale PG FTS vs BM25 | 91.7% / 83.3% / +8.3 pt | 100.0% / 100.0% / 0.0 pt | 91.7% / 83.3% / +8.3 pt |
| N4-fixtures | denso bge-m3 | 66.7% / 75.0% / -8.3 pt | 100.0% / 100.0% / 0.0 pt | 66.7% / 75.0% / -8.3 pt |
| N4-fixtures | RRF | 75.0% / 83.3% / -8.3 pt | 100.0% / 100.0% / 0.0 pt | 75.0% / 83.3% / -8.3 pt |
| T082-single | lessicale PG FTS vs BM25 | 36.8% / 55.3% / -18.4 pt | 68.4% / 78.9% / -10.5 pt | 42.1% / 63.2% / -21.1 pt |
| T082-single | denso bge-m3 | 86.8% / 7.9% / +78.9 pt | 100.0% / 44.7% / +55.3 pt | 94.7% / 10.5% / +84.2 pt |
| T082-single | RRF | 60.5% / 36.8% / +23.7 pt | 100.0% / 81.6% / +18.4 pt | 68.4% / 42.1% / +26.3 pt |
| T083-multi | lessicale PG FTS vs BM25 | 41.3% / 69.3% / -28.0 pt | 82.6% / 100.0% / -17.4 pt | 52.2% / 80.0% / -27.8 pt |
| T083-multi | denso bge-m3 | 63.0% / 71.3% / -8.3 pt | 100.0% / 100.0% / 0.0 pt | 73.9% / 84.0% / -10.1 pt |
| T083-multi | RRF | 63.0% / 83.3% / -20.3 pt | 100.0% / 100.0% / 0.0 pt | 73.9% / 96.0% / -22.1 pt |

## (c) Recall a livello di blocco vs evidenza dopo mappatura (R-3)

| Corpus | Recall@10 blocco RRF | Recall@10 evidenza RRF | Perdita di mappatura | Citazioni invalide | Conflitti persi / valutati |
|---|---:|---:|---:|---:|---:|
| N4-fixtures | 100.0% | 100.0% | 0 | 0 | 0 / 0 |
| T082-single | 100.0% | 100.0% | 0 | 0 | 0 / 2 |
| T083-multi | 100.0% | 100.0% | 0 | 0 | 0 / 3 |

Avvertenza: la mappatura è 1:1 per costruzione (una evidenza per blocco), quindi la perdita misurata verifica la pipeline, non la copertura dell'estrazione reale.

## (d) Tempo di indicizzazione a freddo (embedding reali, batch da 4, modello già caricato)

stima: 6293 blocchi, 45,4 ms/blocco su 16 blocchi di prova ⇒ ~4,8 min di indicizzazione.

| Corpus | Blocchi | Secondi | Blocchi/s |
|---|---:|---:|---:|
| N4-fixtures | 14 | 0,9 | 15,2 |
| T082-single | 3116 | 121,6 | 25,6 |
| T083-multi | 3163 | 118,8 | 26,6 |

Ricerca ibrida completa per query (embedding della query via rete + lessicale + denso + RRF): n=56, p50 122,9 ms, p95 143,4 ms.

## (b) Scansione esatta vs HNSW sul segnale denso

Metodo: i vettori delle query sono calcolati una volta; si misura la SOLA query SQL densa (stessi filtri del prodotto, pool 50), 3 giri dopo uno di riscaldamento sulle 56 query dei tre corpora, su un database con i tre progetti (~6,3k voci). L'indice HNSW è creato solo nel database temporaneo di test (nessuna migrazione, nessun codice di prodotto toccato). HNSW (m=16, ef_construction=64: default pgvector) creato in 3,25 s, indice di 49984 KiB, per tutti i progetti del database di prova.

| Variante | HNSW nel piano | p50 ms | p95 ms | Media ms | Overlap@10 vs esatta | Overlap@50 vs esatta | Query con meno risultati dell'esatta | Recall@10 denso (N4 / T082 / T083) | Hit@1 denso (N4 / T082 / T083) |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| esatta (scansione, nessun indice HNSW) | no | 61,25 | 76,84 | 59,72 | 100.0% | 100.0% | 0 | 100.0% / 100.0% / 100.0% | 66.7% / 94.7% / 73.9% |
| HNSW, impostazioni predefinite (ef_search 40), piano scelto dal planner | no | 59,93 | 73,40 | 57,51 | 100.0% | 100.0% | 0 | 100.0% / 100.0% / 100.0% | 66.7% / 94.7% / 73.9% |
| HNSW, piano forzato (enable_seqscan=off), ef_search 40 | no | 61,22 | 76,41 | 60,30 | 100.0% | 100.0% | 0 | 100.0% / 100.0% / 100.0% | 66.7% / 94.7% / 73.9% |
| HNSW, piano forzato, ef_search 200 | no | 48,95 | 85,55 | 59,44 | 100.0% | 100.0% | 0 | 100.0% / 100.0% / 100.0% | 66.7% / 94.7% / 73.9% |
| HNSW, piano forzato, ef_search 200 + iterative_scan=relaxed_order | no | 60,70 | 75,01 | 59,07 | 100.0% | 100.0% | 0 | 100.0% / 100.0% / 100.0% | 66.7% / 94.7% / 73.9% |


### Lettura di (b): HNSW non confrontabile con questa query

In nessuna variante il planner ha usato l'indice `eval_hnsw`, nemmeno con `enable_seqscan=off`: il piano parte da `IX_RetrievalIndexEntries_BlockId_IndexConfigHash` e da bitmap/seq scan sui blocchi filtrati, poi ordina per distanza. Le latenze (p50 ~49-61 ms, p95 ~73-86 ms su ~6,3k voci) e le classifiche sono quindi quelle della scansione esatta; l'overlap 100% non dimostra nulla sull'HNSW. Per confrontare davvero servirebbe una query che permetta l'uso dell'indice (ordinamento per distanza con filtro di progetto non sargabile) o un indice parziale/per progetto: richiede modifica del codice di prodotto, non eseguita. Misurato: scansione esatta con ~6,3k voci, p50 61,25 ms, p95 76,84 ms (SOLA query SQL, vettori già calcolati); ricerca ibrida completa per query p50 122,9 ms, p95 143,4 ms. Non misurato: HNSW effettivo, scale maggiori.

## Query: 56 nel rapporto, 54 nelle tabelle aggregate (verifica documentale)

Le 56 query sono quelle del gold congelato (`Evaluation/retrieval-cases.json`: N4 = 12, T082 = 19, T083 = 25). Le 56 attraversano il motore di ricerca (latenza per query n = 56 e traccia del registro). Le tabelle aggregate di Recall/Hit contano solo le query con **almeno un riscontro documentale** (`docFindings.Count > 0` nel harness): 12 + 19 + 23 = 54. Le due escluse sono **T083 `P1` e `P2`** («Quale limite giornaliero di ordini / termine di pagamento ha comunicato il PM?»), il cui unico riscontro del gold è `pm-message.txt` (classificazione `Declared`): sono riscontri **Message**, che per progetto sono sempre offerti e non classificati dal recupero (limite già dichiarato) e verificati come presenti nel pacchetto (T083: 4/4). Nessun gold e nessun risultato è stato modificato; N4 non ne ha di esclusi.

## Deroga HNSW (decisione del committente, 2026-11-09)

La misura (b) **non è stata completata**: in nessuna variante il planner ha usato l'indice HNSW, quindi non esiste alcun risultato HNSW (l'overlap 100% è la scansione esatta confrontata con sé stessa). Il committente autorizza la deroga al confronto esatta vs HNSW per M1 e mantiene la scansione esatta; non si modifica la query né gli indici. Motivi dichiarati: esatta p50 ≈ 61 ms / p95 ≈ 77 ms sulla sola query densa, ~6,3k voci nei database di prova, Recall@10 RRF 100% sui corpus sintetici, nessuna evidenza attuale che giustifichi modifiche. Da rivalutare in una futura attività di ottimizzazione con volumi e filtri rappresentativi. La deroga non si estende ad altri gate della 002 o della 003.

## Valutazione T020 (gate tecnico M1) — 2026-11-09

Gate **tecnico**, su gold sintetico e con le riserve sotto. **Non è accettazione del prodotto** (nessuna decisione PM; SC-012 e corpus anonimizzato aperti).

| Criterio (tasks T020) | Dato misurato | Esito |
|---|---|---|
| Recall@10 ibrido sul gold congelato ≥ 95% | blocco RRF: N4 100%, T082 100%, T083 100% | **Soddisfatto** (corpus sintetici) |
| Perdita di mappatura blocco→evidenza = 0 | 0 su tutti i corpora | **Soddisfatto formalmente; non dimostra l'estrazione reale** (1 evidenza per blocco, 1:1 per costruzione); da misurare sul ledger reale prima di M3 |
| R-1: lessicale PG vs BM25 (regressione > 2 pt da spiegare) | T082 −10,5 pt, T083 −17,4 pt Recall@10 | **Regressione confermata, causa non isolata** (ipotesi: motore e granularità); non blocca il gate perché SC-002 è sull'ibrido |
| R-1: esatta vs HNSW | non eseguibile con la query attuale | **Derogato** dal committente; nessun risultato HNSW; esatta mantenuta |
| R-3, R-5 e R-1 in `research.md` | aggiornati con la misura | **Fatto** |
| Elenco delle deroghe | vedi sotto | **Fatto** |

Elenco delle deroghe e riserve (M1):
1. Confronto esatta vs HNSW non completato (derogato). Nessun valore HNSW esiste.
2. Corpus sintetici del laboratorio, query di un solo autore, nessun corpus anonimizzato (SC-012 aperto).
3. Parser finto a paragrafi (~3,1k blocchi), non i ~460 blocchi Docling di T082 del laboratorio: il confronto col lab non è a parità di granularità. Nessun PDF.
4. Mappatura 1:1 per costruzione: la perdita 0 non misura l'estrazione reale (002:T138, 002:T150 aperti).
5. Lessicale PG sotto il riferimento del laboratorio su T082/T083: regressione > 2 pt non spiegata da un esperimento a parità.
6. Hit@1 dell'RRF su T082 (68,4%) inferiore al denso da solo (94,7%): non è un criterio di M1; verifica in T026/T027.
7. SHA-256 dei pesi NotVerified (provisioning T046 al DGX admin).
8. Tempi misurati con il preset del router precedente alla riconfigurazione del 2026-11-09: indicativi, non requisiti.

Esito: nessuna condizione di STOP di T020 si è verificata (Recall@10 ibrido ≥ 95% e perdita di mappatura 0 sul gold sintetico). **M1 è chiusa come gate tecnico, con le deroghe e riserve elencate.** Non si dichiara verificato nulla che l'evidenza non dimostri: in particolare la copertura dell'estrazione reale e l'indipendenza delle query.
