# Validazione I4 reale (T067)

Esecuzione: 2026-11-03, serializzata (una sola prova DGX per volta, mai due run in parallelo). **Esito complessivo: T067 NON chiuso, FAIL.** Parser Docling reale, PostgreSQL 18 reale, SDK MAF 1.26.0, modello interno reale (Qwen3.6-35B su DGX, runtime `--ctx-size 32768`, `--parallel 1`, nessun `--reasoning-budget`). Il percorso reale arriva fino a documenti Ready e chat con fonti (PASS, anche nel browser); **l'analisi documentale con il modello reale non produce una revisione valida** (1 riuscita su 25 chiamate di analisi), quindi modifica PM, conferma, Genera, bozza, attribuzioni e SC-003 al 100% restano **NOT VERIFIED/FAIL**. Un errore gestito (`work_output_invalid`) non e un PASS e nessuna asserzione, gold o contratto e stato adattato a una risposta sbagliata del modello. Non chiude SC-004 oltre contesto ne l'accettazione umana. Nessun segreto riportato.

## Configurazione (dichiarata)
- Test backend: `backend/tests/bProjects.IntegrationTests/DocumentSmallFlowMafRealTests.cs` (categoria `MafReal`, collection `RealParser`): app di produzione in processo (`WebApplicationFactory`), **dispatcher e worker di parsing in background come `Program.cs`** (nessun passo pilotato dal test), `AddAiWorkflows`, `EvidenceExtractor` e `DocumentEvidenceAgent` reali, parser Docling reale (container `bprojects-t055-parser`), PostgreSQL reale. Nessun modello o parser finto.
- Opzioni AI: come compose, con **due scarti dichiarati** imposti dalla finestra reale W=32768: Analysis `MaxOutputTokens=16384` (il default 32768 non lascia budget di input) e **Analysis ragionamento OFF**. Chat/Documento/Estrazione come compose (ragionamento OFF, 4096/16384). Deadline run di analisi 1200 s.
- **Warm-up del router** prima di ogni test (16 token, stesso modello): a modello scaricato la sonda del runtime non vede la finestra e il preflight da `runtime_unverified`. Senza warm-up la 1a esecuzione ha fallito entrambi i documenti di N1 cosi (2 su 2): e un limite di produzione, non nascosto.
- Browser: `tests/e2e/document-small-flow-real.spec.ts` su stack Docker reale (progetto compose `bprojects-t067`, env fuori dal repo: db + app + parser Docling + web nginx) con DGX reale.
- Attese annotate nel codice PRIMA delle prove (cinque casi nominali, `Nominal(...)`): N1 PDF+DOCX (24 mesi, accesso anonimo vietato, ordini annullati esclusi, quinto giorno lavorativo, tabella Operatore/Sola lettura, SSO; correzione PM «garanzia»); N2 Markdown+TXT (modifiche escluse, collaudo 2027-03-01 da tabella, email senza consenso, resi non richiesti, carta vietata, esportazioni «tranne» anagrafici, «45 giorni, non 30» senza affermare 30, iniezione non eseguita, zero voci `Declared`; correzione PM); N3 conflitto documento-documento (30/60 giorni) + tabella PDF a due pagine (R-01..R-05, R-05 esclusa dal rilascio 1) con risoluzione PM del conflitto; N4 conflitto documento-PM (PM 45 giorni `Declared` contro scheda A 30 giorni `Documented`, conflitto Open); N5 nuova versione (90 giorni) + esclusione di un documento, revisione confermata invariata.
- Ogni caso verifica in modo vincolante: Ready dei documenti, chat (ogni `[fonte:id]` = fonte reale con citazione esatta nel blocco, contenuto atteso), analisi (revisione 1.1.0, **100% citazioni documentali = ID reale dell'evidenza + blocco/range + citazione esatta**, ogni evidenza rappresentata, attese semantiche, nessuna inversione, Declared/Documented coerenti con le fonti), correzione PM (nuova revisione PmEdit con fonte PmRevision, fonti documentali immutate), conferma, bozza (Proposed/NotApproved, marcatori nella revisione confermata, attese), classificazione reale (Classified, ricevuta SHA-256, record unico), chiamate non troncate.

## Esiti per scenario (ultima serie con il prompt finale; ogni caso eseguito una volta per serie)
| Caso | Documenti Ready (parser+estrazione reali) | Chat con fonti | Analisi | Correzione PM / conferma / Genera / bozza |
| --- | --- | --- | --- | --- |
| N1 PDF+DOCX | PASS (9 evidenze: pdf 4, docx 5) | PASS: «24 mesi» e «accesso anonimo non ammesso» con 2 fonti reali, citazioni esatte; Classified/Ordinary `single_call_fit`, 4,2 s | **FAIL** `work_output_invalid`: `contextAndScope.included[0]` non e un item (il modello ha scritto stringhe-id) | NOT VERIFIED |
| N2 MD+TXT | PASS (10 evidenze: md 5, txt 5) | PASS (45 giorni, esclusione; Ordinary) | **FAIL** `work_output_invalid`: stesso errore di struttura (`included[0]`) | NOT VERIFIED |
| N3 conflitto doc-doc + tabella PDF | PASS (7 evidenze) | **FAIL** (ultima serie): turno `work_output_invalid`, il modello e entrato in un ciclo di ripetizione fino a `finish=length` a 4096 token. Nelle 3 serie precedenti PASS con 30 E 60 giorni citati come contraddizione e R-05 esclusa | **FAIL** (serie precedente `work_output_invalid`: id duplicati fra alternative, mappa con id non d'item) | NOT VERIFIED |
| N4 conflitto doc-PM | PASS (1 evidenza) | PASS (30 giorni; Ordinary) | **FAIL** `work_output_invalid`: fonte `Message` senza i campi null, id duplicati | NOT VERIFIED |
| N5 versione ed esclusione | PASS (v1: 2 documenti) | v1 non disponibile nell'ultima serie (analisi v1 fallita: mancano `requirements`/`operationalFlows`/...). Nella serie `023725` (unica analisi riuscita) vedi sotto | **FAIL** (ultima serie); `023725`: analisi v1 PASS, analisi v2 FAIL (manca `sourceCutoffSequence`) | NOT VERIFIED (generazione mai raggiunta) |

### Cio che il modello reale ha DIMOSTRATO (PASS vincolanti, serie `023725` di N5, con il prompt precedente all'ultima aggiunta)
- Analisi v1 (2 documenti): revisione 1.1.0, 30 giorni `Documented` con fonte esatta e documento delle note (45 giorni) rappresentato; conferma PM 200 `PmConfirmed`.
- Nuova versione v2 (90 giorni) e **esclusione** del documento delle note: preparazione Ready; **la revisione confermata sulla v1 resta identica byte per byte**; il passaggio storico della v1 resta leggibile con il testo originale; **chat v2**: cita solo il passaggio della v2, riporta «90 giorni» e la condizione «diverso accordo scritto», non cita la v1 ne il documento escluso.
- Classificazione reale: tutti i turni di chat sono `Classified/Ordinary` (`single_call_fit`, payload misurato, ricevuta SHA-256, record unico, osservato prima dell'avvio, trascorso 3,4-5,9 s, coda ~0,3 s, preflight ~0,5 s). Analizza e sempre Extended `workload_unknown` (limite T124 noto).
- Estrazione e chat: **tutti e quattro i formati Ready** (PDF testuale, DOCX, Markdown, TXT) con evidenze corrette nei gold (gia provati in T055); chat con 100% di marcatori su fonti reali e citazioni esatte in 21 turni su 23 (2 FAIL: 1 UUID di fonte corrotto dal modello, 1 ciclo di ripetizione).

## Statistiche reali
- Chiamate di analisi documentale: 25, **1 riuscita** (N5 v1). Chat: 23 turni, 21 PASS, 2 FAIL (UUID di fonte corrotto da 1 carattere: rifiutato dal validatore e corretto; ciclo di ripetizione fino a `finish=length`). Estrazione: 0 fallimenti dopo il warm-up (la 1a esecuzione senza warm-up: 4 documenti `runtime_unverified`).
- Durata analisi: 31-136 s (completion 1,9-9,2 k token); N1 9 evidenze: input ~6,2 k token. Chat 2-6 s.

## Diagnosi dei fallimenti (parser / prompt / modello / validatore, distinti)
Per ogni fallimento si e letto l'output grezzo (diagnostica MAF locale, fuori da Git) e, dove serviva, misurato con `/tokenize` del DGX.
1. **Configurazione/modello (ragionamento)**: con Analysis ragionamento ON e max_tokens 16384 e poi 20480 la chiamata e finita `finish=length` (272 s e 348 s). I token visibili erano **6547 e 1511** (misurati): il resto (~9,8 k e ~19 k) e ragionamento nascosto senza limite (il server non ha `--reasoning-budget` e il backend non lo passa). Con W=32768 il ragionamento ON per Analysis non e utilizzabile: scelto OFF (scarto dichiarato). NOT VERIFIED il default di compose (ON, 32768) su un runtime W=131072.
2. **Modello + prompt (ripetizioni)**: con ragionamento OFF il modello ha ripetuto in loop (a) l'ultimo `entryId` di `visitedEntryIds` 278 volte dopo averne elencati solo 6 su 9 (ordinare e ricopiare hash da 64 caratteri sparsi nelle voci), (b) l'intero array `requirements` 7 volte (32 k caratteri), (c) una risposta di chat fino a 4096 token.
3. **Prompt (difetti reali, corretti)**: «id nuovo» era ambiguo (il modello usava etichette `req-1`); `certezza=Declared` nell'intestazione di una voce veniva copiata nella classificazione di elementi che vengono solo da documenti; nessuna enumerazione delle proprieta richieste (il modello ometteva `visitedNodeIds`, i campi `null` delle fonti, `sourceCutoffSequence`); la richiesta di emettere `candidates` con `id` inventati (il modello non puo calcolare `candidateId`/hash) faceva fallire ogni caso con un conflitto.
4. **Modello (errori di copia/struttura non attribuibili al prompt)**: UUID di una fonte riprodotto con una cifra diversa (chat N2: `…940678d` -> `…9406788`); `preparationId` con un gruppo di 7 caratteri; `evidenceMap.evidenceIds` con un hash di 64 caratteri o con id di FONTE al posto di id di EVIDENZA; item con `id` non UUID o duplicati fra alternative; `contextAndScope.included` come lista di stringhe-id; fonte `Message` con campi documentali non null; sezioni intere omesse. Ogni errore e diverso dal precedente (non deterministico), con 60+ identificativi da riprodurre alla lettera per risposta.
5. **Validatore/prodotto (difetto reale, corretto)**: due evidenze che citano lo **stesso passaggio** (stesso `sourceId`: due righe della tabella Markdown) facevano lanciare `ArgumentException` (chiave duplicata) in `ReconciliationReferences.Index`, classificata `model_error` permanente invece di validare. Era presente in ogni run su quel dato, indipendentemente dal modello.
6. **Parser**: nessun fallimento in questa campagna (0 rc=-4/422 su oltre 50 documenti elaborati).

## Difetti trovati e correzioni minime (con test deterministici)
- `AI/ReconciliationReferences.cs` (`Index`): fonti raggruppate per `sourceId`, proprietari come insieme; test rosso osservato `ReconciliationCoreValidationTests.Two_evidences_citing_the_same_source_are_valid_input_and_never_crash_the_reference_index` (`ArgumentException: An item with the same key has already been added`), poi verde.
- `AI/Prompts/document-analysis-v2.md` (stesso `PromptVersion`): id UUID, `certezza` != classificazione, JSON compatto, `visitedEntryIds` copiato dal server, dodici proprieta di ogni result, tredici di ogni fonte, quindici di `analysis`, id distinti, nessun elemento ripetuto, **nessun candidato/disposizione/proposta/patch** (conflitti solo in `analysis.conflicts`). Test: `DocumentAnalysisPromptContractTests` (9, DET, contratto del TESTO inviato: il rosso era osservato dal modello reale, i test sono scritti dopo le evidenze grezze e non provano la qualita).
- `AI/DocumentAnalysisWorkflow.cs` (`BuildMessages`): il server fornisce nei «dati affidabili» l'elenco `visitedEntryIds` gia ordinato con il conteggio; asserzione in `DocumentAnalysisWorkflowTests` (stessa regola del contratto, nessun validatore allentato).
- Non toccati: contratti, validatori, codec strict, gold, asserzioni del test reale.
- Test deterministici rilanciati (PG, DGX non usato): `ReconciliationCoreValidationTests`, `ReconciliationCoreContractTests`, `DocumentAnalysisWorkflowTests`, `DocumentAnalysisServerRulesTests`, `DocumentAnalysisSchemaTests`, `DocumentAnalysisPromptContractTests`, `DocumentSmallFlowTests`, `DocumentWorkflowRoutingTests`, `DocumentSourceSnapshotTests` = **180/180 PASS** (riesecuzione finale dopo l'ultima modifica, PG 55432, DGX non usato). Nessuna regressione completa.

## Esito browser (stack reale, 2 esecuzioni, desktop-1440): FAIL
Stack Docker reale `bprojects-t067` (db + app + parser Docling + nginx, nessuna route simulata), account PM da `bootstrap-accounts`, DGX reale. Spec `tests/e2e/document-small-flow-real.spec.ts`, nessun fake, un solo viewport (slot DGX unico).
- **PASS (vincolanti)**: upload dei quattro formati dal browser (`pdf-text.pdf`, `docx-basic.docx`, `md-basic.md`, `txt-negations.txt`) -> tutti «Pronto» con parser e estrazione reali (nessun comando AI oltre a creazione progetto e upload); originale PDF scaricato identico per hash; chat: risposta con «24 mesi» e «accesso anonimo non ammesso», due fonti `F1`/`F2` apribili che mostrano il passaggio reale (una contiene «24 mesi», nessun marcatore sconosciuto); monitor: «Regime: Ordinario», ricevuta SHA-256 visibile (`13dc90472e63…` e `d378ba7b7207…`), CTX 32.768, 115-118 token generati.
- **FAIL**: Analizza -> «Analisi non riuscita. Output del modello troncato dal limite di lunghezza» (esecuzione 1 con la configurazione del compose, ragionamento ON; esecuzione 2 con `AI_ANALYSIS_ENABLE_THINKING=false` e output 16384, uguale al test backend: ancora troncato, stessa causa del ciclo di ripetizione del modello). Anteprima, correzione PM, conferma, Genera, bozza, attribuzioni visibili e axe sull'anteprima/bozza: **NOT VERIFIED** (mai raggiunti). La UI ha mostrato correttamente l'errore del run senza anteprima, conferma, Genera o bozza (comportamento corretto, ma errore gestito = FAIL, non PASS).
- Screenshot/trace in `tests/e2e/test-results` (fuori da Git).

## Raccomandazione (decisione al coordinatore, non implementata: cambierebbe i contratti)
Il contratto `{results, analysis, evidenceMap}` richiede al modello di riprodurre alla lettera 60+ UUID/hash e di inventare UUID di item: con questo modello a W=32768 la risposta valida e rara (1/25). Le strade che non adattano la qualita al modello ma riducono la superficie d'errore: (a) alias brevi nei prompt di chat/analisi risolti dal server (come gia `rangeKey` in T049), (b) il server costruisce `sources` e `evidenceMap` dalle sole `evidenceRefs` e assegna gli id degli item (il modello emette chiavi locali), (c) schema strutturato completo inviato al server (grammatica) al posto della sola busta, (d) valutare il profilo W=131072 (Smart) con ragionamento controllato (`--reasoning-budget`). Ognuna e una modifica di contratto (T059/T120): da decidere prima di rieseguire T067.

## NOT VERIFIED / limiti
- Modifica PM, conferma, Genera, bozza, attribuzioni (PmRevision) e coverage-2 con modello reale: mai raggiunti oltre la conferma di N5 v1; il 100% di SC-003 su analisi e bozza non e verificato.
- Con modello reale non provati: conflitto doc-doc e doc-PM in analisi (N3/N4: mai passata l'analisi), risoluzione PM del conflitto, nuova analisi v2 (N5 v2), iniezione non eseguita (N2), tabella PDF in analisi.
- Il ragionamento ON per Analysis e il profilo W=131072 non sono stati provati.
- La campagna usa un solo modello/runtime; i risultati non sono generalizzabili a Qwen3.6-35B-Smart o altri.
- Warm-up del router fuori dal prodotto: in produzione, a modello scaricato, la prima richiesta documentale fallisce `runtime_unverified` senza retry (T124).
- `recoveryMs` null (T102), Analizza non e mai Ordinary (T124), `hasSourceChanges` non esposto: invariati.

---

## Campagna comparativa a W=131072 (2026-11-03) — T067 resta aperto

Dettaglio e misure: `I4-real-131k-faseA.md`, `I4-real-131k-faseB.md`, `I4-real-131k-faseC.md`. Nessun contratto, schema, validatore, prompt, gold o asserzione è stato modificato; il DGX è stato usato in modo serializzato (una inferenza alla volta). Lo storico dei FAIL precedenti sopra resta valido.

### Tabella comparativa (una esecuzione per cella; n=1)

| Modello/profilo | W effettiva | Reasoning | Budget reasoning | Input/Output analisi (N1/N2/N3) | N1 | N2 | N3 | Tempo analisi | Memoria | Errori |
|---|---|---|---|---|---|---|---|---|---|---|
| Qwen3.6-35B (baseline storica) | 32768 | OFF | – | – | – | – | – | – | unavailable | 1/25 analisi valide: id/hash copiati male, loop, campi omessi |
| Qwen3.6-35B-Smart (A) | 131072 verificata | OFF, out 16384 | – | 6287/7317, 6332/8887, 5267/6442 | PASS | FAIL | FAIL | 107–155 s | unavailable | N2: id di fonte al posto di id di evidenza (25+16 riferimenti); N3: 14 id item non UUID (etichette) |
| Qwen3.6-35B-Smart (B) | 131072 verificata | ON, out 32768 | 12288 (applicato, provato) | 6268/13968, 6284/18393, 5245/17271 | PASS | FAIL | FAIL | 236–320 s | unavailable | N2: `overview` nullo (strutturale); N3: UUID di fonte troncato a 35 caratteri |
| Qwen3.8-Flash-Next-Smart (C1) | 131072 verificata | OFF, out 16384 | – | N3: 6013/6159 | FAIL | FAIL | FAIL | N3: 275 s | unavailable | N1/N2: quote di tabella non verbatim già in estrazione (analisi non raggiunta, NOT VERIFIED); N3: 9/9 `evidenceIds` con hash della voce |
| Qwen3.8-Flash-Next-Smart (C2) | 131072 verificata | ON, out 32768 | 12288 (applicato, provato) | N3: 6011/18325 | FAIL | FAIL | FAIL | N3: 768 s | unavailable | N1/N2 come C1; N3: 4 violazioni (evidenceIds vuoto/disordinato, itemId pendente, fonti Message con campi documentali) |

Server: llama.cpp b10703, router mode, 1 slot, `n_ctx` 131072 da `/props?model=` e `/slots?model=` (nessuna riduzione osservata, nessun offload dichiarato: `--n-gpu-layers all`). Client MAF: `enable_thinking` e `max_completion_tokens` come da cella, temperatura 0.1, margine di sicurezza 1024; parametrizzazione solo via env nel test (`BPROJECTS_TEST_CTX_WINDOW`, `_ANALYSIS_MAX_OUTPUT`, `_ANALYSIS_THINKING`, `_SAFETY_MARGIN`), asserzioni invariate.

### Comparabilità e limiti
- Stessi corpus, prompt, schema, validatori e decoder client; cambiano profilo/W/reasoning/output. Il profilo Smart impone però parametri di campionamento del server (es. `presence-penalty 1.5` per Qwen3.6-Smart, 0 per Qwen3.8): non è un confronto "a un solo fattore" tra famiglie, e l'effetto di `presence-penalty` sulla copia di UUID è un'ipotesi NON provata.
- n=1 per cella; il codec rifiuta alla prima violazione, quindi conflitti, tabella R-01..R-05 e citazioni di N2/N3 sono NOT VERIFIED nei casi falliti.
- Unavailable: memoria/KV cache (niente nvidia-smi, `/metrics` 501), tempo di caricamento del modello, `models.ini` (SSH non disponibile), conteggio esatto dei token di reasoning (stimato per differenza), varianza.
- Nessun errore di contesto, `finish=length`, loop o `runtime_unverified` nelle prove a 131K: il limite di W non spiega più i fallimenti; il budget di reasoning è applicato ma non risulta causale (al tetto in N2/N3 di B, non in N1).

### Errori strutturali vs semantici
- **Strutturali/id/riferimenti (dominanti)**: id di fonte al posto di evidenza, hash della voce al posto dell'UUID, etichette al posto degli UUID di item, UUID troncato, campo nullo. Il tipo di errore **cambia a ogni run e a ogni configurazione** (A→B→C) senza ridursi.
- **Semantici**: osservati solo in C (quote di tabella non verbatim in estrazione); per il resto non valutabili.

### Ripetizione
Nessun profilo ha superato N1–N3: le 3 ripetizioni di N1 non sono state eseguite (gate non soddisfatto). N4/N5 e browser reale a 131K non eseguiti.

### Gate e conclusione
Gate non soddisfatto in nessuna configurazione. **Conclusione: B**, con cautela. A 131K l'analisi passa da 1/25 (W=32768) a 1/3 per cella (Smart), ma con campioni non confrontabili in numerosità: il miglioramento non è statisticamente stabilito. Il dato robusto è un altro: eliminati contesto, output e loop, restano errori di copia di identificativi che si spostano tra run. Qwen3.8-Flash-Next-Smart è peggiore (0/3, più lento, errori già in estrazione). Serve il contratto con alias brevi e ricostruzione server-side.

Profilo da preferire per i prossimi test: `Qwen3.6-35B-Smart`, W=131072, reasoning OFF (stesso esito di B a 1/2–1/3 del tempo). Non è un'adozione di produzione.

### Rischi residui
Warm-up del router (sotto), memoria non misurata, campioni piccoli, parametri di campionamento del profilo non isolati, DGX a modello scaricato.

### Proposta separata: readiness del runtime (non implementata, nessun fallback)
Oggi, a modello scaricato, il preflight dà `runtime_unverified` senza retry (T124). Disegno proposto come task specifico: nel preflight distinguere con codici diversi (a) modello non caricato/caricamento in corso → readiness con warm-up bounded (poche sonde con backoff, tetto fisso, idempotente, dentro la deadline originale dal `RequestedAt`, mai oltre il suo hard cap); (b) runtime irraggiungibile (timeout/connessione) → stessa readiness bounded; (c) runtime incompatibile o finestra insufficiente → Failed immediato; (d) tokenizer/configurazione non verificabili → unavailable, mai classificato valido per Ordinary. Esaurita la readiness il root fallisce con il codice del caso, senza inferenza su runtime ignoto.

### Proposta separata: contratto alternativo (non implementato, serve decisione)
Alias brevi per Evidence e Source assegnati dal server nel prompt; id canonici UUID/hash generati dal server; `sources` ed `evidenceMap` ricostruiti deterministicamente dal server dagli alias; relazioni semantiche dichiarate esplicitamente dall'LLM; validazione e coverage invariati. Nessuna ADR; richiede modifica di T059/T120/T121 e relativi test.

---

## Chiusura I4 — sessione 2026-11-07 (HEAD 63dfde0): T126 reale e prima prova N4

**Stato: I4 NON COMPLETATA.** T126 verificato con il router reale nello stato naturale; N4 FAIL; N1/N2/N3/N5 e browser reale NON eseguiti (gate N4 non soddisfatto, regola «una prova N4»). Una sola inferenza DGX in questa sessione (il test N4).

### Preflight offline (variabili `BPROJECTS_TEST_DGX_*` rimosse, PG reale 15432 + parser Docling reale 58300)
12 classi pertinenti (readiness, classificazione, deadline, routing, retry bozza/analisi, alias chat/analisi/bozza, small flow con fake, DgxGuard): 251 test, 251 superati, 0 falliti, 0 ignorati. Build 0 errori. Un primo giro senza PostgreSQL aveva 72 ignorati su 103: non conta come verifica.

### T126 — verifica reale in sola lettura
Router `http://192.168.253.18:8080`, `GET /v1/models`: `Qwen3.6-35B-Smart` = `sleeping` (tutti gli altri `unloaded`). Con il vero `ModelRuntimeMonitor` e il vero `InternalModelClient` (harness fuori dal repository, contesto 131072, nessuna modifica al router):
- `GetSnapshotAsync`: `Status=Unverified`, `Readiness=ModelUnloaded`, `ModelStatus=sleeping`, 192 ms; finestra non dedotta (nessuna sonda `/props`).
- `WaitUntilReadyAsync` con residuo 8 s: `ModelUnloaded`, `DeadlineReached=true` dopo 8,0 s (rispetta il residuo della deadline originale).
- `WaitUntilReadyAsync` con residuo 60 s: `ModelUnloaded`, `DeadlineReached=false` dopo 7,3 s (il giro di backoff ha tetto fisso, non attende oltre).
- Richieste HTTP complessive: **5 × `GET /v1/models`**; nessun `POST`, nessuna inferenza, nessun `/props?model=` né `/slots?model=` (che in router mode possono avviare il caricamento). Il modello è rimasto `sleeping` durante la prova.
Esito T126: **VERIFIED per gli stati `sleeping`/`unloaded` (deterministici + reale)**. Non verificati dal vero: `loading`, `unreachable`, modello assente, finestra diversa (coperti solo dai test deterministici). Il caso «caricamento che termina entro la deadline» non è provabile in sicurezza senza richiesta di inferenza. T126 resta NON spuntato in `tasks.md`: la sua riga lega la chiusura alla prova a modello scaricato con motivi e tempi registrati; l'autore decida se questa evidenza basta.

### N4 — una prova reale (Qwen3.6-35B-Smart, W=131072, reasoning OFF, output 16384, margine 1024)
Test: `DocumentSmallFlowMafRealTests.N4_…` (filtro verificato: 1 test). Durata 1 m 13 s. **FAIL: 36 PASS / 5 FAIL.** Sei chiamate al modello, tutte `finish=stop` (nessun troncamento): 2 estrazioni, 1 chat, 2 analisi, 1 bozza.
Catture complete per chiamata (richiesta, risposta, esito) dall'harness già esistente, solo locali in `diagnostics-local/I4-N4/` (non versionate).

I 5 FAIL hanno **due cause diverse**:
1. **Falso negativo del test (difetto di test, corretto).** `analisi: revisione 1.1.0 (workflow documentale v3 con alias)` confrontava `WorkflowDefinitionVersion` con `DefinitionVersionV3`, ma dal T131 (157c1fe) le nuove richieste congelano `project-analysis-documents-v4` (`SourceSnapshotStore.AnalysisDefinition`); `DocumentSmallFlowTests.cs:188` asserisce già V4 sullo stesso percorso ed è PASS. Prima: FAIL con run v4 valido. Dopo: attesa V4 (stesso criterio funzionale: schema 1.1.0 e definizione alias congelata). Vale per tutti i nominali N1–N5, non solo N4. Non ho rieseguito il DGX per provarlo: il «dopo» è la verità già verde del test offline.
2. **Conflitto perso nel retry T130 (4 FAIL: conflitti=0, alternative 30/45, stato Open).**
   - Chiamata 005 (prima analisi, 3107 token prompt): **semanticamente corretta**. `Declared` 45 giorni (E1) e `Documented` 30 giorni (E3) come alternative di un conflitto `c1`, nessuna scelta automatica. Scartata dal validatore perché l'evidenza **E2** non è citata da nessun elemento (`evidence_not_mapped`). E2 è un'evidenza `Gap` derivata dalla domanda del PM («Non è noto cosa dica la scheda A…»).
   - Chiamata 006 (unico retry guidato T130, 3225 token): il messaggio ripete il primo e aggiunge `CORREZIONE… queste evidenze non sono citate: [E2]… Produci di nuovo l'analisi COMPLETA… sia quelle già citate nella risposta precedente…`. Il modello ha **rigenerato da zero** e ha prodotto `conflicts: []`: 30 e 45 come voci separate (`Documented`/`Declared`) più una `openQuestion` `Inferred` che cita E1, E2, E3. Valido per il validatore, copertura completa, provenienza 100% corretta, ma conflitto assente.
   - Il retry T130 funziona come da contratto (AI §10): un solo retry, messaggio con alias E2, nessun merge, nessuna chiamata oltre. Conferma reale, per la prima volta, di un retry T130 con la telemetria delle catture (prima «NOT VERIFIED»).
   - Causa effettiva del fallimento: **non** l'incapacità del modello di riconoscere il conflitto, **ma** la rigenerazione completa del retry, che non mostra al modello la risposta precedente: il secondo output cambia struttura. Variabilità di rigenerazione + messaggio di correzione che non chiede di preservare i conflitti già dichiarati.
   - La bozza (007) è stata prodotta e superata (30 e 45 presenti, 2 marcatori validi): quindi anche la parte a valle non è da imputare.

### Perimetro: perché mi fermo
Rimediare al punto 2 richiederebbe modificare il contratto del retry T130 (AI §10): per esempio includere nel retry la risposta precedente o chiedere esplicitamente di preservare i conflitti, o trattare E2 diversamente. Il mandato vieta nuovi retry, merge server-side e modifiche arbitrarie ai contratti e chiede di fermarsi sulla decisione semantica. Nessun prompt, contratto, validatore, gold o asserzione semantica è stato toccato. Nessuna seconda prova N4 e nessuna prova N1/N2/N3/N5.

### Classificazione
- **Funzionante e verificato**: preflight offline 251/251; T126 su stati `sleeping`/`unloaded` reale; chat documentale, estrazione, alias chat, classificazione Declared/Documented, citazioni 100%, conferma, bozza; retry T130 singolo reale.
- **Implementato ma non verificato**: N1, N2, N3, N5 e il browser reale a v4 (mai eseguiti dopo T131/T133); T126 per `loading`/`unreachable` dal vero.
- **Fallito**: N4 (conflitto doc-PM non dichiarato dopo il retry).
- **Bloccato per limiti del modello**: variabilità della rigenerazione dell'analisi (stesso input, stessa risposta valida solo al primo tentativo).
- **Bloccato per decisione funzionale/architetturale**: contenuto del messaggio di retry T130 (preservare i conflitti) e trattamento delle evidenze `Gap` da domanda del PM.
