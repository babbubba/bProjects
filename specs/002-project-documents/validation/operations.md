# Istruzioni operative del delta 002 (T113)

Solo il delta documentale. Per tutto il resto valgono `README.md`, `CLAUDE.md` e le istruzioni della 001, invariate. Nomi e comandi sono quelli di `infra/compose.yaml`, `backend/src/bProjects.App/Documents/DocumentOptions.cs`, `frontend/package.json` e `tests/e2e/`. Dettagli: `quickstart.md`, `validation/migration.md`, `validation/parser.md`, `validation/verification.md`. Questo file non attesta prove DGX né l'accettazione della 002.

## Confine col ramo legacy 001

- Senza documenti il comportamento 001 resta invariato (viste 1.0, coverage-v1, FIFO, conferma/generazione). La 001 resta accettata con riserve per il solo pilota; T052/T053 aperti, non PASS.
- Non toccare Constitution, artefatti e checklist della 001, né `docs/acceptance-trace/` e `diagnostics-local/` (locali, non tracciati).
- Mai `docker compose down -v` su uno stack con dati reali: cancella i volumi `pgdata` e `documents`.

## Configurazione e limiti documentali

- Originali: volume Docker `documents`, montato su `/var/lib/bprojects/documents` (`Documents__StorageRoot`), privato, nessuna porta né URL pubblica.
- Limiti e quote (sezione `Documents`, `DocumentOptions`): default nel codice, ad esempio `MaxFileBytes` 50 MB, `ProjectQuotaBytes` 1 GB, `MaxExpandedBytes` 250 MB, `MaxPages` 1000, `MaxBlocks` 100000, `ParseTimeoutSeconds` 300, `OrdinaryDeadlineSeconds` 180, `ExtendedDeadlineSeconds` 3600, `MaxAiCallsPerRun` 512, `MaxReconciliationLevels` 32, `MaxCandidatesPerRun` 100000. Sono validati all'avvio. Compose espone solo `Documents__StorageRoot`: per cambiare i limiti usare l'Admin (`GET/PUT /api/admin/document-limits`, ruolo Admin) oppure aggiungere localmente variabili `Documents__<Nome>` all'ambiente di `app`.
- Account Admin: solo con `bootstrap-accounts` esplicito (`Bootstrap__Admins__N__UserName`), mai da `infra/.env`.
- Finestra e limiti del modello: variabili `AI_*` di `infra/.env.example`, invariate dalla 001 (`AI_CONTEXT_WINDOW_TOKENS` deve coincidere con `n_ctx` del runtime, altrimenti `context_window_mismatch`).
- Parser: `PARSER_BASE_URL` (con il profilo `parser`: `http://parser:8000`) e `PARSER_INTERNAL_SECRET` (almeno 32 caratteri casuali, condiviso con il parser). Vuoti = parsing non configurato, i documenti restano In coda.

## Parser offline, pesi, licenze

- Servizio `parser` nel profilo compose `parser` (Docling CPU, cartella `parser/`): rete `parser_net` interna senza egress, root read-only, `cap_drop ALL`, 2 CPU, 4 GiB, nessuna porta host, nessun mount degli originali.
- Pesi (layout, TableFormer, tokenizer) scaricati solo nello stage `builder` e verificati con `parser/model-artifacts.json` (hash e dimensioni); a runtime nessun download. Dipendenze da `parser/requirements.lock` con hash, valido per linux/amd64 (su arm64 va rigenerato). Licenze e revisioni dei pesi: `validation/parser.md` (Apache-2.0, CDLA-Permissive-2.0; solo metadati, revisione legale non eseguita).
- Build e avvio: `docker compose -f infra/compose.yaml --profile parser up -d --build --wait parser` (immagine di circa 3,6 GB).
- Ricostruire l'immagine dopo ogni modifica al parser: un'immagine precedente alla T106 manda in crash la conversione Markdown (difetto ambientale visto in T108).

## Backup coerente e migrate esplicito

1. Fermare `web` e `app`: `docker compose -f infra/compose.yaml stop web app`.
2. Backup coerente di database E volume `documents` insieme, a servizi fermi, riferiti allo stesso istante. `validation/migration.md` prova la sequenza con copia a database fermo; gli strumenti dello stack reale (pg_dump, snapshot di volume Docker) non sono stati esercitati.
3. `docker compose -f infra/compose.yaml up -d --wait db parser`, poi `docker compose -f infra/compose.yaml run --rm app migrate`. Il server non migra mai da solo: senza `migrate` un database 001 resta invariato.
4. `docker compose -f infra/compose.yaml up -d --build --wait app web`.

Rollback: senza dati 1.1 le `Down` riportano allo schema 001 senza perdere righe. Con dati documentali la `Down` è rifiutata e non cambia nulla: l'unico ritorno è il restore di DB e volume dello stesso backup. Ogni futura migrazione deve ripetere il guard nella propria `Down`.

## Avvio senza DGX

Lo stack parte senza DGX raggiungibile: `AI_ENDPOINT`, `AI_MODEL_ID`, `AI_API_KEY` sono obbligatorie ma non vengono contattate all'avvio. Upload, archivio, parsing e consultazione dei documenti non richiedono il modello; chat, analisi e documento falliscono con errore esplicito (nessun fallback cloud).

## Livelli di test e discovery

I numeri dei livelli non si sommano e uno skip ambientale non è un PASS: verificare che la categoria abbia casi scoperti. Esiti e conteggi dell'ultima campagna: `validation/verification.md`.

| Livello | Comando | Requisiti |
| --- | --- | --- |
| Deterministico (DET) | da `backend/`: `dotnet test --solution bProjects.sln --filter-not-trait "Category=MafReal"` | nessuno; i test PG e parser risultano ignorati |
| PostgreSQL | stesso comando | `BPROJECTS_TEST_POSTGRES` (database di test isolato); con `BPROJECTS_TEST_PARSER_ENDPOINT` e `BPROJECTS_TEST_PARSER_SECRET` anche i test col parser reale |
| Solo Integration | `dotnet test --solution bProjects.sln --filter-trait "Category=Integration"` | `BPROJECTS_TEST_POSTGRES` |
| Parser (pytest) | stage `test` di `parser/Dockerfile`, in container senza rete (`--network none`, root read-only, `--cap-drop ALL`, `PARSER_EXPECT_OFFLINE=1`, fixture in `/fixtures:ro`); comando completo in `validation/parser.md` | Docker; un fallimento preesistente dichiarato in `validation/verification.md` |
| Vitest | `npm --prefix frontend ci`, poi `npm --prefix frontend run test:ci` | Node come da `CLAUDE.md` |
| E2E fake-stack | `npm --prefix frontend run test:e2e` (o `npx playwright test` da `tests/e2e`) | nessuno stack; le prove `[E2E reale]` e `E2E_MODEL_MODE=real` risultano ignorate |
| Stack reale con LLM finto | da `tests/e2e`: `npx playwright test -c playwright.real.config.ts` | container PostgreSQL e parser di test già in esecuzione (variabili `E2E_STACK_*`, default in `tests/e2e/support/real-stack/launcher.ts`); modello finto, nessun DGX |
| DGX reale | `dotnet test --solution bProjects.sln --filter-trait "Category=MafReal"`; E2E con `E2E_MODEL_MODE=real` | `BPROJECTS_TEST_DGX_ENDPOINT`/`_MODEL`/`_API_KEY` e opt-in `BPROJECTS_TEST_DGX_ALLOW=1`, solo su mandato separato e slot seriale |

Per le campagne non-DGX rimuovere dal processo le variabili `BPROJECTS_TEST_DGX_*` e non impostare mai `BPROJECTS_TEST_DGX_ALLOW`. Restano aperti: prove DGX, parte umana di T107, T114.

## Diagnosi: errori, coda, recupero

- Stato: `GET /api/projects/{projectId}/documents` e `.../documents/{documentId}/versions/{version}/preparations/{generation}`; i documenti restano In coda se il parser non è configurato.
- Codici parser/backend: `parser_not_configured` (URL o segreto vuoti), `parser_unauthorized` (segreto diverso), `parser_unreachable` e `parser_unavailable` (parser fermo o rete), `parser_busy` (slot di conversione unico occupato), `parser_incompatible` e `parser_config_mismatch` (versione o configurazione del parser cambiata), `parser_unsupported_format`, `document_incomplete` (porzione non leggibile: escludere esplicitamente la fonte per procedere), `document_limit_exceeded` (limite o quota raggiunti: esito esplicito, non successo).
- Modello: `context_window_mismatch` (finestra configurata diversa da `/props`), `model_not_available` (modello non esposto da `/v1/models`).
- Retry: `POST /api/projects/{projectId}/document-jobs/{jobId}/retry` per il parsing fallito; `POST .../versions/{version}/preparations` per la rielaborazione. I risultati precedenti restano.
- Coda: un solo slot di inferenza, quindi analisi, chat ed estrazioni attendono in FIFO; la deadline parte dal comando del PM e il retry non la azzera.
- Riavvio o crash: stato e lease si recuperano da PostgreSQL; nessuna migrazione automatica. Controllare `GET /health` di `app` e lo stato `healthy` di `parser` con `docker compose -f infra/compose.yaml ps`.
- Log: nessun testo integrale dei documenti; segreti mai stampati.
