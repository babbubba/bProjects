# Contratto: valutazione continua (C7)

**Feature**: `003-retrieval-evidence-search` | **Data**: 2026-11-09 | Spec: FR-026–FR-030, FR-036, SC-002, SC-003, SC-010, SC-012; decisioni D-1, D-2

## 1. Principio: promuovere, non ricreare

Gold, query e corpora sintetici del laboratorio (`research/slice-003-lab` @ `f005c8d`) sono **copiati come fixture di test** con `PROVENANCE.json` (commit, percorso, SHA-256, autore delle query = singolo autore, data di congelamento). Il gold è **congelato**: modificarlo fa fallire il test; una nuova versione di gold è un nuovo insieme con propria baseline. Il laboratorio resta riferimento storico invariato; nessuno script Python entra nel prodotto o nella CI di prodotto: le metriche sono riscritte come funzioni C# deterministiche (verificate contro i valori in `results/*`).

## 2. Insiemi

| Insieme | Origine | Uso |
|---|---|---|
| Gold sintetico congelato | T082 (19 query/16 fatti gold), T083 (25 query/17 fatti documentali + P1/P2), N4 (12 casi) | Soglie di accettazione e regressione |
| Gold di calibrazione | stessi corpora, query **non** usate per le soglie, se disponibili; altrimenti dichiarato | Calibrazione di `retrieval-policy-N` (non per l'accettazione) |
| Corpus anonimizzato + ≥ 20 query di un secondo autore | PM | Accettazione piena (D-2); soglie sul corpus cliente da fissare dopo la prima misura |

Query multi-riscontro: Recall conta **tutte** le evidenze annotate; un solo lato di un conflitto non vale 1.

## 3. Metriche (per corpus e per configurazione)

Recall@1/5/10/20/50; Hit@1/5/10; posizione del primo riscontro; falsi `AnsweredInSnapshot`; conflitti persi; citazioni invalide; perdita di mappatura blocco→evidenza (R-3); latenza per query (embedding query, ricerca, fusione, riordino); tempo di indicizzazione a freddo. nDCG non riportato (nessun giudizio graduato indipendente).

## 4. Soglie (D-1, approvate 2026-11-09) sul gold sintetico congelato

| Soglia | Valore | Misura |
|---|---|---|
| Recall@10 (ibrido) | ≥ 95% | per corpus T082, T083, N4 |
| Hit@1 con riordino | ≥ 95% | idem |
| Falsi `AnsweredInSnapshot` | 0 | dai test di flusso (T036/T040) |
| Regressione | ≤ 2 punti | rispetto alla **baseline dichiarata sullo STESSO insieme di query** (hash coincidenti); insiemi diversi non sono confrontabili e il confronto è rifiutato |

La baseline è la prima esecuzione ufficiale (`baseline-m1.json`, aggiornata a M2 con riordino): non il 100% del laboratorio. Le soglie non si applicano al corpus cliente finché non esiste una prima misura.

## 5. Modalità e rapporto

- **Simulata** (CI, sempre): embedding/riordino deterministici simulati; verifica il *plumbing* e le invarianti (filtri, isolamento, ricostruzione), non la qualità semantica; il rapporto lo dichiara.
- **Reale** (opt-in `BPROJECTS_TEST_DGX_ALLOW=1`, come `DgxGuardTests`): modelli reali; senza opt-in test ignorati con messaggio esplicito, mai superati.
- Ogni rapporto riporta: hash di input e gold, configurazione (`IndexConfigHash`, `PolicyHash`), versione dei servizi, modalità, **provenienza dei dati (sintetici/cliente), autore delle query, limiti noti** (dati sintetici, riempitivo povero, un solo autore, PDF/isolamento/versioni escluse non provati dal laboratorio, hash dei pesi non verificato, nessuna garanzia di completezza).

## 6. Esito di accettazione (D-2)

| Condizione | Esito |
|---|---|
| Soglie §4 rispettate **e** corpus anonimizzato **e** ≥ 20 query di un secondo autore valutate | **Accettata** |
| Soglie §4 rispettate ma manca uno dei due requisiti indipendenti | **Accettata con riserva**: il testo DEVE evidenziare *«manca la validazione su corpus anonimizzato e su query indipendenti»*; **non equivale all'accettazione piena** |
| Soglie non rispettate | Non accettata |

Il rapporto riporta anche lo stato di `002:T138`/`002:T150` e dello SHA-256 dei pesi (`NotVerified` finché non registrato). 0 occorrenze di «accettata» senza i due requisiti (SC-012).
