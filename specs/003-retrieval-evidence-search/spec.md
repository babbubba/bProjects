# Feature Specification: Recupero di evidenze documentali con ricerca ibrida e provenienza verificabile

**Feature Branch**: `feature/003-retrieval-evidence-search`
**Feature**: `003-retrieval-evidence-search`
**Created**: 2026-11-09
**Status**: Draft — specifica funzionale con le cinque decisioni aperte risolte dal committente il 2026-11-09; piano e task in `plan.md`/`tasks.md`; nessuna implementazione. Nessun requisito è accettato
**Input**: Terza vertical slice: sostituire la sorgente dei candidati dello stadio Gap della 002 (oggi l'intero ledger) con un recupero di evidenze su passaggi originali, ibrido lessicale + semantico con riordino, quando il ledger supera il budget; mantenere provenienza, determinismo e distinzione fra «non recuperato» e «assente».

**Fonti di questa specifica** (nessuna riscritta): [transition §F](../../docs/roadmap/slice-002-003-transition.md) (perimetro, C1–C7, scartati, confine unico); [ai-workflow.md §13.7–13.8](../002-project-documents/contracts/ai-workflow.md) (contratto dello stadio Gap); [copertura implementativa](../../docs/roadmap/implementation-coverage.md); esperimenti del branch `research/slice-003-lab` (commit `f005c8d`, in sola lettura): `report-finale.md`, `phase-2/report.md`, `research-preliminare.md`, `results/*`. Le misure citate sono **evidenze sperimentali su dati sintetici, non requisiti né accettazioni**.

## Clarifications

### Session 2026-11-09

Risposte ricavate dai documenti esistenti e dalle decisioni già prese, non nuove decisioni del committente. Le questioni realmente aperte stanno in «Decisioni aperte» in fondo.

- Q: Quale parte della 002 la 003 sostituisce? → A: Solo la **sorgente dei candidati** dello stadio Gap; DTO, prompt, validatore, assembly, coverage, checkpoint e fail-fast restano quelli della 002 (§13.7: «la 003 potrà sostituire solo la sorgente dei candidati»).
- Q: Cosa conteggia come «caso d'uso» della 003? → A: Il ramo «ledger > budget» dello stadio Gap (limite residuo della 002: oggi `gap_ledger_exceeds_input`). La Chat e la bozza non cambiano; la ricerca come funzione per il PM è fuori perimetro.
- Q: Si sceglie il motore d'indice? → A: No (D7). Baseline da valutare nel piano: PostgreSQL con ricerca lessicale italiana + estensione vettoriale; un motore dedicato (Qdrant) solo se una misura sul corpus bProjects lo giustifica.
- Q: Si adottano i sommari SKRAG, la selezione preventiva o l'NLI come filtro? → A: No: scartati dalle evidenze (transition §F; phase-2 B2/B3, report NLI).
- Q: Il recupero può dichiarare che una risposta non esiste? → A: No: un recupero senza risultati pertinenti non è una prova di assenza; `retrievalComplete` non si converte mai in «assente» (vedi 002 §13.7, tabella a cinque stati).
- Q: La valutazione continua (C7) è un task applicativo che ricrea i benchmark? → A: No: gold e script del laboratorio sono promossi a test; il laboratorio resta riferimento storico invariato.

### Session 2026-11-09 (decisioni del committente sulle cinque decisioni aperte)

Decisioni **approvate dal committente** il 2026-11-09; sostituiscono le raccomandazioni precedenti dove diverse.

- Q: Soglie di accettazione numeriche (D-1)? → A: Recall@10 ≥ 95%, Hit@1 con riordino ≥ 95%, 0 falsi `AnsweredInSnapshot` e regressione ≤ 2 punti si applicano al **gold sintetico CONGELATO**; le regressioni si misurano rispetto alla **baseline dichiarata sullo STESSO insieme di query** (stessi gold, stessi input per hash). Nessuna soglia sul corpus cliente finché non esiste una prima misura.
- Q: Corpus e autori per la valutazione indipendente (D-2)? → A: L'**accettazione piena** richiede un corpus anonimizzato e almeno 20 query scritte da un secondo autore. Altrimenti l'esito è «accettata con riserva», che DEVE evidenziare esplicitamente che manca la validazione su corpus anonimizzato e su query indipendenti e **NON equivale all'accettazione piena**.
- Q: Servizi di ricerca non disponibili (D-3, **modificata** dal committente)? → A: **Embedding indisponibile ⇒ errore tipizzato, zero invii LLM, nessun fallback silenzioso.** **Riordino indisponibile ⇒ consentito un ranking RRF DIAGNOSTICO** con `retrievalComplete = false`, che NON può dichiarare completata una valutazione Gap. In modalità degradata NON si emettono risultati definitivi `AnsweredInSnapshot`, dichiarazioni di assenza di evidenze né attestazioni di completezza dei conflitti; il workflow rende esplicito il mancato completamento e conserva la causa tipizzata; nessun risultato parziale è presentato come valido; nessun aumento automatico di budget né cambio di modello per aggirare il problema. Sostituisce la raccomandazione «ordine RRF con avviso».
- Q: Ambito della ricerca (D-4)? → A: La 003 serve **solo** il recupero per la valutazione Gap (Chat, bozza e ricerca per il PM sono fuori perimetro); i componenti restano riutilizzabili per evoluzioni future **senza interfacce speculative con una sola implementazione** (YAGNI).
- Q: Pinning dei modelli (D-5)? → A: Modelli fissati per **nome, artefatto e dimensione dei vettori** nella readiness; SHA-256 dei pesi registrato al provisioning; fino ad allora «non verificato» nei rapporti.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Valutare i Gap su corpus oltre il budget del ledger (Priority: P1)

Come PM che carica molti documenti voglio che l'analisi valuti comunque le mie domande aperte (Gap) trovando i riscontri pertinenti anche quando l'intero ledger delle evidenze non entra più nella chiamata di valutazione, senza che il sistema dichiari «assente» ciò che semplicemente non ha cercato bene.

**Why this priority**: È l'unico limite residuo della 002 (oggi il run fallisce con `gap_ledger_exceeds_input`) e il solo punto in cui la 003 produce valore per l'utente senza cambiare altro.
**Independent Test**: Un progetto sintetico con ledger oltre il budget dello stadio Gap e Gap annotati (risposta presente, parziale, assente, in conflitto, in documento escluso) percorre Analizza → coverage; ogni Gap ha esito, supporti verificabili e stato di ricerca coerente con i gold.

**Acceptance Scenarios**:

1. **Given** un ledger oltre il budget di input dello stadio Gap e almeno un Gap, **When** il PM richiede Analizza, **Then** il run non fallisce per `gap_ledger_exceeds_input`: i candidati sono prodotti dal recupero, lo stadio valuta ciascun Gap e la coverage mostra `searchBasis = retrieval`. (FR-001–FR-003, FR-024)
2. **Given** un Gap la cui risposta documentale è presente in un passaggio di un documento incluso, **When** il recupero lo include fra i candidati, **Then** l'esito è `AnsweredInSnapshot` con supporti validati sui passaggi originali, mai sui riferimenti del Gap. (FR-014–FR-016)
3. **Given** un Gap senza alcun candidato pertinente recuperato, **When** lo stadio termina, **Then** l'esito non è presentato come assenza provata: la coverage distingue «assenza non provata (ricerca incompleta o non esaustiva)» da «assenza provata» e riporta `retrievalComplete`. (FR-017–FR-019)
4. **Given** riscontri in conflitto (valori diversi nello stesso ambito, oppure documento e dichiarazione PM), **When** il recupero e la valutazione terminano, **Then** entrambi i lati sono nei candidati e il conflitto resta aperto, senza arbitraggio. (FR-006, FR-016)
5. **Given** una versione di documento esclusa dal PM o un altro progetto, **When** il recupero viene eseguito, **Then** nessun passaggio di quella versione o di quel progetto compare fra i candidati: l'esclusione precede il ranking. (FR-005)
6. **Given** un Gap valutabile anche in modo esaustivo (ledger entro budget), **When** la 003 è attiva, **Then** la 002 si comporta come prima (`ledger_exhaustive`, `retrievalComplete = true`); il recupero si usa solo dove la 002 fallirebbe per budget. (FR-002)

---

### User Story 2 - Ricostruire e ispezionare perché un passaggio è stato (non) recuperato (Priority: P1)

Come PM (e come revisore tecnico) voglio poter vedere, per ogni Gap, quali passaggi sono stati candidati, in che ordine, con quale provenienza e quali sono stati scartati per budget o per errore, così da poter giudicare l'affidabilità dell'esito.

**Why this priority**: Senza registro dei candidati le perdite di ranking e di confezionamento sono indistinguibili (S06, M03/M05 nel laboratorio) e «non trovato» non è verificabile.
**Independent Test**: Dato un run concluso, il registro dei candidati è consultabile e congelato; riesecuzioni con lo stesso snapshot e gli stessi indici producono lo stesso registro o una differenza spiegata.

**Acceptance Scenarios**:

1. **Given** un run concluso, **When** il PM consulta la coverage di un Gap, **Then** vede l'elenco ordinato dei candidati con documento, versione, sezione, posizione nell'originale e rango, e l'indicazione di quali sono entrati nel contesto della valutazione. (FR-010–FR-013)
2. **Given** candidati oltre il budget di confezionamento, **When** il contesto viene composto, **Then** gli esclusi sono elencati per identificativo con il motivo, non omessi silenziosamente né troncati a metà passaggio. (FR-012)
3. **Given** un errore o l'indisponibilità di un servizio di ricerca durante il recupero, **When** il run prosegue o termina, **Then** l'evento è registrato; l'esito del Gap non appare mai come completo se il recupero non lo era. (FR-020, FR-021)
4. **Given** un indice cancellato o corrotto, **When** viene ricostruito dalle sole evidenze e dai blocchi originali persistiti, **Then** i passaggi indicizzati sono identici e nessun dato autorevole è andato perso. (FR-004, FR-007)

---

### User Story 3 - Misurare continuamente la qualità del recupero (Priority: P2)

Come responsabile tecnico voglio una valutazione ripetibile (gold, query, metriche) che segnali regressioni del recupero prima che arrivino ai PM, e che dichiari i propri limiti (dati sintetici, un solo autore delle query).

**Why this priority**: Le evidenze di oggi sono di laboratorio; senza valutazione continua non si può promuovere né mantenere la capacità. È P2 perché non è di per sé un percorso PM.
**Independent Test**: Il comando di valutazione, con endpoint disponibili o simulati, produce per ogni query Recall@k, posizione del primo riscontro, falsi `AnsweredInSnapshot` e conflitti persi, e fallisce se scendono sotto le soglie concordate.

**Acceptance Scenarios**:

1. **Given** il gold e le query promossi come test, **When** la valutazione viene eseguita, **Then** riporta Recall@1/5/10/20, Hit@1, falsi `AnsweredInSnapshot` e conflitti persi, per corpus e per configurazione. (FR-026–FR-028)
2. **Given** una modifica a indice, modello, parametri o parser, **When** la valutazione viene ripetuta, **Then** è confrontabile con la precedente (stessi gold, stessi input per hash) e una regressione oltre soglia blocca l'accettazione. (FR-027, FR-029)
3. **Given** metriche calcolate su dati sintetici o su query di un solo autore, **When** vengono riportate, **Then** il rapporto lo dichiara; non sono presentate come prova di completezza generale. (FR-030)

---

### User Story 4 - Confermare la separazione fra recupero e valutazione senza regressioni della 002 (Priority: P2)

Come responsabile della qualità voglio che l'introduzione del recupero non alteri i run, gli snapshot, i prompt, i contratti e le definizioni storiche della 002 e che il confine fra recupero e valutazione sia unico e verificabile.

**Why this priority**: Protegge il lavoro approvato; è condizione per poter spedire la 003 senza riaprire la 002.
**Independent Test**: Suite esistente della 002 invariata e verde; run storici riletti byte per byte; un test dimostra che lo stadio Gap riceve lo stesso contratto d'ingresso con entrambe le sorgenti.

**Acceptance Scenarios**:

1. **Given** i run e gli snapshot esistenti, **When** il sistema 003 li legge, **Then** gli algoritmi, i messaggi, le chiavi di freeze e i report v2–v6 sono invariati. (FR-031)
2. **Given** le due sorgenti di candidati (ledger intero, recupero), **When** lo stadio Gap è invocato, **Then** il suo contratto d'ingresso è lo stesso e nessuna interfaccia con una sola implementazione è introdotta. (FR-002, FR-032)

---

### Edge Cases

- Ledger oltre il budget **e** nessun Gap: nessun recupero (percorso 002 invariato, gerarchia v4-equivalente).
- Query del Gap molto breve, ambigua o contenente numeri: il recupero resta disponibile; la qualità è misurata, non assunta.
- Passaggio che spezza una clausola da condizioni/eccezioni: il contesto padre selettivo deve poter ricomporre la clausola entro il budget; altrimenti la perdita è registrata.
- Documento in PDF o in altro formato non provato: nessuna garanzia di qualità; l'esclusione dal perimetro di accettazione è dichiarata.
- Più progetti con corpora simili nello stesso database: il confinamento per progetto è obbligatorio e va provato (oggi non provato nel laboratorio).
- Servizio di embedding non disponibile, lento o con risposta malformata: errore tipizzato a zero invii LLM (FR-020). Riordino non disponibile: ranking RRF diagnostico con `retrievalComplete = false`, nessuna valutazione Gap completata e nessun esito definitivo (FR-035); nessun esito completo con candidati parziali.
- Modello di embedding o parametri cambiati: gli indici derivati sono invalidati e ricostruiti; i run storici conservano il proprio registro.
- Punteggi del riordino negativi o non calibrati: usati solo come ordinamento, mai come probabilità né come soglia di verità.
- Ledger che cresce dopo il run: il registro dei candidati è riferito allo snapshot congelato, non al ledger corrente.
- Documenti molto grandi (milioni di caratteri): tempi di indicizzazione e costo a freddo noti e dichiarati; non è garantita la prontezza immediata dopo il caricamento.

## Requirements *(mandatory)*

### Functional Requirements

**Recupero e candidati**

- **FR-001**: Il sistema DEVE produrre, per uno stadio Gap che non può ricevere l'intero ledger, una lista ordinata di evidenze candidate ottenuta da recupero sui passaggi originali dello snapshot congelato.
- **FR-002**: Il recupero DEVE essere usato **solo** quando il ledger non entra nel budget dello stadio Gap; quando il ledger entra, il comportamento resta quello della 002 (sorgente esaustiva) e il recupero non interviene.
- **FR-003**: Il contratto d'ingresso dello stadio Gap DEVE restare «lista ordinata di candidati + `searchBasis` + `retrievalComplete`» più i Gap e i riferimenti Open canonici; la 003 DEVE valorizzare `searchBasis = retrieval` e calcolare `retrievalComplete`.
- **FR-004**: L'unità di ricerca DEVE essere il passaggio figlio originale (blocco o evidenza con intervallo nell'originale), non un chunk aggregato; ogni candidato DEVE essere riconducibile a documento, versione, sezione (se esiste), intervallo e hash dell'originale.
- **FR-005**: I filtri di progetto, di versione inclusa e di esclusione scelta dal PM DEVONO essere applicati **prima** del ranking; nessun passaggio escluso o di altro progetto può comparire fra i candidati.
- **FR-006**: Il recupero DEVE trattare ogni Gap con la propria interrogazione e DEVE poter restituire più riscontri (anche in conflitto) per la stessa domanda; un solo lato di un conflitto non soddisfa il recupero.
- **FR-007**: Gli indici di ricerca DEVONO essere derivati, ricostruibili dai soli dati autorevoli (originali, versioni, blocchi, evidenze) e invalidabili al cambio di modello, parametri o parser; non DEVONO essere fonte di verità.
- **FR-008**: Il ranking DEVE combinare almeno un segnale lessicale (con italiano) e uno semantico multilingue con fusione per rango; entrambi DEVONO operare sugli stessi passaggi figli.
- **FR-009**: Un riordino dei soli passaggi figli candidati DEVE poter essere applicato; i suoi punteggi DEVONO essere usati solo per l'ordinamento e non DEVONO essere trattati come probabilità né come soglia di assenza.

**Registro dei candidati e confezionamento**

- **FR-010**: Il registro dei candidati (ordine, ranghi per segnale, rango finale, cutoff applicato, errori) DEVE essere congelato nel run e consultabile; DEVE essere sufficiente a riprodurre l'input dello stadio.
- **FR-011**: Il contesto di un candidato DEVE poter essere esteso al padre (titolo/sezione/chunk contenitore) in modo **selettivo**, con budget esplicito di caratteri/token; l'espansione non è sempre attiva.
- **FR-012**: Il confezionamento DEVE usare passaggi interi; gli esclusi per budget DEVONO essere registrati per identificativo con motivo; nessun troncamento a metà passaggio.
- **FR-013**: Il registro e la coverage DEVONO riportare per ogni candidato documento, versione, sezione, intervallo e rango, senza esporre al modello UUID, hash o offset tecnici (formato VOCE della 002).

**Valutazione, citazioni e assenza**

- **FR-014**: Ogni identificativo e citazione prodotti dal modello DEVONO essere validati deterministicamente contro i candidati effettivamente forniti; identificativi non risolvibili producono errore tipizzato, non correzioni silenziose.
- **FR-015**: Una citazione DEVE corrispondere letteralmente a un passaggio candidato; un identificativo composto da più parti (per es. nome file e id) DEVE essere risolto in modo deterministico solo quando la parte file coincide con quella dell'id.
- **FR-016**: Conflitti fra riscontri DEVONO essere preservati e mai arbitrati dal sistema; l'esito corrisponde alla matrice di §13.2 della 002.
- **FR-017**: `retrievalComplete` DEVE valere `false` se il recupero ha avuto errori, cutoff che escludono candidati non valutati, esclusioni per budget di candidati ritenuti pertinenti dal ranking, o servizi non disponibili.
- **FR-018**: Un Gap senza riscontri e con `retrievalComplete = false` o `searchBasis = retrieval` DEVE essere rappresentato come «assenza non provata»; «assenza provata» è riservata alla sorgente esaustiva completa.
- **FR-019**: `retrievalComplete` NON DEVE mai essere convertito in «assente», né il silenzio del recupero in `NoAnswer` provato.
- **FR-020** *(risolto 2026-11-09, D-3)*: Errori e indisponibilità dei servizi di ricerca DEVONO essere registrati e propagati nel registro. Embedding indisponibile ⇒ errore tipizzato a zero invii LLM, senza fallback silenzioso. Riordino indisponibile ⇒ ranking RRF **diagnostico** con `retrievalComplete = false`, ammesso solo come registro consultabile.
- **FR-035** *(nuovo, D-3)*: In modalità degradata (riordino indisponibile) il sistema NON DEVE emettere risultati definitivi `AnsweredInSnapshot`, dichiarazioni di assenza di evidenze né attestazioni di completezza dei conflitti, NON DEVE dichiarare completata la valutazione Gap, DEVE rendere esplicito il mancato completamento conservando la causa tipizzata e NON DEVE presentare risultati parziali come validi. Nessun aumento automatico di budget né cambio di modello per aggirare il problema.
- **FR-021**: Nessun esito DEVE apparire completo se uno qualunque dei candidati richiesti non è stato valutato.

**Estrazione e indice**

- **FR-022**: L'estrazione esaustiva delle evidenze NON DEVE usare selezione preventiva distruttiva (filtri su categorie, sommari, classificatori): ogni blocco dello snapshot resta elaborato dall'estrazione come nella 002.
- **FR-023**: Il recupero non DEVE sostituire né ridurre l'estrazione: serve solo a comporre l'input dello stadio Gap.

**Integrazione con la 002**

- **FR-024**: Il fail-fast `gap_ledger_exceeds_input` DEVE essere sostituito, per le nuove richieste, dal percorso di recupero quando il recupero è disponibile; `gap_assessment_exceeds_output` e gli altri limiti restano.
- **FR-025**: La definizione di run, i prompt, i DTO, le chiavi di freeze e i report della 002 DEVONO restare invariati; le novità DEVONO stare dietro una nuova definizione congelata e nuove chiavi di freeze.

**Valutazione continua**

- **FR-026**: Il sistema DEVE includere una valutazione ripetibile con gold versionati, query e metriche (Recall@1/5/10/20, Hit@1, falsi `AnsweredInSnapshot`, conflitti persi, citazioni invalide) promossi a test dai materiali del laboratorio.
- **FR-027**: La valutazione DEVE registrare per hash input, gold, configurazione e versione dei servizi; i risultati DEVONO essere confrontabili fra esecuzioni.
- **FR-028**: La valutazione DEVE poter essere eseguita su servizi reali (opt-in esplicito) e senza di essi con componenti simulati, distinguendo le due modalità nel rapporto.
- **FR-029** *(risolto 2026-11-09, D-1)*: Le soglie Recall@10 ≥ 95%, Hit@1 con riordino ≥ 95%, 0 falsi `AnsweredInSnapshot` e regressione ≤ 2 punti DEVONO essere applicate come condizione di accettazione sul **gold sintetico congelato**; le regressioni si misurano rispetto alla baseline dichiarata sullo stesso insieme di query.
- **FR-036** *(nuovo, D-2)*: L'esito di accettazione DEVE distinguere «accettata» (corpus anonimizzato + ≥ 20 query di un secondo autore, soglie rispettate) da «accettata con riserva», che DEVE dichiarare esplicitamente la mancanza di validazione su corpus anonimizzato e query indipendenti e non è accettazione piena.
- **FR-030**: Ogni rapporto di valutazione DEVE dichiarare provenienza dei dati (sintetici/cliente), autore delle query e limiti noti.

**Compatibilità e vincoli**

- **FR-031**: Run, snapshot, WorkOutput, prompt, gold e checkpoint storici della 001 e 002 NON DEVONO essere riscritti.
- **FR-032**: Non DEVONO essere introdotte interfacce con una sola implementazione, nuovi microservizi, grafi di conoscenza né framework estranei alla slice; un nuovo servizio o dipendenza strutturale richiede ADR proporzionato.
- **FR-033**: Nessun dato di progetto DEVE lasciare l'infrastruttura interna; nessun fallback verso servizi cloud.
- **FR-034** *(precisato 2026-11-09, D-5)*: Readiness, versione dei modelli (nome, artefatto, dimensione vettori) e disponibilità dei servizi DEVONO essere verificate e riportate prima dell'uso, in modo analogo alla readiness del modello generativo (T126); nome, artefatto e dimensione dei vettori sono **fissati** nella configurazione e una difformità blocca l'uso; lo SHA-256 dei pesi è registrato al provisioning e, finché assente, DEVE essere dichiarato «non verificato».
- **FR-037** *(nuovo, D-4)*: Il perimetro funzionale è il solo recupero per lo stadio Gap; Chat, bozza e ricerca per il PM NON DEVONO essere servite dalla 003. I componenti DEVONO restare separabili e riutilizzabili senza introdurre interfacce con una sola implementazione.

### Requisiti architetturali vincolanti (derivati dalla ricerca)

Non sono requisiti utente, ma vincoli che il piano DEVE rispettare, salvo evidenza contraria misurata sul corpus bProjects:

- **A-1** Recupero sui **passaggi figli originali** (non su chunk grandi): i chunk grandi hanno dato Recall@10 44,7% (T082) e 33,3% (T083) contro 100% dei passaggi brevi.
- **A-2** Ricerca **lessicale + semantica multilingue (BGE-M3) con fusione RRF** (k=60); BM25 da solo 78,9% di Recall@10 su T082 (hybrid 100%).
- **A-3** **Riordino BGE Reranker** sui figli candidati: Hit@1 T082 68,4% → 100%, T083 96% → 100%; costo osservato 1,1–2,2 s/query.
- **A-4** **Recupero selettivo del contesto padre con budget**: l'espansione completa moltiplica per circa 14× il testo mediano dei primi 10 (2.028 → 28.963 caratteri su T082); utile per titolo/ambito (caso N4).
- **A-5** **Metadati e provenienza completi** su ogni candidato (documento, versione, sezione, intervallo, hash); ripetere un titolo su ogni riga può peggiorare il lessicale (T082 78,9% → 73,7%).
- **A-6** **Indici derivati e ricostruibili**; nessuna autorità sull'indice.
- **A-7** **Separazione recupero/valutazione**: il recupero non decide esiti; la valutazione non esegue recupero.
- **A-8** **Validazione deterministica** di ID e citazioni (citazioni invalide 12–15 → 1–2 con risoluzione deterministica dell'ID).
- **A-9** **Nessuna selezione distruttiva nell'estrazione esaustiva** (B2: −5 fatti principali e −12 regole; B3: 3/16 fatti).
- **A-10** `retrievalComplete` non viene mai convertito in «assente».
- **A-11** Esclusi dalla 003: sommari SKRAG, NLI come arbitro o filtro (7/12 corretti), knowledge graph, agenti, microservizi, adozione integrale di SKRAG (il codice di riferimento è solo ispirazione e comporta tagli a 1.200/2.400/12.000 caratteri non ammessi).
- **A-12** Motore d'indice **non scelto** (D7): baseline da valutare nel piano = PostgreSQL con ricerca lessicale italiana + pgvector; Qdrant solo se una misura sul corpus bProjects lo giustifica.

### Capacità già implementate nella 002 (non duplicare)

Evidence ledger con provenienza (FR-007 della 002); snapshot congelato per run; catalogo e alias deterministici (`alias-catalog-1`); stadio Gap §13.7 (DTO `gap-stage-alias-1`, prompt `document-gap-v1`, validatore, assembly, coverage-3 con `searchBasis/retrievalComplete/absenceProven`); checkpoint e ripresa; `MaxAiCallsPerRun`; fail-fast tipizzati `gap_ledger_exceeds_input` e `gap_assessment_exceeds_output`; messaggio `message-format-2` (D2/§13.8).

### Dipendenze dalla 002

- Il contratto d'ingresso dello stadio Gap (§13.7) resta **invariato**: la 003 sostituisce solo la sorgente dei candidati.
- Limite residuo ledger > budget: oggi fail-fast; la 003 lo supera solo per i Gap, non per altre fasi.
- Gate aperti della 002 (gate DGX «SC-004-Gap», T082/T083, T107 umano, T114) non sono chiusi dalla 003 e non ne sono prerequisiti, salvo che lo stadio Gap con sorgente esaustiva sia verificato prima di sostituirne la sorgente.
- Servizi DGX embedding/riordino con readiness (analoga a T126); corpus cliente reali per la valutazione indipendente.

### Key Entities

- **Passaggio figlio**: blocco/evidenza originale con documento, versione, sezione, intervallo e hash; unità di ricerca.
- **Contesto padre**: unità contenitore (titolo/sezione/chunk) usata per espansione selettiva con budget.
- **Indice derivato**: rappresentazione lessicale e semantica dei passaggi figli, ricostruibile, mai autorevole.
- **Registro dei candidati**: lista ordinata congelata nel run con ranghi, cutoff, errori ed esclusi per budget.
- **Insieme di valutazione**: gold, query, metriche e rapporto con provenienza dei dati.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Su un progetto con ledger oltre il budget dello stadio Gap, Analizza non termina più con `gap_ledger_exceeds_input` e valuta tutti i Gap annotati (esito, supporti, stato di ricerca).
- **SC-002** *(soglie approvate 2026-11-09)*: Sul gold sintetico **congelato** (T082, T083, N4) il Recall@10 dei passaggi figli ibridi è ≥ 95% (riferimento del laboratorio: 100% su passaggi brevi) e Hit@1 con riordino ≥ 95%; nessuna regressione > 2 punti rispetto alla baseline dichiarata sullo **stesso insieme di query**.
- **SC-003**: Falsi `AnsweredInSnapshot` = 0 sul gold sintetico congelato; conflitti persi dopo risoluzione deterministica degli ID ≤ valore del laboratorio (1–2 su 10 casi) e nessuno per esclusioni a monte del recupero.
- **SC-004**: Citazioni invalide dopo risoluzione deterministica ≤ 2 su 10 casi (riferimento del laboratorio); ogni citazione accettata è verificata letteralmente su un passaggio fornito.
- **SC-005**: 0 passaggi di versioni escluse o di altri progetti fra i candidati in test dedicati.
- **SC-006**: Ricostruzione di un indice da zero produce lo stesso insieme di passaggi indicizzati; i run storici restano invariati byte per byte.
- **SC-007**: La suite della 002 resta verde senza modifiche ai test esistenti; v2–v6 invariati.
- **SC-008**: Nessun Gap con `retrievalComplete = false` o sorgente `retrieval` è mai rappresentato come assenza provata (0 occorrenze in test).
- **SC-009**: Latenza del recupero per Gap, a indice pronto e con riordino, misurata e riportata (riferimento di laboratorio 1,1–2,2 s/query; nessun obiettivo di prestazione assunto fino al piano).
- **SC-010**: La valutazione continua è eseguibile in CI senza servizi reali (simulati) e con servizi reali su opt-in; ogni rapporto dichiara provenienza dei dati e limiti.
- **SC-011** *(nuovo, D-3)*: Con embedding indisponibile: errore tipizzato e 0 invii LLM; con riordino indisponibile: registro RRF diagnostico con `retrievalComplete = false`, 0 risultati `AnsweredInSnapshot` definitivi, 0 dichiarazioni di assenza o di completezza dei conflitti, causa tipizzata conservata (test dedicati).
- **SC-012** *(nuovo, D-2)*: Il rapporto di accettazione riporta «accettata» solo con corpus anonimizzato e ≥ 20 query di un secondo autore; in assenza, riporta «accettata con riserva» con la lacuna evidenziata (0 occorrenze di «accettata» senza i due requisiti).

## Assumptions

- Il corpus di validazione iniziale è sintetico (T082, T083, N4); la validazione su corpus cliente è un requisito di accettazione successivo, non di avvio.
- Docling/parser e blocchi con intervalli sono quelli della 002.
- I modelli di embedding e di riordino sono ospitati su infrastruttura interna e disponibili con readiness verificabile; il modello generativo è quello della 002.
- I dati di progetto sono confinati per progetto; l'isolamento multi-progetto non è ancora provato.

## Limiti e rischi noti

- Dati sintetici; le query di valutazione sono scritte da un solo autore su gold noto; manca la valutazione indipendente su domande spontanee dei PM.
- Il riempitivo sintetico è lessicalmente povero e rende il recupero più facile di contratti reali con clausole quasi equivalenti.
- Hash dei pesi dei modelli non verificati (non esposti dalle API); nomi di artefatto e quantizzazione non li sostituiscono.
- Punteggi del riordino non calibrati e possibilmente negativi.
- PDF, isolamento multi-progetto, versioni escluse e cutoff non provati dal laboratorio.
- Latenze misurate con coseno in Python senza database né BLAS: non rappresentano un'implementazione .NET/pgvector.
- Il laboratorio non ha eseguito analisi o bozza bProjects: l'effetto sulla valutazione completa dei Gap è dimostrato solo su pilota (4/10 esiti completi con figli, 2/10 con padri); i padri hanno migliorato lo stato ma peggiorato le citazioni grezze.
- Nessuna garanzia di completezza: un buon Recall sui gold non dimostra l'assenza di altri riscontri applicabili.
- Indicizzazione di corpora molto grandi: costo a freddo di circa 120–130 s per T082 in laboratorio, non garantito in produzione.
- La 002 non è accettata; la 003 dipende da un'implementazione 002 che può cambiare.

## Decisioni aperte

**Nessuna decisione aperta.** Le cinque decisioni della bozza sono state **risolte dal committente il 2026-11-09** e registrate in «Clarifications» (Session 2026-11-09) e nei requisiti FR-020, FR-029, FR-034, FR-035, FR-036, FR-037 e SC-002, SC-003, SC-011, SC-012:

| # | Tema | Esito (2026-11-09) |
| --- | --- | --- |
| 1 | Soglie numeriche | Recall@10 ≥ 95%, Hit@1 con riordino ≥ 95%, 0 falsi `AnsweredInSnapshot`, regressione ≤ 2 punti; sul gold sintetico congelato, baseline sullo stesso insieme di query. Approvata come raccomandata, con precisazione. |
| 2 | Valutazione indipendente | Accettazione piena solo con corpus anonimizzato e ≥ 20 query di un secondo autore; altrimenti «accettata con riserva» con lacuna esplicita, non equivalente alla piena. |
| 3 | Servizi non disponibili | **Modificata**: embedding ⇒ errore tipizzato, zero invii LLM; riordino ⇒ RRF diagnostico con `retrievalComplete = false`, nessun esito definitivo né completamento Gap. |
| 4 | Ambito | Solo il recupero per la valutazione Gap; componenti riutilizzabili senza interfacce speculative. |
| 5 | Pinning modelli | Nome, artefatto e dimensione vettori fissati in readiness; SHA-256 al provisioning, «non verificato» fino ad allora. |

Questioni tecniche che il piano risolve con misura (non decisioni normative): motore d'indice (`research.md` R-1), mapping blocco→evidenza (R-3), parametri di cutoff e budget dei padri (R-6).

**Conferma del committente (2026-11-09):** lettura conservativa di D-3 confermata: con il riordino indisponibile lo stadio Gap LLM non viene invocato, il registro RRF diagnostico viene persistito e il run termina con causa tipizzata (nessun esito «provvisorio»). Restano da approvare: padri M4 (nuovo formato messaggio, prima di T041) e `absenceProven` sempre falso con sorgente `retrieval`.
