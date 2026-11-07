# Validazione I6 deterministica (T092)

Livello **E2E-OFFLINE (fake-stack)**, browser Chromium a 1440 e 390 px. Nessun DGX, nessun modello reale, nessun backend, PostgreSQL o parser.

## Stato di T092: PARZIALE (non spuntato)

- **Eseguito**: `tests/e2e/document-long-context.spec.ts` + `tests/e2e/support/fake-long-context.ts`, 6 test x 2 viewport = **12 discovered, 12 passed, 0 failed, 0 skipped** (`npx playwright test document-long-context.spec.ts`; log grezzi in `diagnostics-local/`, non tracciati).
- **NON eseguito**: il livello «backend + PostgreSQL + parser reali con modello finto» richiesto da T092. Questa esecuzione non ha avuto il permesso di compilare/avviare il backend (lavoro concorrente sul backend). Resta da fare e da registrare qui sotto prima di spuntare T092.

## Cosa provano i test (solo contratto HTTP letto dal browser)

| Test | Prova |
| --- | --- |
| Chat multipasso | Regime Ordinario con latenza lunga resta Ordinario (nessuna riclassificazione N->E dedotta dal browser); promozione dichiarata solo dal server con ricevuta; unita «x di y pianificate finora» con denominatore che cresce, mai percentuali/«completato»; nessuna risposta finche il server non completa; tre fonti di documenti diversi apribili da tastiera. |
| Limite documentale in chat | `document_limit_exceeded`: nessuna risposta assistant, messaggio d'errore, composer utilizzabile. |
| Analisi + copertura | Progresso multipasso in analisi; report coverage-2 al 100% per ID con nota «nessuna copertura, anche al 100%, certifica il significato», nessuna dicitura di fedelta/completezza, alternative di un conflitto aperto e frontiera mostrate integralmente; report parziale (porzioni mancanti + bloccante => «la verifica non è chiusa»); errore 500 del server => messaggio, nessun nuovo report. |
| Bozza multi-blocco | Due citazioni con la prima fonte condivisa restano distinte (mappa persistita), ciascuna con le proprie parti; fonte di una sola citazione senza etichetta «x di y»; dopo nuova versione del documento e reload le stesse parti originali si aprono, nessun comando emesso. |
| Errori/limiti analisi | `coverage_incomplete` e `context_overflow` (con token misurati): nessuna anteprima, conferma, Genera o bozza; retry con nuova chiave. |
| Disconnessione | Stream caduto con riconnessione rifiutata durante un run multipasso: il risultato perso si riconcilia dai GET, una sola connessione, nessun comando. |

## Limiti

- Le garanzie del server (scansione di tutte le unita, riduzione, fail-fast, commit/assembly, ripresa, routing) sono provate dai test backend T084/T089/T091/T078/T123, non da questo livello.
- Il parsing simulato serve blocchi canned per formato; le citazioni multi-blocco usano blocchi del fake.
- `hasSourceChanges`/`sourceMap` sono impostati dal fake come da contratto DraftView.
