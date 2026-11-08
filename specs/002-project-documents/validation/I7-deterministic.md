# Validazione I7 deterministica (T103)

Nessun DGX, nessun modello reale: variabili `BPROJECTS_TEST_DGX_*` non usate (il backend dello stack reale e avviato con `Ai__*` esplicite e fittizie; l'ambiente `Ai__*`, `AI_*`, `BPROJECTS_*` non e mai ereditato), `BPROJECTS_TEST_DGX_ALLOW` mai impostata, nessun `Category=MafReal`, nessun `E2E_MODEL_MODE=real`.

I due livelli sono **distinti** e si discoverano separatamente (`tests/e2e/document-version-recovery.spec.ts`: il livello dipende da `E2E_STACK`, impostata solo dalla configurazione dello stack reale). I numeri non si sommano e un livello non sostituisce l'altro.

## Livello 1 - E2E fake-stack (browser, API simulate)

Browser Chromium a 1440 e 390 px, nessun backend, PostgreSQL, parser o modello. Supporto: `support/fake-stack.ts` (rotta di «riavvio» `down`), `support/fake-parsing.ts` (`createGeneration`), nuovo `support/fake-lifecycle.ts` (bundle corrente, candidate, rielaborazione e promozione CAS con la semantica di `contracts/http-api.md`).

Comando: `npx playwright test document-version-recovery` (da `tests/e2e`, senza `E2E_BASE_URL`).

| Esito | Discovered | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- |
| fake-stack (4 prove x 2 viewport) | 8 | 8 | 0 | 0 |

| Prova | Cosa prova (solo contratto HTTP letto dal browser) |
| --- | --- |
| v1/v2, fissa, escludi, rielabora, promuovi | v1 e v2 Pronto, ciascuna con il proprio primo bundle corrente e nessuna candidata; fissa/ultima pronta/escludi persistono nello stato del server e sopravvivono al ricaricamento; la rielaborazione mostra «In coda» senza cambiare il bundle, a fine lavoro compare la candidata «generazione 2» (non usata come fonte), la promozione e una sola alla revisione attesa; download di v1 e v2 con hash identico; ricarica e riconnessione SSE emettono solo letture. |
| due sessioni PM | promozione simultanea = una sola promozione (l'altra e un replay 200); promozione con vista scaduta = conflitto 409 con messaggio esplicito, vista riletta, nessuna ripetizione automatica. |
| browser chiuso/riaperto, 3 sessioni con riavvio | stato (pin, versioni) dal server a ogni riapertura, nessun comando da sola riapertura, riavvio a meta rielaborazione: un solo job, una sola candidata, promozione nella sessione 3, hash degli originali intatti. |
| errori, retry, readiness, ultimo successo | parsing `Failed` => mai «Pronto», un solo retry per clic, la ricarica non ritenta; runtime non raggiungibile dichiarato dal monitor e ultimo successo misurato invariato; ritorno «in linea». |

**Limiti del livello fake** (dichiarati): non prova server, parser, PostgreSQL, concorrenza reale ne ripresa dopo riavvio, e NON verifica citazioni/bozze/snapshot identici (richiedono revisione e bozza reali: li prova il livello 2). La candidata in lavorazione e simulata come il server la espone (`processingStatus` della versione).

## Livello 2 - E2E stack reale + LLM finto (backend + PostgreSQL + parser REALI)

Backend reale (`bProjects.App.dll`, nessun hook ne fake mode, DI invariata) con `Ai__Endpoint` verso il modello finto `tests/e2e/support/fake-llm.ts` (Node, 127.0.0.1, solo il protocollo realmente usato: `GET /v1/models`, `/props`, `/slots`, `POST /tokenize`, `/apply-template`, `/v1/chat/completions` con SSE; ogni richiesta fuori protocollo o di un grafo non riconosciuto riceve 501 ed e registrata in `unexpected`, che le prove verificano vuoto). PostgreSQL: **database isolato** `bprojects_e2e_<casuale>` nel container di test `bprojects-002-test-pg2` (creato, migrato con `migrate`, account PM con `bootstrap-accounts`, eliminato alla fine; mai dati reali). Parser: container reale `bprojects-t055-parser` (127.0.0.1:58300). Frontend: build Angular servito da un proxy 127.0.0.1 con `/api` inoltrato al backend. Avvio e chiusura (anche del backend) gestiti da `globalSetup`/teardown di `playwright.real.config.ts`; ogni prova parte da un database senza progetti (`TRUNCATE ... CASCADE`) e dal modello finto azzerato. Le risposte del modello finto riusano le regole dei modelli finti in-process (estrazione per intervallo Primary, chat con citazioni dalle fonti, analisi alias-2, bozza alias) e passano tutti i validatori reali.

Comando: `npx playwright test -c playwright.real.config.ts document-version-recovery`.

| Esito | Discovered | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- |
| stack reale + LLM finto (desktop-1440, una sola esecuzione seriale) | 5 | 5 | 0 | 0 |

Smoke dell'harness (`real-stack-smoke.spec.ts`, separato): 1 discovered, 1 passed - upload -> parser reale -> estrazione col modello finto -> Pronto -> chat con fonte -> persistenza in PostgreSQL.

| Prova | Cosa prova |
| --- | --- |
| lifecycle | v1/v2 Pronto con hash in PostgreSQL uguali ai byte caricati; pin/ultima pronta/escludi persistiti in `ProjectSourceSelectionItems` e dopo ricarica; rielabora v1 crea UNA candidata (generazione 2) senza cambiare `EvidenceDocumentStates`, la promozione sposta i puntatori con `Revision`+1 e registra `DocumentPromotions`; originali scaricati con hash identico; ricarica + offline/online + rilettura: `DocumentJobs`/`EvidenceExtractionJobs`/`ProcessingRuns`/`ChatTurns`/`InferenceCalls`/promozioni invariati e nessuna richiesta al modello. |
| identita | chat con fonti, Analizza, conferma, Genera (modello finto, validatori reali); poi v2 + rielabora v1 + promozione: impronte (revisione, bozza, snapshot e voci, run, messaggi, originali gia caricati) **identiche byte per byte**, citazioni risolte dall'API con lo stesso testo esatto, nessun nuovo run/turno, nessuna chiamata AI diversa dall'estrazione, bozza e fonti apribili dopo ricarica. |
| due sessioni concorrenti | rielaborazione simultanea da due contesti: generazioni 1 e 2 soltanto; promozione simultanea: esattamente una promozione e `Revision`+1; le due viste convergono. |
| 3 sessioni con riavvio | browser riaperto dal solo cookie di refresh dopo ogni riavvio del backend, pin/versioni persistiti, lettura pura senza job ne chiamate al modello; riavvio MENTRE l'estrazione della candidata e in corso (prima chiamata rallentata): una sola candidata e un solo job di estrazione `Completed` dopo la ripresa; promozione; terzo riavvio senza lavoro nuovo; 3 promozioni totali, hash intatti. |
| errori/retry/readiness/ultimo successo | PDF corrotto => parsing `Failed`, ricaricare non ritenta, un solo nuovo `DocumentJob` per clic; 503 transitorio dell'estrazione => documento Pronto col tentativo fallito e quello vincente registrati in `InferenceCalls`; 503 persistente => mai «Pronto»; runtime spento => il monitor lo dichiara e il turno di chat e `Failed` senza sostituire l'ultimo successo; ritorno in linea => nuovo messaggio, nuovo ultimo successo. |

### Difetti di prodotto emersi dal livello reale (corretti, commit separati)

1. **Backend/contratto (`DocumentCatalog.cs`)**: `candidatePreparations` era serializzata come array di UUID, contro `http-api.md` (array di oggetti `{preparationId, generation, processingStatus, evidenceVersionId, createdAt, canPromote}`) e contro il frontend T101: la UI reale non poteva mai mostrare ne promuovere una candidata. Corretto (`CandidatePreparationView`); `DocumentVersionLifecycleTests` aggiornato alla forma del contratto (52/52 PASS dopo la correzione). Nota: il contratto menziona anche candidate fallite, il backend e i suoi test (T093/T098) le escludono («la fallita non e una candidata promovibile»): contraddizione documentale NON risolta, invariata.
2. **Frontend (`project-documents.component.ts`)**: il polling osservava solo l'ultima versione dell'elenco e le candidate: dopo «Rielabora» su una versione non ultima la vista restava ferma su «In coda». Corretto (osserva anche `processingStatus` delle versioni del dettaglio aperto); nuovo test Vitest, verificato rosso senza la correzione (48/48 PASS con).

## NON eseguito / limiti

- Non eseguite la suite backend completa e la suite Playwright di default completa: solo i file toccati (`DocumentVersionLifecycleTests` 52/52, `document-version-recovery.spec.ts`) e le suite fake-stack che condividono `fake-stack.ts`/`fake-parsing.ts` (vedi I6). Vitest completa rieseguita a fine lavoro dopo entrambe le correzioni frontend (T103 e T092): 23 file, 423/423 PASS.
- Livello reale: solo desktop-1440; nessun mobile-390. Tokenizer del modello finto = parole separate da spazi; l'estrazione dei messaggi PM restituisce zero evidenze motivate (la sola forma valida e ripetibile senza inventare).
- Il modello finto non prova qualita ne conformita semantica del modello reale: quelli restano T104/DGX.
- Non sono provati qui i task T104 (DGX), T106, T107.
