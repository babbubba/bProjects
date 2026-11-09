# ADR-0002 — pgvector e immagine PostgreSQL per l'indice derivato di recupero

**Stato**: proposto con la Slice 003 (T002). **Data**: 2026-11-09. **Ambito**: dipendenza di infrastruttura, non ripianificazione del prodotto.

## Contesto e decisione

La Slice 003 aggiunge una ricerca ibrida (lessicale `tsvector` italiano + densa) sui blocchi già persistiti dalla 002. Il segnale denso richiede l'estensione **pgvector**, assente nell'immagine `postgres:18` standard. Si adotta l'immagine `pgvector/pgvector:pg18` (stessa major di PostgreSQL), fissata per tag **e** digest in `infra/compose.yaml` e usata nei test PG, con la migrazione `CREATE EXTENSION IF NOT EXISTS vector` eseguita **solo** dal comando esplicito `migrate`, mai all'avvio del server. Colonna `vector(1024)`, operatore `<=>` (coseno), scansione esatta di default; HNSW solo se una misura la giustifica ([research R-1](../../specs/003-retrieval-evidence-search/research.md)).

## Alternative considerate

- **Qdrant** (o altro vector DB): escluso salvo misura sul corpus bProjects che dimostri che PostgreSQL+pgvector non rispetta Recall@10/latenza dichiarati. Nuovo servizio, nuova superficie operativa e di sicurezza, filtri di progetto/snapshot da sincronizzare (FR-032, Costituzione I).
- **BM25 applicativo / Elasticsearch / OpenSearch**: duplicano il motore già nel database o aggiungono un servizio senza evidenza che serva.
- **Solo lessicale o solo denso**: evidenza sperimentale contraria (ibrido 100% Recall@10 contro 78,9% del solo BM25 su T082).

## Conseguenze operative

- **Backup/restore**: l'indice è una proiezione **derivata**: nessun dato esiste solo nell'indice (Costituzione IV, FR-007). Il ripristino di un backup può avvenire con le tabelle d'indice vuote; si ricostruiscono dai soli `DocumentBlock`. Il restore richiede un'istanza con l'estensione `vector` disponibile (stessa immagine).
- **`migrate`**: crea l'estensione e le due tabelle d'indice in una migrazione additiva; rollback dell'indice non tocca blocchi, evidenze o snapshot. Se l'utente di `migrate` non può creare estensioni, l'operazione fallisce con errore esplicito (nessun fallback).
- **Aggiornamento dell'immagine**: cambiare tag/digest è una decisione esplicita (pin); l'upgrade major di PostgreSQL segue la procedura ordinaria di dump/restore.
- **Ambiente di test**: i test PG (`BPROJECTS_TEST_POSTGRES`) devono puntare a un PostgreSQL 18 con pgvector (stessa immagine `pgvector/pgvector:pg18`); su un server senza l'estensione i test dell'indice falliscono in modo esplicito.
- **Vincolo**: l'indice non diventa mai fonte di verità; una voce incoerente con il blocco (`TextHash`) è stantia e si ricostruisce.
