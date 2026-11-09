# M2 — diagnostica per query (003 T027) — dati grezzi leggibili

Separata dalla baseline ufficiale. Posizioni 1-based dei riscontri documentali; «—» = assente dalla classifica. Punteggi del riordino: solo dati diagnostici.

| Corpus | Query | #gold | lessicale | denso | RRF | riordino | Hit@1 RRF | Hit@1 riordino |
|---|---|---:|---|---|---|---|---|---|
| N4-fixtures | N4-doc-title | 1 | 1 | 2 | 1 | 1 | sì | sì |
| N4-fixtures | N4-doc-30 | 1 | 1 | 1 | 1 | 1 | sì | sì |
| N4-fixtures | N4-doc-B | 1 | 3 | 4 | 3 | 3 | **no** | **no** |
| N4-fixtures | N4-doc-B-60 | 1 | 1 | 1 | 1 | 1 | sì | sì |
| N4-fixtures | neg-consent | 1 | 1 | 1 | 1 | 1 | sì | sì |
| N4-fixtures | neg-returns | 1 | 1 | 3 | 2 | 1 | **no** | sì |
| N4-fixtures | neg-card | 1 | 1 | 1 | 1 | 1 | sì | sì |
| N4-fixtures | neg-export | 1 | 1 | 1 | 1 | 1 | sì | sì |
| N4-fixtures | neg-45 | 1 | 1 | 3 | 2 | 2 | **no** | **no** |
| N4-fixtures | md-release | 1 | 1 | 1 | 1 | 1 | sì | sì |
| N4-fixtures | md-excluded | 1 | 1 | 1 | 1 | 1 | sì | sì |
| N4-fixtures | md-collaudo | 1 | 1 | 1 | 1 | 1 | sì | sì |
| T083-multi | M01 | 1 | 1 | 1 | 1 | 1 | sì | sì |
| T083-multi | M02 | 1 | 1 | 1 | 1 | 1 | sì | sì |
| T083-multi | M06 | 1 | 4 | 3 | 3 | 1 | **no** | sì |
| T083-multi | M07 | 1 | 1 | 1 | 1 | 1 | sì | sì |
| T083-multi | M03 | 1 | 12 | 4 | 1 | 2 | sì | **no** |
| T083-multi | M04 | 1 | 4 | 3 | 4 | 2 | **no** | **no** |
| T083-multi | M05 | 1 | 9 | 2 | 1 | 2 | sì | **no** |
| T083-multi | M08 | 1 | 9 | 1 | 1 | 1 | sì | sì |
| T083-multi | M12 | 1 | 2 | 1 | 2 | 1 | **no** | sì |
| T083-multi | M09 | 1 | 20 | 1 | 1 | 2 | sì | **no** |
| T083-multi | M10 | 1 | 1 | 1 | 1 | 1 | sì | sì |
| T083-multi | M11 | 1 | 2 | 2 | 2 | 1 | **no** | sì |
| T083-multi | M13 | 1 | 18 | 1 | 2 | 2 | **no** | **no** |
| T083-multi | M14 | 1 | 27 | 3 | 4 | 3 | **no** | **no** |
| T083-multi | M15 | 1 | 1 | 1 | 1 | 1 | sì | sì |
| T083-multi | M16 | 1 | 4 | 1 | 1 | 1 | sì | sì |
| T083-multi | M17 | 1 | 1 | 1 | 1 | 1 | sì | sì |
| T083-multi | all-payments | 2 | 1,2 | 2,1 | 1,2 | 1,2 | sì | sì |
| T083-multi | all-warranty | 2 | 2,1 | 2,1 | 2,1 | 2,1 | sì | sì |
| T083-multi | all-limits | 1 | 1 | 1 | 1 | 1 | sì | sì |
| T083-multi | equivalent-delivery | 2 | 1,3 | 1,2 | 1,3 | 2,1 | sì | sì |
| T083-multi | penalty-reference | 2 | 1,2 | 1,2 | 1,2 | 1,2 | sì | sì |
| T083-multi | ambra-complements | 2 | 2,1 | 2,1 | 2,1 | 1,2 | sì | sì |

Dettaglio completo (top 10 per segnale, punteggi, concorrenti preferiti, controlli, stabilità, sonda esplorativa) in `M2-diagnostic-raw.json`.
