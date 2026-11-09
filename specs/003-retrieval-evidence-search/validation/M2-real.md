# M2 — misura reale del riordino (003 T026)

Questo rapporto è una MISURA descrittiva: non contiene alcun giudizio di gate. Il gate di chiusura M2 è T027 e spetta al committente.

## Configurazione

- Politica `retrieval-policy-1` (hash `0c7049270c3960b8ca981a51fa6ab2ea0ceb24fccb4b978fa365d7d84d4f2245`): pool 50, riordino dei primi 50 della RRF, k RRF 60. `IndexConfigHash` 1c74f8a47666cce29fd987509caaa42810b2dadbc888f892b4fefe04b2d01a54.
- Embedding: stato Ready; modello bge-m3-Q5; artefatto atteso bge-m3-Q5_K_M.gguf, osservato bge-m3-Q5_K_M.gguf; dimensione osservata 1024; SHA-256 NotVerified.
- Riordino, stato iniziale: stato ModelUnloaded; modello bge-reranker-v2-m3; artefatto atteso bge-reranker-v2-m3-Q5_K_M.gguf, osservato n/d; dimensione osservata n/d; SHA-256 NotVerified.
- Riordino, stato finale: stato Ready; modello bge-reranker-v2-m3; artefatto atteso bge-reranker-v2-m3-Q5_K_M.gguf, osservato bge-reranker-v2-m3-Q5_K_M.gguf; dimensione osservata n/d; SHA-256 NotVerified.
- Servizi: PostgreSQL 18.6 (Debian 18.6-1.pgdg12+2) on x86_64-pc-linux-gnu; pgvector 0.8.7; router llama.cpp interno (host non riportato): embedding bge-m3 (GGUF Q5_K_M) e riordino bge-reranker-v2-m3 (GGUF Q5_K_M)
- Caso «riordino non raggiungibile» (nessun comando di load/unload; il modello era scaricato e il router lo ha caricato alla prima richiesta di riordino ordinaria e minima di 2 documenti inviata dall'harness): stato iniziale del riordino: stato ModelUnloaded; modello bge-reranker-v2-m3; artefatto atteso bge-reranker-v2-m3-Q5_K_M.gguf, osservato n/d; dimensione osservata n/d; SHA-256 NotVerified; esito del pipeline: Diagnostic; stadio invocabile: False; cause: [reranker_unavailable].
- Input (hash di gold, documenti e query) identici a quelli di M1.

## Metriche a livello di blocco (solo riscontri documentali)

| Corpus | Query | Hit@1 RRF | Hit@1 riordino | Recall@1 RRF | Recall@1 riordino | Recall@5 RRF | Recall@5 riordino | Recall@10 RRF | Recall@10 riordino |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| N4-fixtures | 12 | 75.0% | 83.3% | 75.0% | 83.3% | 100.0% | 100.0% | 100.0% | 100.0% |
| T082-single | 19 | 73.7% | 100.0% | 65.8% | 92.1% | 97.4% | 100.0% | 100.0% | 100.0% |
| T083-multi | 23 | 73.9% | 73.9% | 63.0% | 63.0% | 100.0% | 100.0% | 100.0% | 100.0% |
| ALL | 54 | 74.1% | 85.2% | 66.7% | 77.8% | 99.1% | 100.0% | 100.0% | 100.0% |

## Confronto con M1 (`baseline-m1.json`, ordine RRF); riferimento T027: variazione peggiorativa oltre 2 pt su Hit@1 e Recall@10

| Corpus | Metrica | M1 (RRF) | Questa esecuzione, RRF | Questa esecuzione, riordino | Riordino − M1 | RRF (stessa esecuzione) − M1 | Peggioramento > 2 pt (indicazione) |
|---|---|---:|---:|---:|---:|---:|---|
| N4-fixtures | hit@1 | 75.0% | 75.0% | 83.3% | +8.3 pt | 0.0 pt | no |
| T082-single | hit@1 | 68.4% | 73.7% | 100.0% | +31.6 pt | +5.3 pt | no |
| T083-multi | hit@1 | 73.9% | 73.9% | 73.9% | 0.0 pt | 0.0 pt | no |
| ALL | hit@1 | 72.2% | 74.1% | 85.2% | +13.0 pt | +1.9 pt | no |
| N4-fixtures | recall@1 | 75.0% | 75.0% | 83.3% | +8.3 pt | 0.0 pt | n/a |
| T082-single | recall@1 | 60.5% | 65.8% | 92.1% | +31.6 pt | +5.3 pt | n/a |
| T083-multi | recall@1 | 63.0% | 63.0% | 63.0% | 0.0 pt | 0.0 pt | n/a |
| ALL | recall@1 | 64.8% | 66.7% | 77.8% | +13.0 pt | +1.9 pt | n/a |
| N4-fixtures | recall@5 | 100.0% | 100.0% | 100.0% | 0.0 pt | 0.0 pt | n/a |
| T082-single | recall@5 | 97.4% | 97.4% | 100.0% | +2.6 pt | 0.0 pt | n/a |
| T083-multi | recall@5 | 100.0% | 100.0% | 100.0% | 0.0 pt | 0.0 pt | n/a |
| ALL | recall@5 | 99.1% | 99.1% | 100.0% | +0.9 pt | 0.0 pt | n/a |
| N4-fixtures | recall@10 | 100.0% | 100.0% | 100.0% | 0.0 pt | 0.0 pt | no |
| T082-single | recall@10 | 100.0% | 100.0% | 100.0% | 0.0 pt | 0.0 pt | no |
| T083-multi | recall@10 | 100.0% | 100.0% | 100.0% | 0.0 pt | 0.0 pt | no |
| ALL | recall@10 | 100.0% | 100.0% | 100.0% | 0.0 pt | 0.0 pt | no |

## Latenza per query (n=56, tutte le query del gold, anche senza riscontri documentali)

| Fase | p50 ms | p95 ms | media ms | max ms |
|---|---:|---:|---:|---:|
| totale (embedding query + ricerca + riordino) | 551,4 | 626,3 | 502,5 | 894,8 |
| ricerca (embedding query + lessicale + denso + RRF) | 140,9 | 169,9 | 136,7 | 172,1 |
| solo riordino | 407,3 | 489,9 | 365,7 | 772,2 |

## Limiti noti

- Dati sintetici del laboratorio (nessun dato cliente); query di un solo autore, tutte usate per la misura: nessuna indipendenza delle query, nessun corpus anonimizzato (SC-012).
- Parser finto a paragrafi (TXT, Markdown, DOCX), ~3,1k blocchi per corpus; non i ~460 blocchi Docling del laboratorio. Nessun PDF.
- Una evidenza per blocco: la mappatura è 1:1 per costruzione; le metriche sono a livello di BLOCCO e sui soli riscontri documentali (i riscontri Message sono esclusi da Recall@k).
- Servizi quantizzati (Q5_K_M) su router condiviso: i valori possono variare fra esecuzioni; SHA-256 dei pesi non verificato. Le latenze sono indicative, la prima richiesta può includere il caricamento on-demand del modello.
- Nessun troncamento dei testi e nessuna soglia sui punteggi del riordino. Il Recall è misurato solo sul gold annotato, senza garanzia di completezza.
- Nessun giudizio di gate: l'esito di T027 spetta al committente.

## Osservazioni per T027 (non è un giudizio di gate)

- Recall@10 con riordino 100% su tutti i corpora, nessuna regressione rispetto a M1 oltre 2 pt.
- Hit@1 con riordino: ALL 85,2% (T082 100%, N4 83,3%, T083 73,9%). La soglia di T027 è ≥ 95%: **non raggiunta** sul gold congelato. Il riordino non ha migliorato T083 (73,9% → 73,9%).
- Le query di T083 non risolte al primo posto non sono state analizzate in questo rapporto (causa non indagata).
- Il caricamento on-demand del riordino ha richiesto una richiesta ordinaria: una prima esecuzione, a router raggiungibile ma riordino scaricato, si era fermata con evidenza (`reranker-not-ready.txt`).
