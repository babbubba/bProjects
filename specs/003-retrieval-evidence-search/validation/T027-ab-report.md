# T027 — confronto A/B dell'input del riordino

Misura descrittiva, separata dalla baseline ufficiale. **Nessun giudizio di gate**: T027 resta del committente. I gold storici (N4, T082, T083) sono qui solo controllo di regressione; `T027-dev` è un insieme di SVILUPPO dell'assistente, non una validazione indipendente.

- Data: 2026-11-10. A: `retrieval-policy-1` (hash `0c7049270c3960b8ca981a51fa6ab2ea0ceb24fccb4b978fa365d7d84d4f2245`, solo testo). B: `retrieval-policy-2` (hash `84f3fb4aeb291dfe27bda03996f152bf9a3356b39fee21a9021180ba5cc60981`, `rerank-input-1`).
- Stessa configurazione di recupero: pool 50, riordino dei primi 50, RRF k=60; una ricerca per query, stessi candidati per A e B. Embedding `bge-m3-Q5`, riordino `bge-reranker-v2-m3`.
- Indicizzazione (s): T027-dev 17.2, N4-fixtures 0.7, T082-single 120.0, T083-multi 121.6.

## Per insieme

| Gruppo | Query | Hit@1 RRF | Hit@1 A | Hit@1 B | B − A | Recall@5 A | Recall@5 B | Recall@10 A | Recall@10 B | B − A (R@10) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| T027-dev | 32 | 71.9% | 68.8% | 100.0% | +31.3 pt | 100.0% | 99.0% | 100.0% | 100.0% | 0.0 pt |
| N4-fixtures | 12 | 75.0% | 83.3% | 91.7% | +8.3 pt | 100.0% | 100.0% | 100.0% | 100.0% | 0.0 pt |
| T082-single | 19 | 73.7% | 100.0% | 94.7% | -5.3 pt | 100.0% | 100.0% | 100.0% | 100.0% | 0.0 pt |
| T083-multi | 23 | 73.9% | 73.9% | 100.0% | +26.1 pt | 100.0% | 100.0% | 100.0% | 100.0% | 0.0 pt |
| TUTTI | 86 | 73.3% | 79.1% | 97.7% | +18.6 pt | 100.0% | 99.6% | 100.0% | 100.0% | 0.0 pt |

## T027-dev per categoria

| Gruppo | Query | Hit@1 RRF | Hit@1 A | Hit@1 B | B − A | Recall@5 A | Recall@5 B | Recall@10 A | Recall@10 B | B − A (R@10) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| explicit-doc | 15 | 53.3% | 60.0% | 100.0% | +40.0 pt | 100.0% | 100.0% | 100.0% | 100.0% | 0.0 pt |
| section-ref | 4 | 50.0% | 0.0% | 100.0% | +100.0 pt | 100.0% | 100.0% | 100.0% | 100.0% | 0.0 pt |
| implicit-unique | 6 | 100.0% | 100.0% | 100.0% | 0.0 pt | 100.0% | 100.0% | 100.0% | 100.0% | 0.0 pt |
| similar-multi | 7 | 100.0% | 100.0% | 100.0% | 0.0 pt | 100.0% | 95.2% | 100.0% | 100.0% | 0.0 pt |

## Con e senza riferimento al documento nella query (dichiarato per T027-dev, euristica per parole chiave sui gold storici)

| Gruppo | Query | Hit@1 RRF | Hit@1 A | Hit@1 B | B − A | Recall@5 A | Recall@5 B | Recall@10 A | Recall@10 B | B − A (R@10) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| cita il documento | 35 | 57.1% | 51.4% | 100.0% | +48.6 pt | 100.0% | 100.0% | 100.0% | 100.0% | 0.0 pt |
| non cita il documento | 51 | 84.3% | 98.0% | 96.1% | -2.0 pt | 100.0% | 99.3% | 100.0% | 100.0% | 0.0 pt |

## Solo gold storici (controllo di regressione)

| Gruppo | Query | Hit@1 RRF | Hit@1 A | Hit@1 B | B − A | Recall@5 A | Recall@5 B | Recall@10 A | Recall@10 B | B − A (R@10) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| N4-fixtures | 12 | 75.0% | 83.3% | 91.7% | +8.3 pt | 100.0% | 100.0% | 100.0% | 100.0% | 0.0 pt |
| T082-single | 19 | 73.7% | 100.0% | 94.7% | -5.3 pt | 100.0% | 100.0% | 100.0% | 100.0% | 0.0 pt |
| T083-multi | 23 | 73.9% | 73.9% | 100.0% | +26.1 pt | 100.0% | 100.0% | 100.0% | 100.0% | 0.0 pt |

Regressione sui gold storici (B − A): Hit@1 +11.1 pt, Recall@10 0.0 pt. Segnalazione per peggioramento oltre 2 pt: Hit@1 no, Recall@10 no.

## Query cambiate (Hit@1)

Regressioni (corrette in A, mancate in B): **1**. Miglioramenti (mancate in A, corrette in B): **17**.

| Esito | Insieme | Query | Categoria | Cita il documento | Posizioni A | Posizioni B |
|---|---|---|---|---|---|---|
| regressione | T082-single | S14 | legacy | no | 1 | 2 |
| miglioramento | T027-dev | E01 | explicit-doc | sì | 3 | 1 |
| miglioramento | T027-dev | E02 | explicit-doc | sì | 3 | 1 |
| miglioramento | T027-dev | E03 | explicit-doc | sì | 2 | 1 |
| miglioramento | T027-dev | E04 | explicit-doc | sì | 3 | 1 |
| miglioramento | T027-dev | E07 | explicit-doc | sì | 2 | 1 |
| miglioramento | T027-dev | E08 | explicit-doc | sì | 2 | 1 |
| miglioramento | T027-dev | S01 | section-ref | sì | 2 | 1 |
| miglioramento | T027-dev | S02 | section-ref | sì | 4 | 1 |
| miglioramento | T027-dev | S03 | section-ref | sì | 2 | 1 |
| miglioramento | T027-dev | S04 | section-ref | sì | 3 | 1 |
| miglioramento | N4-fixtures | N4-doc-B | legacy | sì | 3 | 1 |
| miglioramento | T083-multi | M03 | legacy | sì | 2 | 1 |
| miglioramento | T083-multi | M04 | legacy | sì | 2 | 1 |
| miglioramento | T083-multi | M05 | legacy | sì | 2 | 1 |
| miglioramento | T083-multi | M09 | legacy | sì | 2 | 1 |
| miglioramento | T083-multi | M13 | legacy | sì | 2 | 1 |
| miglioramento | T083-multi | M14 | legacy | sì | 3 | 1 |

## Latenza del solo riordino per query (ms, include la lettura dei testi e della provenienza dal DB)

| Variante | n | p50 | p95 | media | max |
|---|---:|---:|---:|---:|---:|
| A (solo testo) | 86 | 330,4 | 458,2 | 336,7 | 878,9 |
| B (documento + sezione + testo) | 86 | 381,9 | 504,2 | 369,6 | 689,3 |

## Limiti

- Dati sintetici; `T027-dev` scritto dopo aver osservato i fallimenti di M2 dallo stesso autore dell'esperimento: serve un insieme indipendente (altro autore o revisore) per una validazione.
- Una esecuzione per variante; servizi quantizzati su router condiviso; latenze indicative. Hit@1 su query multi-riscontro = almeno un riscontro in prima posizione.
- Il riferimento al documento nei gold storici è un'euristica per parole chiave, non una dichiarazione d'autore.
