# Blocchi originali e contesto padre

Metrica espansa: gold presente in uno dei padri dei primi k blocchi recuperati. Il costo include tutti i padri distinti; nessun riordino o filtro tramite gold. Non è un knowledge graph semantico.

| Corpus | Metodo | Recall@10 blocchi | Recall@10 con padri | Caratteri blocchi p50 | Caratteri con padri p50 | Ricerca p50 ms | Espansione p50 ms |
|---|---|---:|---:|---:|---:|---:|---:|
| N4-fixtures | bm25 | 100.0% | 100.0% | 556 | 622 | 0.2 | 0.03 |
| N4-fixtures | dense | 100.0% | 100.0% | 654 | 866 | 117.1 | 0.01 |
| N4-fixtures | hybrid_rrf | 100.0% | 100.0% | 612 | 842 | 117.5 | 0.01 |
| N4-fixtures | hybrid_rerank | 100.0% | 100.0% | 652 | 780 | 415.9 | 0.01 |
| T082-single | bm25 | 78.9% | 78.9% | 3115 | 8630 | 25.6 | 0.04 |
| T082-single | dense | 100.0% | 100.0% | 1972 | 29012 | 248.7 | 0.03 |
| T082-single | hybrid_rrf | 100.0% | 100.0% | 2653 | 26120 | 276.9 | 0.01 |
| T082-single | hybrid_rerank | 100.0% | 100.0% | 2028 | 28963 | 1119.9 | 0.01 |
| T083-multi | bm25 | 100.0% | 100.0% | 2085 | 26006 | 26.3 | 0.05 |
| T083-multi | dense | 100.0% | 100.0% | 921 | 25996 | 262.0 | 0.02 |
| T083-multi | hybrid_rrf | 100.0% | 100.0% | 1870 | 26082 | 291.3 | 0.01 |
| T083-multi | hybrid_rerank | 100.0% | 100.0% | 1435 | 26364 | 1073.0 | 0.01 |
