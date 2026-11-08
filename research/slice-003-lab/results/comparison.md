# Misure retrieval

Hit: quota di query con almeno un gold. Recall: media per query delle evidenze gold distinte recuperate. Gold unici: unione delle evidenze recuperate in almeno una query pertinente; non è completezza per ogni domanda. Latenze: singola esecuzione per query, indice già preparato; p95 empirico nearest-rank, nessuna significatività statistica.

| Corpus | Chunking | Metodo | Hit@1 | Hit@5 | Hit@10 | Recall@1 | Recall@5 | Recall@10 | Gold unici @10 | p50 ms | p95 ms |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| N4-fixtures | docling_context (6) | bm25 | 91.7% | 100.0% | 100.0% | 91.7% | 100.0% | 100.0% | 12/12 | 0.2 | 0.4 |
| N4-fixtures | docling_context (6) | dense | 83.3% | 100.0% | 100.0% | 83.3% | 100.0% | 100.0% | 12/12 | 116.9 | 128.4 |
| N4-fixtures | docling_context (6) | hybrid_rrf | 91.7% | 100.0% | 100.0% | 91.7% | 100.0% | 100.0% | 12/12 | 117.2 | 128.7 |
| N4-fixtures | docling_context (6) | hybrid_rerank | 91.7% | 100.0% | 100.0% | 91.7% | 100.0% | 100.0% | 12/12 | 294.8 | 349.7 |
| N4-fixtures | docling_text (6) | bm25 | 91.7% | 100.0% | 100.0% | 91.7% | 100.0% | 100.0% | 12/12 | 0.2 | 0.3 |
| N4-fixtures | docling_text (6) | dense | 83.3% | 100.0% | 100.0% | 83.3% | 100.0% | 100.0% | 12/12 | 117.4 | 188.1 |
| N4-fixtures | docling_text (6) | hybrid_rrf | 91.7% | 100.0% | 100.0% | 91.7% | 100.0% | 100.0% | 12/12 | 117.6 | 188.3 |
| N4-fixtures | docling_text (6) | hybrid_rerank | 91.7% | 100.0% | 100.0% | 91.7% | 100.0% | 100.0% | 12/12 | 291.8 | 368.5 |
| N4-fixtures | line_context (19) | bm25 | 91.7% | 100.0% | 100.0% | 91.7% | 100.0% | 100.0% | 12/12 | 0.4 | 0.8 |
| N4-fixtures | line_context (19) | dense | 83.3% | 100.0% | 100.0% | 83.3% | 100.0% | 100.0% | 12/12 | 118.5 | 140.5 |
| N4-fixtures | line_context (19) | hybrid_rrf | 91.7% | 100.0% | 100.0% | 91.7% | 100.0% | 100.0% | 12/12 | 118.8 | 141.0 |
| N4-fixtures | line_context (19) | hybrid_rerank | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 100.0% | 12/12 | 452.0 | 539.4 |
| N4-fixtures | line_text (19) | bm25 | 83.3% | 100.0% | 100.0% | 83.3% | 100.0% | 100.0% | 12/12 | 0.2 | 0.3 |
| N4-fixtures | line_text (19) | dense | 75.0% | 100.0% | 100.0% | 75.0% | 100.0% | 100.0% | 12/12 | 108.9 | 130.1 |
| N4-fixtures | line_text (19) | hybrid_rrf | 83.3% | 100.0% | 100.0% | 83.3% | 100.0% | 100.0% | 12/12 | 109.0 | 130.3 |
| N4-fixtures | line_text (19) | hybrid_rerank | 91.7% | 100.0% | 100.0% | 91.7% | 100.0% | 100.0% | 12/12 | 413.6 | 516.5 |
| T082-single | docling_context (458) | bm25 | 63.2% | 78.9% | 78.9% | 55.3% | 78.9% | 78.9% | 13/16 | 4.9 | 10.7 |
| T082-single | docling_context (458) | dense | 10.5% | 36.8% | 47.4% | 7.9% | 31.6% | 44.7% | 7/16 | 133.1 | 195.6 |
| T082-single | docling_context (458) | hybrid_rrf | 42.1% | 68.4% | 84.2% | 36.8% | 65.8% | 81.6% | 13/16 | 139.5 | 199.7 |
| T082-single | docling_context (458) | hybrid_rerank | 78.9% | 89.5% | 89.5% | 73.7% | 89.5% | 89.5% | 14/16 | 2179.3 | 2503.7 |
| T082-single | docling_text (458) | bm25 | 63.2% | 78.9% | 78.9% | 55.3% | 78.9% | 78.9% | 13/16 | 6.9 | 12.9 |
| T082-single | docling_text (458) | dense | 10.5% | 36.8% | 47.4% | 7.9% | 31.6% | 44.7% | 7/16 | 167.5 | 224.0 |
| T082-single | docling_text (458) | hybrid_rrf | 42.1% | 68.4% | 84.2% | 36.8% | 65.8% | 81.6% | 13/16 | 179.3 | 229.6 |
| T082-single | docling_text (458) | hybrid_rerank | 78.9% | 89.5% | 89.5% | 73.7% | 89.5% | 89.5% | 14/16 | 2295.7 | 4971.0 |
| T082-single | line_context (3115) | bm25 | 63.2% | 73.7% | 73.7% | 55.3% | 73.7% | 73.7% | 12/16 | 26.2 | 75.1 |
| T082-single | line_context (3115) | dense | 94.7% | 100.0% | 100.0% | 86.8% | 100.0% | 100.0% | 16/16 | 237.8 | 537.2 |
| T082-single | line_context (3115) | hybrid_rrf | 68.4% | 78.9% | 94.7% | 60.5% | 78.9% | 94.7% | 15/16 | 262.3 | 597.0 |
| T082-single | line_context (3115) | hybrid_rerank | 94.7% | 100.0% | 100.0% | 86.8% | 100.0% | 100.0% | 16/16 | 1150.2 | 1507.4 |
| T082-single | line_text (3115) | bm25 | 68.4% | 78.9% | 78.9% | 60.5% | 78.9% | 78.9% | 13/16 | 26.3 | 79.4 |
| T082-single | line_text (3115) | dense | 89.5% | 100.0% | 100.0% | 81.6% | 100.0% | 100.0% | 16/16 | 284.8 | 481.8 |
| T082-single | line_text (3115) | hybrid_rrf | 68.4% | 84.2% | 100.0% | 60.5% | 84.2% | 100.0% | 16/16 | 306.4 | 542.6 |
| T082-single | line_text (3115) | hybrid_rerank | 100.0% | 100.0% | 100.0% | 92.1% | 100.0% | 100.0% | 16/16 | 1137.8 | 1652.4 |
| T083-multi | docling_context (477) | bm25 | 60.0% | 100.0% | 100.0% | 49.3% | 100.0% | 100.0% | 19/19 | 4.5 | 12.7 |
| T083-multi | docling_context (477) | dense | 28.0% | 40.0% | 40.0% | 21.3% | 33.3% | 33.3% | 6/19 | 138.3 | 191.1 |
| T083-multi | docling_context (477) | hybrid_rrf | 48.0% | 64.0% | 76.0% | 37.3% | 60.0% | 74.0% | 16/19 | 141.6 | 202.4 |
| T083-multi | docling_context (477) | hybrid_rerank | 72.0% | 100.0% | 100.0% | 59.3% | 100.0% | 100.0% | 19/19 | 2035.9 | 2172.1 |
| T083-multi | docling_text (477) | bm25 | 56.0% | 100.0% | 100.0% | 45.3% | 100.0% | 100.0% | 19/19 | 4.5 | 9.1 |
| T083-multi | docling_text (477) | dense | 32.0% | 40.0% | 40.0% | 25.3% | 33.3% | 33.3% | 6/19 | 145.4 | 184.4 |
| T083-multi | docling_text (477) | hybrid_rrf | 44.0% | 72.0% | 88.0% | 33.3% | 66.7% | 84.0% | 18/19 | 150.5 | 189.8 |
| T083-multi | docling_text (477) | hybrid_rerank | 72.0% | 100.0% | 100.0% | 59.3% | 100.0% | 100.0% | 19/19 | 2034.9 | 2274.5 |
| T083-multi | line_context (3137) | bm25 | 80.0% | 100.0% | 100.0% | 69.3% | 100.0% | 100.0% | 19/19 | 27.6 | 73.1 |
| T083-multi | line_context (3137) | dense | 84.0% | 100.0% | 100.0% | 71.3% | 100.0% | 100.0% | 19/19 | 274.6 | 463.3 |
| T083-multi | line_context (3137) | hybrid_rrf | 96.0% | 100.0% | 100.0% | 83.3% | 100.0% | 100.0% | 19/19 | 306.4 | 518.9 |
| T083-multi | line_context (3137) | hybrid_rerank | 100.0% | 100.0% | 100.0% | 87.3% | 100.0% | 100.0% | 19/19 | 1133.0 | 1357.9 |
| T083-multi | line_text (3137) | bm25 | 56.0% | 100.0% | 100.0% | 45.3% | 100.0% | 100.0% | 19/19 | 38.1 | 69.1 |
| T083-multi | line_text (3137) | dense | 72.0% | 100.0% | 100.0% | 59.3% | 100.0% | 100.0% | 19/19 | 387.1 | 471.3 |
| T083-multi | line_text (3137) | hybrid_rrf | 64.0% | 100.0% | 100.0% | 53.3% | 100.0% | 100.0% | 19/19 | 441.4 | 543.8 |
| T083-multi | line_text (3137) | hybrid_rerank | 76.0% | 100.0% | 100.0% | 63.3% | 100.0% | 100.0% | 19/19 | 1260.0 | 1400.2 |

## Indicizzazione e reranking

I tempi embedding indicano soltanto input nuovi: la cache per contenuto riusa vettori identici tra varianti. Le query della campagna principale vengono sempre inviate al modello. I risultati `pilot/` precedono questa correzione della misura di latenza e non entrano nella tabella.

| Corpus | Variante | Build BM25 ms | Embedding nuovi | Chiamate embedding | Tempo HTTP embedding s | Token embedding | Query embedding p50 ms | Rerank p50 ms | Rerank p95 ms |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| N4-fixtures | docling_context | 0.3 | 0 | 0 | 0.00 | 0 | 109.6 | 174.8 | 231.3 |
| N4-fixtures | docling_text | 0.4 | 0 | 0 | 0.00 | 0 | 109.7 | 172.9 | 209.4 |
| N4-fixtures | line_context | 0.2 | 0 | 0 | 0.00 | 0 | 111.3 | 329.0 | 408.6 |
| N4-fixtures | line_text | 0.3 | 0 | 0 | 0.00 | 0 | 104.8 | 301.5 | 403.3 |
| T082-single | docling_context | 129.6 | 0 | 0 | 0.00 | 0 | 99.2 | 2048.6 | 2364.2 |
| T082-single | docling_text | 128.1 | 458 | 115 | 35.60 | 306657 | 107.0 | 2107.7 | 4809.1 |
| T082-single | line_context | 122.8 | 3115 | 779 | 132.37 | 333769 | 102.2 | 871.5 | 1103.8 |
| T082-single | line_text | 146.1 | 35 | 9 | 1.52 | 3490 | 121.4 | 662.6 | 1190.4 |
| T083-multi | docling_context | 143.4 | 168 | 42 | 11.30 | 104118 | 105.8 | 1887.9 | 2037.9 |
| T083-multi | docling_text | 116.8 | 476 | 119 | 32.42 | 307733 | 109.0 | 1889.1 | 2118.7 |
| T083-multi | line_context | 139.9 | 3136 | 784 | 133.82 | 333126 | 99.1 | 822.7 | 903.1 |
| T083-multi | line_text | 126.0 | 3088 | 772 | 129.29 | 312723 | 101.4 | 832.0 | 1004.9 |

## Omissioni per query

Vedere [missing-evidence.json](missing-evidence.json): include i gold mancanti dalla top 10, con posizione effettiva o assenza dai 50 candidati rerank. Nessun caso viene rimosso dal denominatore.
