# Validazione parser 002 (T020–T026)

Esecuzione: 2026-11-02, Docker Desktop (linux/amd64, CPU), nessun DGX, nessun LLM, nessun contenuto riservato (solo corpus sintetico T002).
Gli esiti sono quelli osservati; ciò che non è stato provato è elencato in "Non verificato".

## Identità della build

| Elemento | Valore |
| --- | --- |
| Base immagine | `python:3.12-slim@sha256:aa943ce411423c7582288f18707ff66eddcbaeb510502d747a72aeb34f30d159` |
| Docling / docling-core / docling-slim | 2.129.0 / 2.105.0 / 2.129.0 (pin esatti, nessun "latest") |
| docling-ibm-models, docling-parse, torch, transformers | 4.0.4, 7.24.2, 2.15.0+cpu, 5.22.1 (torch CPU, nessun pacchetto nvidia) |
| `requirements.lock` (SHA-256; = `dependencyLockHash`) | `b30ce354fbd83919e78074afa6663a41d8921fe2a64f9e6fd720891616da1bef` (107 pacchetti, tutti con `--hash`; `pip install --require-hashes --no-deps`) |
| `requirements-dev.lock` (solo stage test) | `62ad55c3c5337dc2fe29f3e9827ecfa09599be269d93727e01db925b15f4005f` |
| `model-artifacts.json` | `6bf509113d3f2c06b5e4a75922ec70d87c09edb866d4446b61053c6936af204c` (16 file con size+SHA-256) |
| `modelArtifactsHash` (a runtime) | `c52bb8c0a43e172ffded91adfb13c833c3bac2add8e84a7de00d09281bd0ef51` |
| `configHash` (a runtime) | `463ab02e843ba57f7c86492773ee793b42dbb5e54932b8d96efc68284e0b7c31` |
| `Dockerfile` | `ef18c65133a2d2da87964f25cac918692105dfc2eb69ba17b4c5024c2ab6072d` |
| Immagine runtime (`bprojects-parser:dev`) | `sha256:53d84f4fced6128a400c994371a29388cee1683c365c6d51904fe999d2a7422f` (3,6 GB) |
| Corpus fixture (`tests/fixtures/documents/manifest.json`) | `e5662c30bc35bffcd9ed3e4c7c2c1fdf0e3783167221094c0d43eb6b103b674e` |

Pesi (scaricati SOLO nello stage `builder`, verificati per hash/size dal build; revisioni Hugging Face registrate in `model-artifacts.json`):

| Repo | Revisione | Licenza | Uso |
| --- | --- | --- | --- |
| docling-project/docling-layout-heron | 8f39ad3c0b4c58e9c2d2c84a38465abf757272d8 | apache-2.0 | layout PDF |
| docling-project/docling-models (TableFormer) | fc0f2d45e2218ea24bce5045f58a389aed16dc23 | cdla-permissive-2.0 | struttura tabelle PDF |
| sentence-transformers/all-MiniLM-L6-v2 (solo tokenizer) | 1110a243fdf4706b3f48f1d95db1a4f5529b4d41 | apache-2.0 | conteggio token del chunker (stima) |

Nessun modello OCR, VLM, formule, classificatore immagini. `docling-layout-heron-onnx` è scaricato dall'utility ma non usato: la conversione PDF è stata provata senza di esso e viene eliminato dal build (`UNUSED_DIRS`). Licenze dei 108 pacchetti installati (metadati): MIT, BSD, Apache-2.0, PSF; nessuna GPL/AGPL/SSPL/sconosciuta rilevata dai metadati.

## Esiti per task

| Task | Esito | Evidenza |
| --- | --- | --- |
| T020 | PASS | Prima dell'implementazione del servizio: 18 passed (validatore su manifest costruiti a mano, rileva campi/range/ownership/hash/cicli invalidi), 94 errors (servizio e conversione assenti). Fallimento rilevato come richiesto. |
| T021 | PASS | Build riproducibile con lock+hash, pesi locali verificati (`model-artifacts: 16 file attesi, 0 discrepanze`), import e conversione con `--network none`. Docling 2.129.0 compila con le opzioni usate (OCR/VLM/enrichment/remote off). Nessuna incompatibilità di licenza dai metadati. |
| T022 | PASS | `/health`, `/version`, `/convert` sincrono con segreto (`X-Internal-Secret`), 401/409/415/422/429/503, un subprocess per conversione in gruppo di processi proprio, kill a timeout, workspace rimosso, nessun flag Ready/retry. |
| T023 | PASS (fixture) | PDF testo/misto/multipagina e DOCX base/immagine: testo, pagina e bbox nativi (origine PDF), `page=null` e percorso `body/p[n]`, `body/tbl[k]/tr[i]/tc[j]` per DOCX; PDF misto e DOCX con immagine = Incomplete con issue bloccante. |
| T024 | PASS (fixture) | MD (titoli, liste, fence, tabella, link testuale non seguito, righe/range) e TXT (UTF-8, UTF-16 con BOM, rifiuto `invalid_encoding` 415, nessun heading inventato, non-BMP). |
| T025 | PASS (fixture + sintetico) | HybridChunker con tokenizer locale (`TokenCount` = stima, `tokenSource=estimated`); range Primary/Context in codepoint, nessuna sovrapposizione proprietaria, header di tabella replicato come Context, titoli Context nei chunk successivi, paragrafi lunghi = range contigui. |
| T026 | PASS | Suite completa in container hardened: **141 passed, 0 failed, 0 skipped** (139 s). Vedi sotto. |

### Suite T026 (141 test)

Comando (immagine `bprojects-parser-test:dev`, stage `test`): `docker run --rm --network none --read-only --tmpfs /tmp:size=1g,mode=1777,noexec,nosuid --cpus 2 --memory 4g --memory-swap 4g --pids-limit 256 --cap-drop ALL --security-opt no-new-privileges:true -e PARSER_EXPECT_OFFLINE=1 -v <repo>/tests/fixtures:/fixtures:ro bprojects-parser-test:dev` (utente 10001, senza rete).

- `test_contract.py` + `test_locators.py` (113): campi obbligatori, status, hash di versione/config/modelli, locator, gold del manifest T002 (fatti testo/PDF/DOCX), offset codepoint↔UTF-16 con non-BMP, copertura Primary esatta e senza duplicazioni.
- `test_offline_conversion.py` (8, incl. il test che prova l'assenza di rete: `connect`/DNS falliscono ma la conversione riesce) e `test_security.py` (20): quattro formati, determinismo del manifest (due conversioni identiche), password (`protected`), corruzione (`corrupt`), zip bomb 260 MiB espansi (da archivio < 5 MB) respinta `parse_limit_exceeded`, traversal (`../`, assoluti, `C:`), macro (`vbaProject.bin`) 415, > 10 000 entry, limite espanso richiesto dal backend, kill a timeout (gruppo di processi inesistente dopo la risposta, workspace rimosso, istanza di nuovo servibile), limite output (controllo del parent e `RLIMIT_FSIZE` con `EFBIG`), tetto server (250 MiB), segreto non ereditato dal figlio, utente non root con `/app` non scrivibile, nessun fetch di immagini/link Markdown né di relazioni DOCX esterne (listener locale: 0 connessioni), errori/versione/health senza segreto né percorsi.
- Compose (`-p bprojects-parsertest`, poi `down` del solo progetto di prova): servizio `parser` healthy, utente 10001, root read-only, `cap_drop ALL`, CPU 2, RAM 4 GiB, pids 256, nessuna porta host, nessun mount, rete `parser_net` `internal: true`; dal parser `connect` esterno e DNS bloccati; da un client sulla rete privata `/convert` risponde 401 senza segreto e 200 con segreto.

## Osservazioni e scelte (da tenere presenti per T027–T031)

- Docling 2.129.0 omette in silenzio le tabelle DOCX senza `w:tblGrid` (fixture `docx-basic`): le tabelle DOCX sono lette dalla struttura OOXML (percorsi `tc[j]`) e inserite nel documento di lavoro del chunker; il `document` serializzato nel manifest resta quello originale di Docling.
- La fixture `pdf-table-multipage` non è riconosciuta come tabella dal layout model (testo con `|`): un item Docling che attraversa due pagine viene suddiviso per `charspan` in blocchi uno per pagina (`pieceOf`), mai una quote su una sola pagina.
- HybridChunker non emette i titoli come contenuto: un titolo è `Primary` nel primo chunk che lo usa come contesto, `Context` altrove.
- Intestazioni/pie di pagina ricorrenti sono escluse dal corpo con issue `page_furniture_omitted` (warning, non incompleta); immagini ≥ 10% della pagina o in pagine senza testo, e ogni immagine/oggetto DOCX incorporato, sono issue bloccanti.
- Autenticazione: header `X-Internal-Secret` (env `PARSER_INTERNAL_SECRET`, vuoto = ogni conversione rifiutata). Compose: profilo `parser`, `app` aggiunta a `parser_net` (nessuna altra modifica ad `app`); il wiring backend resta a T029.

## Non verificato

- Tempi, memoria (4 GiB) e qualità su PDF/DOCX reali o molto grandi; il corpus è sintetico e piccolo. Tabelle PDF con linee (rilevate da TableFormer) non esercitate dalle fixture; l'header replicato è provato solo su documento sintetico.
- Limite di output 250 MB reale: provati il meccanismo e i limiti ridotti, non 250 MB effettivi.
- Revisione legale delle licenze (solo metadati e carte dei modelli); lock valido per linux/amd64: su host arm64 va rigenerato.
- Il validatore .NET (T027), la persistenza e il recupero (T028–T031) e qualsiasi prova con DGX non fanno parte di questo gate.

## Allineamento backend↔parser (T029 reale, T031, T116) — 2026-11-02

- Autenticazione: il backend ora invia `X-Internal-Secret` (prima `Authorization: Bearer`); `/version` è letto con la chiave `schema` come pubblicata dal parser.
- Nomi del manifest reale adottati dal validatore backend (il parser rispetta il contratto, nessun cambio di protocollo): `offsetConvention`, `document.serialization`, `blocks[].order`/`itemRef`/`textSha256`, `locator.bbox` come `{l,t,r,b}` (salvato come array `[l,t,r,b]`), `locator.path` (percorso DOCX, in `DoclingRef`), `table.cols` e `cells[].path`, `inventory[].state`/`itemRef` con `kind` libero, `issues[].issueRef`/`blockRef`, `ranges[].sourceTextSha256`; DOCX con `pageCount` nullable. Unica modifica al parser: `reason` aggiunta alle voci di inventario `empty` (il contratto la richiede).
- Prove (parser reale in container, `BPROJECTS_TEST_PARSER_ENDPOINT`/`BPROJECTS_TEST_PARSER_SECRET`, Skipped se assenti): `ParsedManifestRealParserTests` (9 fixture dei 4 formati accettate dal validatore), worker end-to-end su PostgreSQL in `DocumentParsingRecoveryTests` e retry API in `DocumentParseRetryTests`. Suite parser rieseguita dopo la modifica: 141 passed. Immagine runtime ricostruita: `sha256:73205c7d241a619a63973d5857d953a5fb19ec08179d931fe3f1d2e8c907a622`.
- Il parser reale ha uno slot di conversione: le classi di test che lo usano sono in una collection xUnit serializzata.
