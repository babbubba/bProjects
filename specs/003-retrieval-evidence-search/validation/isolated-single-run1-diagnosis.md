# Diagnosi offline della prova DGX isolata Single (2026-11-13)

Stato: **diagnosi OFFLINE**; nessuna inferenza, nessuna modifica a contratti, schemi, prompt, validatori o workflow di produzione, nessuna nuova definizione di workflow. Classificazione decisa dal committente: **difetto di conformità dell'output dello stadio Gap**. Multi e M4 non avviati; T040 resta APERTO e sospeso. La prova isolata **non equivale a T040** (gerarchia simulata, ledger controllato).

Riferimenti: esito della prova in `M3-real.md` §«Esito della prova DGX isolata Single»; artefatti in `isolated-single-run1/`; test della diagnosi `IsolatedSingleRunDiagnosticTests` (7 test, offline).

## 1. Risposta originale conservata

| File (`isolated-single-run1/`) | SHA-256 |
|---|---|
| `stage-response.json` (risposta del modello, identica a `002-assistant.txt` dell'archivio MafDiagnostics) | `760738b4342afe2e6358dc2df77d008241ceb84dff804cbf28ed74f41bf5c2c7` |
| `stage-request.json` (corpo della richiesta dello stadio, senza intestazioni; l'originale `002-request.json` ha `2db0997c…ab9f`) | `99d4cae20d58ceb65a6e87f0228aa315a23faafae2eea3ce60d49de96df7b108` |
| `stage-result.json` | `c522c93e841c646a54297194977ed33b5b1f9c3613afab6e606f512f7f6ff20e` |
| `report.json` (rapporto del test) | `fde35dccb8ee5af04c4f2f94e8da9eb621d2e2256ef0016363b40cb2bd8d6b80` |

Un test (`The_original_response_and_request_are_preserved_with_their_hashes`) fissa i primi due hash: qualunque modifica li fa fallire. Le copie diagnostiche dei punti seguenti vivono solo in memoria.

## 2. Il rifiuto dipende ESATTAMENTE dall'assenza dei due riferimenti Open di E5

**Metodo.** Percorso del validatore di produzione dello stadio (`GapStageContracts.Validate`): decodifica strict (`GapStageContracts.Decode`, che applica già le regole di forma) e poi `GapAssessmentRules.Check` sull'input assegnato. Gli input del validatore sono **ricostruiti dalla richiesta realmente inviata**: alias→tipo delle 112 voci offerte (5 Gap + 107 candidati), chiavi delle domande aperte `i1…i5`, nessun conflitto («Conflitti aperti canonici: (nessuno)»).

**Limite dichiarato.** Il catalogo completo del run (283 alias, `AnalysisAliasCatalog`) e il database di prova non sono ricostruibili (il database è stato eliminato). `Validate` richiama `Decode`, `Check` con `Assigned(catalog)` e `MissingGaps`; qui `Assigned` è limitato alle 112 voci offerte, che contengono tutti gli alias citati dalla risposta. Per questi record il risultato è equivalente; **non** è un'esecuzione di `Validate` con il catalogo originale e non lo simula.

**Risultati (test, tutti superati).**
- Risposta originale ⇒ rifiutata alla decodifica con esattamente una violazione: «gapAssessments non valide: gapAssessments[E5]: Open richiede esattamente uno fra openItem e openConflict.» (stesso messaggio del run). Le altre quattro valutazioni (E2, E3, E4, E6) non producono violazioni.
- E5 ha `openItem: null` e `openConflict: null`; la regola (`(openItem is null) == (openConflict is null)` per un esito `Open`) è l'unica causa.

**Copia SOLO diagnostica, mai evidenza PASS** (E5 `openItem: "i3"`, il riferimento offerto dal messaggio `[i3] (E5)`): stessa decodifica e stesso `Check` ⇒ **0 violazioni** e nessun Gap non valutato (`MissingGaps` vuoto). Varianti sulla stessa copia, sempre nel solo test: `openConflict: "c1"` ⇒ violazione perché nessun conflitto era offerto; entrambi valorizzati ⇒ «esattamente uno»; `openItem: "i9"` ⇒ domanda non offerta. Conclusione: **l'unico motivo del rifiuto è l'assenza di entrambi i riferimenti**; con il riferimento offerto le altre regole sono soddisfatte.

## 3. Coerenza E3 / E135 / E1

Contenuto delle evidenze nel messaggio inviato:
- **E1** (documento d'estensione parziale): «L'appaltatore deve erogare la formazione degli operatori del cliente in tre giornate presso la sede del cliente.» — è l'unica voce del pacchetto con «tre giornate»; non contiene un costo.
- **E135** (`capitolato-aurora.txt`): «Le segnalazioni di guasto con priorità alta sono prese in carico entro 40 minuti, esclusi i giorni festivi.» — nulla a che vedere con la formazione.

Risposta E3: `supportingEvidence: ["E1"]` (corretto: giustifica la durata) e motivazione «L'evidenza **E135** fornisce la durata della formazione (tre giornate), ma non vi è alcuna evidenza che specifichi il costo». **Il supporto strutturato è giustificato dal contenuto; l'alias nel testo libero della motivazione è sbagliato** (E135 invece di E1). Il validatore non controlla gli alias nel testo libero (solo `supportingEvidence`), quindi l'errore passerebbe inosservato: è un difetto di qualità della motivazione, non di conformità strutturale, e non è stato corretto. Anche la valutazione «assenza del costo» è coerente con E1.

Altre motivazioni lette sul contenuto: E2 → E13 «84 mesi» (giusto); E5 → E255 «60 giorni data fattura» e E43 «30 giorni dalla data di ricevimento» (i due lati; le basi di decorrenza differiscono, la contrapposizione resta ragionevole); E4 e E6 senza supporti, coerenti con l'assenza nel pacchetto.

## 4. Che cosa la prova ha già dimostrato (separato, NON promosso a PASS end-to-end)

Fatti osservati con servizi reali nel solo scenario Single, su ledger controllato e gerarchia simulata:

| Elemento | Evidenza | Limite |
|---|---|---|
| Indicizzazione | 4 documenti, 126 blocchi, 35 richieste di embedding reali in 5,4 s (indice 8,6 s); tutte le preparazioni `Ready` (il test lo asserisce prima di proseguire) | stato `RetrievalIndexStates` non riesaminabile dopo il run |
| Ricerca ibrida | 5 richieste di embedding di query (0,7 s), ricerca 0,9 s totali | un solo scenario, 5 query |
| Riordino | 7 richieste al riordino reale (4,7 s); fase 2,7 s | idem |
| Composizione del pacchetto | messaggio con 107 candidati, sorgente `retrieval`, `retrievalComplete: false` (limitato dal budget) | registro persistito non ispezionato (il run non è `Completed`, verifica saltata, DB eliminato) |
| Contenuto del pacchetto | contiene «84 mesi» (E13), «tre giornate» (E1) e **entrambi i lati** del conflitto (E255 «60 giorni», E43 «30 giorni»); nessuna traccia del documento escluso né di «cinquantamila» nel messaggio (verificato dal test sulla richiesta reale) | non è una misura di recall su un gold esteso |
| Budget | 14.077 token di prompt contro 15.360 (`finish=stop`, 515 token in uscita) | |
| Invocazione dello stadio | UNA richiesta reale (14,6 s) tramite il client e lo schema di produzione; routing fail-closed senza violazioni; la risposta ha valutato tutti e 5 i Gap | rifiutata dal validatore: nessun WorkOutput dello stadio |

**Non dimostrato:** registro persistito con provenienza e parametri; replay e checkpoint con servizi reali; scenario Multi; falsi `AnsweredInSnapshot` e citazioni **sul run committato** (il run è `Failed`: NotVerified); conservazione dei conflitti `Open` (la gerarchia simulata non li offre: NotVerified); estrazione; espansione v10.

## 5. Costo delle richieste non generative al DGX (1.475 `/tokenize`, 883 `/apply-template`)

Dati dalla sessione diagnostica (2.379 eventi HTTP della prova) e dal rapporto del router.

| Percorso | Richieste | Tempo cumulato | Media | p50 | p95 | Max |
|---|---|---|---|---|---|---|
| `/tokenize` | 1.475 | 230,9 s | 157 ms | 151 ms | 204 ms | 1.165 ms |
| `/apply-template` | 883 | 140,1 s | 159 ms | 152 ms | 199 ms | 1.215 ms |
| `/props`, `/slots`, `/v1/models` | 7 + 7 + 7 | 2,4 s | 58–139 ms | | | |

- Finestra continua da 423 s: 371 s di richieste (**duty 88%**), **strettamente sequenziali** (una sola pausa oltre 2 s).
- **Latenza piatta rispetto alla dimensione del testo:** media 151 ms sulle prime 100 richieste, 156 ms a metà, 142 ms sulle ultime 100 (testi molto diversi). Il costo è dominato dal **numero di richieste**, non dai token.
- Per fase (rapporto del router): prima della gerarchia 865 + 577 = 1.442 richieste (243 s, 66% del tempo non generativo); gerarchia simulata 48 + 24 = 72 (11 s); recupero e misura dei costi 562 + 282 = 844 (122 s, 33%). Lo stadio e il riscaldamento non misurano.

**Correzione del Run 5.** L'osservazione «2.414 `/tokenize`, ≈ 0,32 s ciascuna» contava solo `/tokenize`. Nella stessa finestra (10:00:50–10:13:42, 772 s) gli eventi del Run 5 contengono **1.843 `/apply-template` e 2.414 `/tokenize`: 4.257 richieste, 656 s (85% della finestra), ≈ 154 ms ciascuna**. L'anomalia dei ~13 minuti è quindi interamente spiegata da questo traffico sequenziale.

### Hotspot (dal codice e dai conteggi)

Ogni misura di payload (`PayloadPreflight.MeasureAsync` → `CountAsync`) esegue: 1 `/apply-template` del messaggio completo, 1 `/tokenize` del testo reso e 1 `/tokenize` per ciascun elemento extra (lo **schema di risposta**).

- **H1 — misura incrementale del Pack (`DocumentBatchPacking.PackNextAsync`)**: aggiunge un membro alla volta e **rimisura l'intero batch**: n misure per batch (n+1 con la prima candidata scartata), ciascuna con testo crescente. Le 577 misure prima della gerarchia ≈ 2 viste × 283 voci + qualche batch. Pesa 1.442 richieste / 243 s.
- **H2 — costo per voce nel recupero (`DocumentReconcilePlan.ExecuteRetrievalAsync`)**: una misura del messaggio dello stadio per **ciascuna** delle ~280 voci (apply-template + tokenize + tokenize dello schema, 3 richieste per voce). Pesa 844 richieste / 122 s.
- **H3 — testo costante ritokenizzato:** i rapporti `/tokenize` : `/apply-template` sono 1,50 (prima della gerarchia), 2,0 (gerarchia), 1,99 (recupero): circa 288 + 24 + 281 = **593 `/tokenize` (40%) contano lo schema di risposta**, che ha al più 7 valori distinti (6 varianti della gerarchia + lo schema dello stadio). Circa 586 richieste (≈ 92 s) sono quindi ridondanti. *Inferenza dal codice e dai rapporti: i corpi delle richieste di tokenizzazione non sono registrati.*
- **H4 — sequenzialità:** le misure indipendenti del recupero (H2) non hanno concorrenza.
- Non è un hotspot: `ResolveProfileAsync` e le capacità (7 richieste per percorso in tutto il run, già in cache).

### Ottimizzazioni proposte (NON implementate), con misura di successo

Il lavoro appartiene alla pianificazione della gerarchia (002) e alla misura dei costi del recupero (003); ogni proposta richiede una decisione separata e una garanzia di equivalenza.

| # | Proposta | Effetto atteso | Metrica e controllo di equivalenza |
|---|---|---|---|
| P1 | Memoizzare i token di testi costanti (schema, istruzioni) per impronta del modello e SHA-256 del testo | −≈590 `/tokenize` (−40% dei `/tokenize`, ≈ −90 s) | richieste `/tokenize` per run; `PayloadPreflight.Tokens` identici su tutte le misure (golden) |
| P2 | Ricerca del confine del batch per **galoppo/bisezione** invece della scansione lineare (richiede monotonia dei token rispetto ai membri, da verificare) | per batch da ~n+1 a ~2·log₂(n)+1 misure (n≈140: 141 → ≈16); prima della gerarchia 577 → ≈ 60 misure (−90%) | richieste prima della prima chiamata generativa; **stessi batch** (membri e hash di input, es. il piano del Run 5 138/143/1) |
| P3 | Costo per voce con **una sola** tokenizzazione del testo della voce (più una misura a vuoto), invece di 3 richieste per voce; la riserva (`slack`) già presente assorbe l'errore ai confini | 844 → ≈ 290 richieste nel recupero | differenza per voce rispetto alla misura completa entro la tolleranza dichiarata; **stesso pacchetto** sul ledger Single |
| P4 | Concorrenza limitata (6–8) per le misure indipendenti del recupero | tempo ÷ ~6 a parità di richieste | durata della fase «recupero e misura» (122 s → < 25 s) |

Stima complessiva (P1–P4, ordini di grandezza): richieste non generative 2.358 → ≈ 330 (−86%), tempo 371 s → ≈ 50 s. Protocollo di misura: contatori del router isolato (già presenti: richieste per percorso e fase), golden dei piani dei batch del Run 5 e del pacchetto Single, confronto a tokenizzatore finto deterministico prima di qualunque prova reale.

## 6. Valutazione tecnica: la parte retrieval della M3 è verificata indipendentemente?

**Verificata indipendentemente in modo PARZIALE e PRELIMINARE; non è un'accettazione.**

A favore, con servizi reali e senza gerarchia reale né estrazione: l'indice reale si costruisce senza fallimenti; la ricerca ibrida e il riordino reali producono, per cinque domande annotate prima della prova, un pacchetto che contiene tutte le evidenze attese (risposta presente, parziale, **entrambi i lati del conflitto**) e **nessuna** evidenza del documento escluso; il pacchetto resta nel budget (14.077 ≤ 15.360 token); i tempi del recupero sono ordini di secondi (pipeline 5,9 s, riordino 2,7 s per 5 query).

Contro, e quindi non sufficienti: un solo scenario (Single) e cinque domande, quindi nessuna misura di recall oltre il campione; ledger idealizzato, non estratto; registro persistito non ispezionato dopo il run (provenienza, hash, parametri solo nel messaggio e nel rapporto); replay e checkpoint con servizi reali non eseguiti; Multi non eseguito; il pacchetto è troncato dal budget (`retrievalComplete: false`), il che è progettuale ma significa che l'«assenza» non è provata (corretto per FR-018/019).

**Distinzioni.**
- *Retrieval M3*: i comportamenti di indicizzazione, ricerca, riordino e composizione del pacchetto risultano coerenti con i criteri funzionali verificabili in isolamento; manca la verifica del registro persistito e di Multi per poterli dichiarare «verificati indipendentemente».
- *Stadio Gap*: **non accettato**: l'unica risposta reale è stata rifiutata dal validatore (difetto di conformità E5, più un alias sbagliato nel testo libero di E3). La risposta indica un comportamento plausibile sui contenuti (esiti attesi su E2, E3, E4, E6 ed E5 come ambiguità), ma nessun criterio dello stadio è PASS.
- *T040*: **invariato, aperto e sospeso**; nessun criterio T040 è superato.

**Passo successivo proposto (non autorizzato):** completare la verifica della sola parte retrieval **senza alcuna richiesta generativa** — embedding e riordino reali (non generativi) su Single e Multi con ledger controllato, ispezione del registro persistito (provenienza, hash, parametri), replay e checkpoint — con lo stesso harness e un router che vieti ogni chat. Richiede una autorizzazione separata.
