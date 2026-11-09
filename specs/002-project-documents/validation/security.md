# Validazione sicurezza documenti (T106)

Nessun DGX, nessun modello reale: `BPROJECTS_TEST_DGX_ALLOW` mai impostata, variabili `BPROJECTS_TEST_DGX_*` rimosse dal processo di test, nessun `Category=MafReal`. Nei livelli con modello l'unico modello e il finto (`tests/e2e/support/fake-llm.ts`, `FakeUnitModel`). Questi test sono OSTILI e distinti dal percorso nominale reale (T104/DGX). I livelli sono separati: i numeri non si sommano e un livello non sostituisce l'altro.

| Livello | File | Scoperti | Superati | Falliti | Saltati |
| --- | --- | --- | --- | --- | --- |
| Backend PG + API reale (parser e modello finti) | `DocumentSecurityTests` | 24 | 24 | 0 | 0 |
| Backend PG + rig agente di estrazione (modello finto HTTP) | `DocumentPromptSecurityTests` (stesso file) | 6 | 6 | 0 | 0 |
| Parser in container hardened `--network none` (fixture + sintetico) | `parser/tests/test_security.py` | 33 (20 preesistenti + 13) | 33 | 0 | 0 |
| E2E fake-stack (browser, API simulate; 1440 e 390 px) | `document-security.spec.ts` | 10 (5 x 2) | 10 | 0 | 0 |
| Statico compose (nessun browser, nessun container avviato) | `document-security-compose.spec.ts` | 4 (2 x 2 progetti Playwright) | 4 | 0 | 0 |
| E2E stack reale + LLM finto (backend, PG isolato e parser REALI; desktop-1440) | `document-security-stack.spec.ts` | 5 | 5 | 0 | 0 |

Comandi (variabili DGX non impostate; password PG e segreto parser da `docker inspect` a runtime, mai scritte):

- backend: `dotnet test --solution bProjects.sln -- --filter-class "bProjects.IntegrationTests.DocumentSecurityTests"` e `...DocumentPromptSecurityTests` (con `BPROJECTS_TEST_POSTGRES`; senza, Skipped);
- parser: `docker run --rm --network none --read-only --tmpfs /tmp:size=1g,mode=1777,noexec,nosuid --cpus 2 --memory 4g --pids-limit 256 --cap-drop ALL --security-opt no-new-privileges:true -e PARSER_EXPECT_OFFLINE=1 -v <repo>/tests/fixtures:/fixtures:ro bprojects-parser-test:<tag> python -m pytest -q -p no:cacheprovider tests/test_security.py` (immagine dallo stage `test`);
- fake + statico: `npx playwright test document-security` da `tests/e2e` (14 = 10 + 4);
- stack reale: `npx playwright test -c playwright.real.config.ts document-security-stack`. Per includere la correzione del parser e stato usato un container parser temporaneo dall'immagine ricostruita (`E2E_STACK_PARSER_CONTAINER`/`E2E_STACK_PARSER_PORT`), rimosso a fine prova; il container `bprojects-t055-parser` preesistente non e stato modificato.

## Cosa copre ogni requisito (gia coperto = non duplicato)

| Requisito T106 | Gia coperto (non rifatto) | Aggiunto da T106 |
| --- | --- | --- |
| Auth, ruoli | matrice 52 rotte con token invalidi (`ContentSecurityTests`), Admin vs PM (`DocumentAdminAuthorizationTests`, `DocumentArchiveApiTests`) | tutte le rotte documentali con token **Admin** = 403 e anonimo = 401 (confronto con il router: una rotta nuova senza sondaggio rompe la prova); stack reale: 401 senza token e con firma manomessa |
| ID diretti di altro progetto | prove puntuali per rotta in T008/T030/T094/T095/T116 | matrice unica di TUTTE le 17 rotte documentali (404, nessun riflesso di nomi/contenuto, impronta di 20 tabelle + volume invariata, download dell'originale invariato); cursori di altro progetto; chiave di idempotenza di un altro progetto; stack reale: 9 GET + 4 comandi con ID di B dal progetto A |
| Limiti al server | upload al limite/oltre, quota, tetti Admin, selezione > documenti, `limit`/cursori blocchi | versione/generazione ostili (`0`, `-1`, `99999999999999999999`, `;DROP`, `%2e%2e%2f`) = 4xx pulito; `limit`/`cursor` anomali su elenco e blocchi; campi multipart del server (sha256, storageKey, projectId, uploadedBy, ...) **rifiutati** con 400, due file, file senza nome, boundary oltre 128, molti campi; nome > 255 e etichetta > 256 = 422 |
| Path / symlink / decompression | file store (traversal, symlink, link di directory), parser (zip bomb, traversal, > 10 000 voci) | nomi ostili via API (`../`, `..\`, assoluti, `<img onerror>`, bidi, CON, `%2e%2e%2f`) ridotti a etichetta, chiavi di storage generate dal server, nessun file fuori radice; parser: entita XML (XXE file+http, billion laughs, parametro), voce symlink e archivio annidato (300 MiB) inerti, dimensioni dichiarate bugiarde, bomba Flate PDF 300 MiB (esito pulito, nessun processo residuo), Markdown annidato 20 000 livelli, riga da 5,6 MB, NUL/controlli |
| Macro / HTML / link / prompt injection | macro DOCX (parser, upload), HTML/link in messaggi e anteprime (001), retry guidato con prima risposta non fidata | campi DOCX (INCLUDETEXT, DDEAUTO, INCLUDEPICTURE, LINK), relazioni esterne (template remoto, OLE, altChunk, VML), azioni PDF (JavaScript, URI, GoToR, Launch, file incorporato): **0 connessioni** verso un listener e nessun contenuto di sistema nell'output; testo ostile servito solo come stringa di un unico JSON; UI: nessun elemento attivo/script/link/richiesta esterna per nomi, etichette, blocchi e risposte; istruzioni nel documento arrivano al modello come dato di un intervallo (1 messaggio utente, nessuno strumento, nessun segreto) e un modello che le **obbedisce** (chiave di intervallo forgiata, prosa che rivela segreti, proprieta estranee, citazione inesistente, fonti inventate e di B) e respinto senza output ne perdite |
| Errori / telemetria redatti | log file store senza contenuto/percorsi, problemi del modello redatti (001), `issues` a testo fisso | log a livello **Trace** (header HTTP inclusi), API e **ogni tabella** del database senza segreto del parser, token, chiave di firma, password, percorso del volume, corpo di errore del parser, testo integrale dei documenti; i log catturati contengono davvero i log HTTP del parser (prova non vuota); stack reale: scansione dei log del backend con soli conteggi (7 segreti dello stack + 7 stringhe) e flusso SSE |
| Parser a rete isolata | `test_offline_conversion` (`connect`/DNS falliscono e la conversione riesce), prova a runtime manuale T026 (`validation/parser.md`) | statico: `parser_net` `internal: true`, parser solo su quella rete, nessuna porta/mount/dispositivo, non root, read-only, `cap_drop ALL`, risorse limitate, ambiente = solo il segreto, solo `app` e `parser` sulla rete, originali montati solo in `app`; stack reale: nessuna connessione dal backend ne dal container del parser all'ascoltatore, con **controllo positivo** (il container raggiunge l'ascoltatore: `true`) |
| Nessuna fonte B nel run di A | — | richieste al modello, analisi, evidenze, blocchi e `SourceSnapshotItems` di A senza il marcatore unico di B; B senza run e con contenuto intatto |

## Difetto di prodotto corretto

**Parser (`text_sources.py`)**: un'immagine Markdown DENTRO un paragrafo (`testo ![alt](url) testo`) mandava in crash la conversione (`AttributeError: 'PictureItem' ... 'text'`, risposta 503 `parser_crash`, transitoria, il documento finiva Failed dopo i tentativi). Le immagini isolate erano gia gestite. Ora il testo attorno resta, l'immagine e dichiarata `unprocessed_markdown_image` (bloccante, documento Incompleto, come per le immagini isolate) e non viene mai scaricata. Test di regressione rosso senza la correzione e verde con (`test_markdown_inline_images_...`); suite parser completa: 158 passati e 1 fallito PREESISTENTE (punto 5).

## Osservazioni NON corrette (decisione o cambio di contratto necessari, o fuori perimetro)

1. **Markdown con blocchi HTML e paragrafi con link/immagini nello stesso documento**: Docling li unisce in un solo gruppo inline, il parser non ne localizza le righe (`source_mapping_unavailable`, warning) ma il backend richiede le righe per md/txt e respinge il manifest (`manifest_inconsistent`, documento Failed). Fallimento sicuro (esplicito, nessun dato parziale), ma Markdown reale con `<br>`/`<details>` e link non e importabile. Richiede una scelta sul contratto di localizzazione (dividere il gruppo per paragrafo oppure ammettere blocchi senza righe). Nei test stack i documenti ostili tengono HTML e link in file separati.
2. Un rifiuto DOPO la ricezione del file (etichetta fuori limite quando il campo segue il file; secondo file) lascia al piu un originale non referenziato sul volume: recuperato dallo sweep degli orfani (T010), nessuna riga ne quota consumata. Non e una perdita di dati, ma consuma disco fino allo sweep.
3. Le risposte JSON dell'API non portano `X-Content-Type-Options: nosniff` ne la web app una CSP (`infra/web.conf`); l'inerzia del contenuto poggia su tipo `application/json; charset=utf-8`, rendering a testo (Angular) e parser Markdown che non produce HTML. Hardening possibile, non richiesto da FR-021.
4. I delimitatori dei contenitori (`<<<FINE FONTE>>>`, `<<<FINE rangeKey=...>>>`) non sono neutralizzati nel testo dei documenti: un documento puo far credere al modello a intervalli/fonti finti. Impatto limitato dai validatori (provato: chiavi forgiate e fonti inesistenti respinte); neutralizzarli cambierebbe prompt e hash di input (contratto), quindi non toccato.
5. `pdf-split-f1` di `test_locators.py` fallisce anche sul codice di HEAD (preesistente, non investigato, estraneo a T106).

## NON verificato / limiti

- Isolamento di rete del parser a runtime nel compose reale (`internal: true`): provato staticamente qui e a mano in T026, non rieseguito. Nello stack di prova il container e su bridge: l'assenza di egress dipende dal comportamento del parser (0 connessioni, con controllo positivo), non dalla rete.
- Ruolo Admin nei livelli E2E (lo stack ha solo un account PM): coperto dal backend. Stack reale solo desktop-1440.
- Il modello finto non prova la qualita dei modelli reali (T104/DGX). Nessun test di carico o concorrenza ostile, nessuna misura di memoria della bomba Flate oltre l'esito pulito.
- Il fake-stack prova solo cio che il browser fa con le risposte simulate, non il server.
