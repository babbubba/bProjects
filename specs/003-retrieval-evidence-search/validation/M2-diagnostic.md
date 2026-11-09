# M2 — diagnosi dell'Hit@1 sotto soglia (003 T027)

**Stato: DIAGNOSTICA, non baseline e non giudizio di gate.** T027 resta APERTO: soglia invariata (Hit@1 ≥ 95%, Recall@10 ≥ 95%, nessuna regressione > 2 pt). Baseline ufficiali (`baseline-m2.json`, `m2-results.json`, `M2-real.md`) non toccate. Dati per query: `M2-diagnostic-raw.json` (completo) e `M2-diagnostic-queries.md` (tabella).

## Condizioni della prova

- Autorizzazione esplicita dell'utente, limitata a questa diagnosi. Corpora N4 (12 query) e T083 (23 query con riscontri documentali); T082 non rieseguito (100% Hit@1 in T026, nessuna dipendenza).
- Gold, corpora, query, `retrieval-policy-1` (pool 50, riordino dei primi 50, RRF k=60) e modelli invariati: embedding `bge-m3-Q5`, riordino `bge-reranker-v2-m3` (Q5_K_M). PostgreSQL 18.6 + pgvector di prova; l'harness crea un database temporaneo, quindi i due corpora sono stati re-indicizzati (nessun indice riusabile). Una sola esecuzione DGX (2 min 44 s); ripetizione solo sulle query fallite.
- Servizio di riordino (da `/v1/models`): `--ctx-size 8192`, `--batch-size/--ubatch-size 2048`, `--parallel 1`, `--pooling rank`.
- I risultati RRF e riordino coincidono con T026: N4 75,0% → 83,3%, T083 73,9% → 73,9% (17/23).

## 1. Query che falliscono (Hit@1, posizione del primo gold)

| Query | RRF | Riordino | Categoria |
|---|---:|---:|---|
| N4-doc-B | 3 | 3 | D |
| N4 neg-45 | 2 | 2 | C |
| T083 M03 | **1** | 2 | D |
| T083 M04 | 4 | 2 | D |
| T083 M05 | **1** | 2 | D |
| T083 M09 | **1** | 2 | D |
| T083 M13 | 2 | 2 | D |
| T083 M14 | 4 | 3 | D |
| (N4 neg-returns) | 2 | **1** | risolta dal riordino |
| (T083 M06, M11, M12) | 3, 2, 2 | **1** | risolte dal riordino |

Lo stesso 73,9% su T083 nasconde insiemi diversi: l'RRF sbaglia M04, M06, M11, M12, M13, M14; il riordino sbaglia M03, M04, M05, M09, M13, M14. Il riordino corregge 3 query (M06, M11, M12) e ne rompe 3 (M03, M05, M09), che l'RRF aveva già al primo posto. Recall@10 = 100% ovunque: in tutti gli 8 casi il gold è fra i primi 3 dopo il riordino (la categoria B non si verifica mai).

## 2. Verifiche tecniche (categoria F: nessun bug)

Su tutte le 35 query: punteggi ordinati in modo decrescente, indici unici e completi, il riordino è una permutazione della finestra RRF, la finestra è il prefisso RRF, l'ordine del prodotto (`CandidateReranker`) coincide con quello calcolato indipendentemente dall'harness. Il testo annotato coincide con quello indicizzato (0 differenze su tutti i blocchi dei due corpora). Sulle 8 query fallite: ripetizione identica (stessi top 10, differenza massima di punteggio 0) e punteggio in batch = punteggio per singolo documento (nessun effetto di mapping o serving). Nessun troncamento: il candidato più lungo è 558 caratteri (~180 token) su un contesto di 8192. Nessuna correzione al codice è giustificata.

## 3. Cause (evidenze)

**D — il blocco non contiene l'identità del documento o della sezione (7 query).** Il riordino riceve solo il testo del blocco (`index-text-1`, senza titolo né nome del file, scelta A-5). Le query indicano il documento («secondo il verbale di collaudo», «nel contratto quadro», «Scheda B»), ma il blocco corretto non lo dice, e blocchi di altri documenti sono quasi identici:
- M13: gold «garanzia … 36 mesi» (verbale) contro «… 24 mesi» (contratto), similarità di Jaccard 0,83, differenza di punteggio 0,11. M14: stessa struttura (60 contro 30 giorni), gold in terza posizione.
- M03/M04: il riordino preferisce il blocco «Il contratto quadro Zefiro ha durata di 48 mesi» (l'unico che contiene le parole «contratto quadro») alla risposta effettiva (punteggio −0,6 contro −3,6): corrispondenza con la menzione del documento invece che con la risposta.
- M05/M09: due documenti dicono la stessa cosa in modi diversi (5 giorni lavorativi contro una settimana lavorativa); M09 è un quasi-pareggio (1,477 contro 1,495). Il riordino non può sapere quale blocco venga dal contratto e quale dall'allegato.
- N4-doc-B: il gold «Il termine di pagamento e' 60 giorni.» non nomina la Scheda B (il titolo è il blocco precedente, che infatti viene preferito al primo posto), e «30 giorni» della Scheda A lo precede.

**C — gold incompleto (1 query).** neg-45 «Il termine dei pagamenti è 45 oppure 30 giorni?»: il gold contiene solo il blocco di `txt-negations` (un paragrafo lungo con più frasi, fra cui quella dei 45 giorni). Il blocco preferito, «Il termine di pagamento e' 30 giorni» (`txt-conflict-a`), risponde in modo pertinente alla metà «30» della domanda. Non si cambia il gold; si segnala al committente.

Altre categorie: A vale come sintomo (il gold c'è ma sta troppo in basso) ma non come causa; B, E, F, G: nessun caso accertato.

## 4. Sonda esplorativa (NON validazione)

Solo sulle 8 query fallite, anteponendo «Documento: <nome file>» ai candidati (query invariata): **7 su 8 ottengono Hit@1** (resta fuori solo neg-45, posizione 2, coerente con la causa C). È un'indicazione di causa, non una misura di miglioramento: è calcolata sulle sole query già fallite e sullo stesso gold di accettazione (in-sample), e non dice nulla sulle query oggi corrette. Non va presentata come risultato né usata per tarare parametri.

## 5. Miglioramenti proposti (per impatto atteso, poi complessità)

1. **Contesto di provenienza nell'input del riordino** (impatto alto, complessità bassa): inviare al riordino, per ciascun candidato, nome del documento e percorso dei titoli oltre al testo del blocco, senza toccare l'indice (nessuna re-indicizzazione) né `index-text-1`; nessun troncamento (blocchi ≤ 558 caratteri, contesto 8192). È una modifica di policy/A-5 e richiede decisione esplicita; va validata su **query nuove e indipendenti** (SC-012) più un controllo di non regressione sulle query oggi corrette, non sul gold di accettazione.
2. **Nuovo insieme di query indipendenti** (rende valida qualunque misura; complessità media).
3. **Revisione di neg-45 da parte del committente** (1 query su 54; complessità bassa): decidere se anche il blocco «30 giorni» sia un riscontro valido. Il gold non è stato modificato.
4. **Confronto con un altro riordino** (impatto atteso basso finché manca l'identità del documento; complessità media): le 7 cause D sono assenza di informazione nell'input, non debolezza del modello. Da considerare solo se la proposta 1 non basta.
5. **Altro modello di embedding**: non indicato (Recall@10 100%; il problema è l'ordinamento finale, non il recupero).

## 6. Il 95% è raggiungibile con il riordino attuale?

Sull'input attuale (testo del blocco senza contesto), no: su 54 query il 95% ammette al massimo 2 mancate al primo posto, oggi sono 8 (N4 2, T083 6; T082 0). Con lo stesso riordino ma con contesto di provenienza è plausibile, perché 7 degli 8 errori hanno la stessa causa, ma è un'ipotesi da misurare su query indipendenti. Nessuna conclusione di gate.

## 7. Esperimento con riordino alternativo

Non raccomandato come primo passo. Se, dopo la proposta 1 su query indipendenti, l'Hit@1 restasse < 95%, confrontare sullo stesso input arricchito un secondo riordino multilingue servito dal router, mantenendo `bge-reranker-v2-m3` come riferimento; autorizzazione DGX da richiedere a parte.

## Limiti

Una sola esecuzione completa (il servizio è risultato deterministico sulle query ripetute); dati sintetici; query di un solo autore; parser finto a paragrafi (non Docling); SHA-256 dei pesi non verificato. La diagnosi spiega i fallimenti osservati, non stima la generalizzazione.
