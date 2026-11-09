# T105 — Upgrade/restore completo DB+volume (PostgreSQL reale, senza DGX)

Prova: `backend/tests/bProjects.IntegrationTests/DocumentFullUpgradeTests.cs` (Category=Integration, richiede `BPROJECTS_TEST_POSTGRES`).

## Sequenza provata

1. Database fermo allo schema 001 (`EvidenceLedger`) con run, bozze, checkpoint, Evidence, selezioni e coverage-v1; volume vuoto.
2. Backup coerente a database e volume fermi (B1).
3. Avvio del server su una copia 001 senza `migrate`: cronologia e schema invariati (nessuna migrazione automatica).
4. Crash a metà upgrade (migrazioni applicate fino a `DocumentParsing`), nuovo processo, ripresa esplicita fino all'ultima migrazione: nessuna migrazione pendente, nessun cambiamento di modello pendente.
5. Tutte le righe 001 (ID, JSONB, conferme, checkpoint, bozze) e gli hash canonici invariati.
6. Rollback del binario 001 prima dei dati 1.1: le Down riportano allo schema 001 senza perdere righe; l'upgrade è ripetibile.
7. Dati 1.1: documento reale sul volume (SHA256 registrato), preparazioni, Evidence documentale, selezione di run; backup B2.
8. Con dati 1.1 la Down verso 001 è rifiutata («Down rifiutata») e non cambia nulla; riavvio con nuovo store: download verificato byte per byte.
9. Restore isolato di B2 (DB + volume): righe, hash e SHA256 del download identici, decoder vecchio/nuovo corretti. Restore isolato di B1: schema 001 esatto, volume 001, upgrade nuovamente applicabile.

## Difetto trovato e corretto

EF esegue ogni `Down` in una transazione propria. Il rifiuto con dati documentali esisteva solo nelle Down di `DocumentStorage`, `DocumentParsing` e `DocumentEvidence`, eseguite dopo quelle delle migrazioni più recenti: un rollback con dati 1.1 eliminava già tabelle e vincoli derivati (lavoro, regime, intervalli, proiezioni di riconciliazione) prima del rifiuto. Il guard è ora anche nella prima Down eseguita (`DocumentLimitsSnapshot`), con l'unione dei predicati; ogni futura migrazione successiva deve ripetere il guard nella propria Down.

## Limiti dichiarati

- Il backup è una copia consistente (`CREATE DATABASE ... TEMPLATE` a nessuna sessione aperta + copia della directory del volume), non gli strumenti di snapshot dello stack reale (pg_dump/snapshot di volume Docker): quelli non sono esercitati qui.
- Nessun LLM e nessun DGX.
