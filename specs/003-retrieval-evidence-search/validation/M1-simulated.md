# Rapporto di valutazione del recupero (003)

- Modalità: **Simulated**. Embedding SIMULATI (SHA-256 → vettore, nessuna semantica) e pgvector reale: le metriche denso e RRF NON sono misure di qualità; le misure reali richiedono il DGX (T019, non eseguito).
- Provenienza dei dati: sintetici (nessun dato cliente); autore delle query: singolo; gold congelato il 2026-11-09 (commit del laboratorio f005c8dab923f12e3130ef86517b1318d707ce10).
- Configurazione: `IndexConfigHash` acd754c22773a078b144c3c61c7dac9b176b2c60a85cfed9522421fb80ab731e; politica: non applicabile in M1 (T022); embedding simulato (hash → vettore) (1024); riordino: nessuno (M1); SHA-256 dei pesi: NotVerified (modello simulato); FTS italian (PostgreSQL FTS, OR dei termini, ts_rank_cd); pool 50; RRF k=60.
- Hash gold (PROVENANCE.json): 401ab635fe3ac1691683af773a030787938d5b2812bec227182657db35890a8a; hash del rapporto: 1bccb71eb19c8e0c59b2a764c8ff06c442848aa8541b44847aa0adb5a3085929.
- Esito: **Non valutabile**. Modalità simulata: le soglie di qualità non si applicano (embedding simulati, nessuna semantica). Nessuna accettazione è possibile da questo rapporto.
  - 002:T138: aperto (checkbox non spuntata)
  - 002:T150: aperto (checkbox non spuntata)
  - SHA-256 dei pesi del modello: NotVerified

## N4-fixtures (4 documenti, 14 blocchi, 12 query)

| Livello / segnale | Query | Recall@1 | @5 | @10 | @20 | @50 | Hit@1 | Hit@5 | Hit@10 | Tutti i riscontri @10 | Primo riscontro (media) | Mai trovato |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Blocco, lessicale | 12 | 91.7% | 100.0% | 100.0% | 100.0% | 100.0% | 91.7% | 100.0% | 100.0% | 100.0% | 1.17 | 0 |
| Blocco, denso | 12 | 8.3% | 25.0% | 100.0% | 100.0% | 100.0% | 8.3% | 25.0% | 100.0% | 100.0% | 6.83 | 0 |
| Blocco, RRF | 12 | 50.0% | 100.0% | 100.0% | 100.0% | 100.0% | 50.0% | 100.0% | 100.0% | 100.0% | 1.75 | 0 |
| Evidenza, RRF (dopo mappatura) | 12 | 50.0% | 100.0% | 100.0% | 100.0% | 100.0% | 50.0% | 100.0% | 100.0% | 100.0% | 1.75 | 0 |

Perdita di mappatura blocco→evidenza: 0. Citazioni invalide: 0. Riscontri Message sempre inclusi: 0/0. Conflitti valutati: 0; persi (almeno un lato fuori dai primi 10): 0.

## T082-single (1 documenti, 3116 blocchi, 19 query)

| Livello / segnale | Query | Recall@1 | @5 | @10 | @20 | @50 | Hit@1 | Hit@5 | Hit@10 | Tutti i riscontri @10 | Primo riscontro (media) | Mai trovato |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Blocco, lessicale | 19 | 36.8% | 68.4% | 68.4% | 73.7% | 81.6% | 42.1% | 68.4% | 68.4% | 68.4% | 5.00 | 3 |
| Blocco, denso | 19 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | n/d | 19 |
| Blocco, RRF | 19 | 31.6% | 57.9% | 68.4% | 68.4% | 78.9% | 36.8% | 63.2% | 68.4% | 68.4% | 9.44 | 3 |
| Evidenza, RRF (dopo mappatura) | 19 | 31.6% | 57.9% | 68.4% | 68.4% | 78.9% | 36.8% | 63.2% | 68.4% | 68.4% | 9.44 | 3 |

Perdita di mappatura blocco→evidenza: 0. Citazioni invalide: 0. Riscontri Message sempre inclusi: 0/0. Conflitti valutati: 2; persi (almeno un lato fuori dai primi 10): 1.

## T083-multi (3 documenti, 3163 blocchi, 25 query)

| Livello / segnale | Query | Recall@1 | @5 | @10 | @20 | @50 | Hit@1 | Hit@5 | Hit@10 | Tutti i riscontri @10 | Primo riscontro (media) | Mai trovato |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Blocco, lessicale | 23 | 41.3% | 73.9% | 82.6% | 95.7% | 100.0% | 52.2% | 73.9% | 82.6% | 82.6% | 5.35 | 0 |
| Blocco, denso | 23 | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% | n/d | 23 |
| Blocco, RRF | 23 | 15.2% | 58.7% | 73.9% | 82.6% | 95.7% | 21.7% | 60.9% | 73.9% | 73.9% | 10.39 | 0 |
| Evidenza, RRF (dopo mappatura) | 23 | 15.2% | 58.7% | 73.9% | 82.6% | 95.7% | 21.7% | 60.9% | 73.9% | 73.9% | 10.39 | 0 |

Perdita di mappatura blocco→evidenza: 0. Citazioni invalide: 0. Riscontri Message sempre inclusi: 4/4. Conflitti valutati: 3; persi (almeno un lato fuori dai primi 10): 0.

## Limiti noti

- Modalità simulata: nessuna soglia di qualità (D-1) è applicata o fissata; le metriche non sono confrontabili con quelle reali del laboratorio.
- Dati sintetici del laboratorio (nessun dato cliente), riempitivo povero, query di un solo autore: nessuna indipendenza delle query, nessun corpus anonimizzato.
- Parser finto a paragrafi (TXT, Markdown con titoli, DOCX): ~3,1k blocchi per corpus, analoghi alla variante «line» del laboratorio (3115/3137 chunk), non ai ~460 blocchi Docling; nessun PDF.
- Una evidenza per blocco (proxy di un ledger completo): la mappatura blocco→evidenza è 1:1 per costruzione; il test verifica l'assenza di perdite nella pipeline, non la copertura dell'estrazione reale.
- I riscontri Message (dichiarazioni del PM) sono sempre inclusi e non classificati: esclusi da Recall@k (il laboratorio li indicizzava); verificati come presenti nel pacchetto.
- Il segnale lessicale è PostgreSQL FTS italiano reale (OR dei termini), diverso dal BM25 del laboratorio: il confronto è oggetto di T019 (R-1).
- Nessun riordino, nessun padre, nessun LLM (M1); isolamento fra progetti, versioni escluse e determinismo sono provati dai test PG, non dalle metriche.
- SHA-256 dei pesi del modello non verificato (NotVerified); nessuna garanzia di completezza: il Recall è misurato solo sul gold annotato.

Misure non deterministiche (fuori dall'hash): indicizzazione a freddo 22,0 s; ricerca per query (media) 23,8 ms. Tempi con embedding simulati su PostgreSQL di test: non rappresentano il DGX.
