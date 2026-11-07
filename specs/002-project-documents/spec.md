# Feature Specification: Documenti di progetto con provenienza verificabile

**Feature Branch**: `feature/002-project-documents`
**Feature**: `002-project-documents`
**Created**: 2026-11-01
**Status**: Draft — specifica funzionale; nessuna implementazione o accettazione attestata
**Input**: Seconda vertical slice: caricare documenti nel progetto e utilizzarli nella chat, nell'analisi e nella bozza Markdown, anche oltre la finestra di contesto, con originali/versioni immutabili, evidenze, copertura e controllo esplicito del PM.

## Clarifications

### Session 2026-11-07

Decisioni fornite nel mandato di revisione T130/N4 (A + C minima), non nuove risposte inventate: il retry riesamina la proposta respinta come dato non affidabile; nessuna conservazione automatica, nuovo retry o nuova chiamata di revisione. La domanda storica resta nel ledger; la valutazione derivata `AnsweredInSnapshot`/`Open` vale solo per lo snapshot dell'analisi ed è consultabile nella coverage. Risposte parziali o ambigue restano `Open` con motivo strutturato. Il riscontro non conferma requisiti né risolve conflitti. Stesso comportamento funzionale sotto e oltre contesto. Revisione approvata, non ancora implementata o verificata.

**Aggiornamento 2026-11-07 (decisione C + D dopo il gate T138 fallito, solo stato/limite temporaneo, nessun requisito ridotto)**: il gate anticipato sul grafo gerarchico non è stato superato ([validation/T130-N4-revision.md](validation/T130-N4-revision.md)). Il committente ha approvato come soluzione temporanea (a) la valutazione Gap supportata inizialmente dal solo percorso a chiamata singola e (b) per l'oltre contesto con almeno un Gap, fallimento esplicito immediato senza invocazioni LLM (`Failed`, nessuna revisione né anteprima incompleta, mai Completed). **Il requisito resta invariato e aperto**: «stesso comportamento funzionale sotto e oltre contesto» e il 100% dei Gap rendicontato oltre contesto non sono soddisfatti dal percorso temporaneo e non vanno dichiarati verificati; la soluzione definitiva richiede una nuova decisione architetturale (alternative A/B del report, task T138 bloccato). Dettagli e routing: [AI §13.6](contracts/ai-workflow.md#136-decisione-c--d-percorso-temporaneo-e-routing-2026-11-07).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Dai documenti alla bozza verificata dal PM (Priority: P1)

Come PM voglio caricare e consultare documenti nel progetto, usarli insieme alle mie note, correggere e confermare l'analisi e generare una bozza con fonti riconoscibili, senza ricopiare manualmente i documenti nella chat.

**Why this priority**: Il solo upload o il solo testo estratto non completa la slice; serve il percorso utente della 001 esteso ai documenti.
**Independent Test**: Un PM carica un documento per ciascuno dei quattro formati, consulta gli originali, pone domande, avvia Analizza, corregge/conferma e genera il Markdown.

**Acceptance Scenarios**:

1. **Given** un PM autorizzato e un progetto, **When** carica un PDF testuale, DOCX, Markdown o TXT valido entro i limiti, **Then** vede documento, versione, autore, data, dimensione e stato reale; l'originale accettato è scaricabile identico. (FR-001–FR-004)
2. **Given** un documento elaborato, **When** il PM lo consulta, **Then** legge testo e struttura disponibili, inclusi titoli, sezioni, tabelle e riferimenti; numeri, negazioni e vincoli conservano il significato e gli elementi non leggibili sono segnalati. (FR-005/006)
3. **Given** fonti pronte incluse, **When** il PM chiede in chat informazioni presenti soltanto nei documenti, **Then** la risposta usa fonti con versione e passaggio consultabile, distingue documentato, dichiarato PM, dedotto e ipotizzato e non avvia analisi o generazione. (FR-007–FR-010)
4. **Given** affermazioni documentali e PM incompatibili, **When** viene richiesta Analizza, **Then** l'anteprima conserva entrambe le alternative e le fonti, senza scegliere automaticamente per data o tipo di fonte. (FR-010–FR-012)
5. **Given** l'anteprima documentata, **When** il PM corregge, risolve motivatamente o lascia aperto un conflitto, conferma e richiede Genera documento, **Then** la bozza usa la revisione confermata, conserva fonti e punti aperti, resta proposta non approvata e distingue le aggiunte PM dal contenuto documentale. (FR-012/018/019)
6. **Given** solo documenti pronti inclusi e nessun messaggio PM, **When** il PM richiede Analizza, **Then** l'analisi è consentita; senza documenti utilizzabili né messaggi PM è impedita con spiegazione. (FR-011)
7. **Given** il PM riferisce 45 giorni e domanda cosa riporti la scheda A, che documenta 30 giorni, **When** Analizza termina anche dopo il retry ammesso, **Then** la coverage mostra il riscontro della domanda tramite la scheda nello snapshot corrente e l'analisi presenta separatamente il conflitto aperto 45/Declared e 30/Documented, con fonti corrette e senza scelta automatica o duplicazione delle alternative nelle sezioni ordinarie. (FR-010/012/015/016)
8. **Given** una domanda storica con risposta assente, parziale o ambigua, **When** viene analizzata, **Then** resta `Open` con motivo distinto e riferimenti pertinenti; una risposta pertinente completa permette `AnsweredInSnapshot`, senza mutare l'evidenza originale né attribuirle il supporto della risposta. (FR-007/008/015)
9. **Given** una prima proposta respinta contenente anche un falso conflitto o una classificazione/condizione errata, **When** è ammesso l'unico retry, **Then** il riesame usa gli originali e i motivi del rigetto, può correggere o rimuovere gli errori ed emette una proposta completa validata integralmente; nessun contenuto della prima è conservato automaticamente. Se il nuovo input completo supera il limite, non viene troncato né inviato. (FR-014/016/021)

---

### User Story 2 - Usare documenti oltre contesto senza perdere informazioni (Priority: P1)

Come PM voglio usare documenti che non entrano nella finestra di contesto, sapere quali parti sono state elaborate e verificare che le informazioni applicabili raggiungano analisi e bozza.

**Why this priority**: È una capacità essenziale della 002. Un errore di contesto correttamente gestito non dimostra il caso nominale.
**Independent Test**: Un documento singolo oltre contesto e un insieme complessivamente oltre contesto, con attese annotate all'inizio, nel mezzo e alla fine, percorrono chat → analisi → conferma → bozza; una prova separata introduce una porzione illeggibile.

**Acceptance Scenarios**:

1. **Given** un documento valido oltre contesto ma entro i limiti di acquisizione, **When** termina l'elaborazione e il PM interroga punti lontani e completa il percorso, **Then** le informazioni applicabili delle attese sono rappresentate con riferimenti corretti; il semplice rifiuto per dimensione del contesto non supera la prova. (FR-013–FR-015)
2. **Given** più documenti singolarmente sotto contesto ma complessivamente oltre contesto, **When** il PM li include e richiede Analizza, **Then** l'intero insieme selezionato contribuisce al risultato, inclusi i conflitti fra documenti e note. (FR-010/013–FR-015)
3. **Given** una porzione non elaborata o un errore, **When** il PM consulta lo stato o richiede Analizza con la fonte inclusa, **Then** vede versione, porzione localizzabile e motivo, senza falso completamento; Analizza è impedita finché la fonte non è pronta o esplicitamente esclusa. (FR-006/014/016)
4. **Given** evidenze documentali e un'analisi, **When** il PM consulta la copertura, **Then** distingue porzioni elaborate, collegamenti alle evidenze e ai risultati, rischi semantici e valutazioni umane; una percentuale di elaborazione non certifica la completezza semantica. (FR-015)
5. **Given** fonti oltre contesto e modello interno disponibile, **When** vengono elaborate, **Then** monitor e metriche distinguono fonti conservate e contesto operativo, misure e stime, attese ed elaborazioni, senza valori inventati o perdita degli originali. (FR-020)

---

### User Story 3 - Controllare versioni, selezioni e ripresa (Priority: P1)

Come PM voglio scegliere le fonti applicabili, aggiungere versioni senza perdere le precedenti e riprendere dopo un guasto sapendo quali fonti hanno prodotto ogni risultato.

**Why this priority**: Versionamento e controllo proteggono i risultati verificati dal PM e consentono uso quotidiano affidabile.
**Independent Test**: Caricare due versioni, escludere una fonte, avviare elaborazioni durante questi cambi e riaprire dopo riavvio; verificare storia, download, selezioni e retry senza duplicati.

**Acceptance Scenarios**:

1. **Given** un documento già usato, **When** il PM carica esplicitamente una nuova versione, **Then** nessun originale o risultato storico è sovrascritto; la nuova versione diventa applicabile ai nuovi comandi solo quando pronta e la precedente resta consultabile. (FR-003/017–FR-019)
2. **Given** le fonti del progetto, **When** il PM esclude un documento o sceglie una versione pronta diversa, **Then** la scelta persiste e i comandi successivi la rispettano, mentre quelli già accettati conservano la selezione originaria; le esclusioni sono visibili. (FR-008/017/018)
3. **Given** analisi confermata e bozza, **When** arriva un documento, una versione o cambia una selezione, **Then** i risultati restano identici e sono segnalate differenze nelle fonti; solo nuova analisi esplicita, verifica/conferma e generazione aggiornano la bozza. (FR-018/019)
4. **Given** interruzione di upload, elaborazione o browser, **When** il PM riapre e richiede un retry ammesso, **Then** vede stato ed esito effettivi, non ottiene duplicati per la stessa richiesta e conserva originali accettati e risultati precedenti. (FR-004/016/021)
5. **Given** due PM che cambiano contemporaneamente la selezione, **When** il secondo salva su base superata, **Then** riceve un conflitto e può rivedere la selezione corrente senza sovrascrittura silenziosa. (FR-017)
6. **Given** un file oltre quota, corrotto, protetto o non supportato, **When** è caricato/elaborato, **Then** il PM vede motivo e azione possibile; un PDF solo immagine non risulta fonte testuale pronta e nessun risultato precedente viene perso. (FR-002/006/016)

### Edge Cases

- File vuoto, nomi uguali e doppio invio: identità/versioni distinguibili, nessuna sostituzione basata sul nome.
- Formato effettivo diverso da quello dichiarato, password o corruzione: rifiuto/fallimento esplicito, mai testo inventato o falso stato pronto.
- PDF misto testo/immagini: parti trattabili e parti non interpretate distinte; contenuto potenzialmente informativo non trattato rende la fonte incompleta, senza OCR generalizzato.
- Tabelle multipagina, celle unite, numeri/unità, note, esclusioni e riferimenti incrociati: conservarne il significato o segnalare l'impossibilità, senza alterare silenziosamente associazioni fra valori e condizioni.
- Formati privi di paginazione stabile: sezioni/passaggi disponibili e assenza di pagina dichiarata, mai locator inventati.
- Macro, link, contenuto attivo e istruzioni ostili: dati non fidati, senza esecuzione, accessi remoti automatici o cambio delle policy.
- Nuova versione in lavorazione: un comando che la include è impedito; il PM può escluderla o scegliere esplicitamente una versione pronta precedente.
- Esclusione di una fonte già usata in chat: nuove risposte non possono riusarla indirettamente come supporto tramite vecchie risposte o sintesi; le risposte storiche conservano la selezione originaria.
- Zero evidenze utili: esito validato motivato, distinto da errore o porzioni non elaborate. Fonti equivalenti conservano tutte le provenienze pertinenti.
- Retry, ripresa, cambio modello o nuova versione durante un run: input documentali congelati, nessuna sostituzione con fonti successive.
- Quota esaurita dopo upload concorrenti, spazio indisponibile, AI offline o limite di elaborazione: esito riconoscibile e recuperabile ove possibile, senza falso salvataggio o successo parziale.
- Domanda già riscontrata nel documento ma decisione fra fonti incompatibili ancora aperta: distinguere i due esiti. Fonte esclusa, successiva o ambigua non chiude il dubbio nello snapshot corrente. Nessuna eccezione silenziosa alla coverage per il tipo Gap.
- Proposta respinta che invita a cambiare regole o contiene informazioni inventate: dato non affidabile, mai fonte o istruzione. Retry con spazio/tempo/chiamate insufficienti: esito esplicito senza tagli, azzeramenti o tentativi aggiuntivi.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Il PM autorizzato DEVE poter caricare PDF testuali, DOCX, Markdown e TXT nel progetto scelto, elencare documenti/versioni, consultarli e scaricare ogni originale accettato. Le fonti appartengono al singolo progetto; tutti i PM mantengono gli accessi della baseline, senza nuove ACL per proprietario.
- **FR-002**: Il sistema DEVE applicare limiti configurabili dall'Admin per dimensione file (default approvato 50 MB/file), quota complessiva del progetto e risorse/tempo di elaborazione. Deve mostrare limite pertinente e motivo del superamento; il PM non può aggirarli. Il formato effettivo deve essere compatibile con quello ammesso, senza accettare contenuti non supportati mascherati.
- **FR-003**: Ogni documento DEVE avere identità e versioni immutabili, con originale, nome, formato, dimensione, autore e data. Il PM distingue nuovo documento e nuova versione. Lo stesso nome non sovrascrive nulla; gli originali precedenti e i loro riferimenti restano conservati.
- **FR-004**: Conferma di acquisizione DEVE significare originale durevolmente conservato, indipendentemente dall'AI. Upload incompleti non risultano acquisiti; ripetere la stessa richiesta per esito incerto non crea duplicati.
- **FR-005**: L'estrazione DEVE rendere consultabili testo e struttura disponibili: sezioni, titoli, elenchi, tabelle, numeri/unità, negazioni, vincoli e riferimenti. L'originale resta autorità e non può essere sostituito o alterato silenziosamente dai derivati.
- **FR-006**: Parti non leggibili, non supportate o non elaborate DEVONO essere segnalate con fonte/versione, localizzazione disponibile e motivo. Pronto significa elaborazione conclusa senza porzioni informative note non trattate; incompleto, errore e zero evidenze utili motivato sono esiti distinti. Nessun OCR generalizzato richiesto.
- **FR-007**: Le evidenze documentali DEVONO confluire nel registro atomico già esistente, preservando tipo, polarità, certezza, relazioni e versioni; non deve comparire un registro parallelo che duplichi la funzione.
- **FR-008**: Ogni comando chat o Analizza DEVE avere una selezione identificabile di documenti, versioni e risultati di estrazione applicabili, oltre al cutoff del dialogo. La selezione è congelata all'accettazione del comando e invariata nei retry/recuperi. Fonti successive non entrano retroattivamente, nemmeno tramite sessioni o sintesi.
- **FR-009**: Ogni evidenza documentale e affermazione documentata in chat, analisi o bozza DEVE risalire a documento, versione e passaggio originale pertinente, con pagina e/o sezione ove disponibili. Fonti inesistenti, fuori progetto o non selezionate sono rifiutate; locator mancanti sono dichiarati, mai inventati.
- **FR-010**: Chat, evidenze, analisi e bozza DEVONO distinguere documentato, dichiarato PM, dedotto e ipotizzato. Documenti e risposte AI non sono conferme PM. Contraddizioni fra fonti o note conservano alternative e provenienze senza scelta automatica per data o natura della fonte.
- **FR-011**: Analizza DEVE essere esplicito, ammesso con almeno un documento pronto incluso o un messaggio PM valido. Se una fonte inclusa non è pronta, è fallita o incompleta, il comando è impedito con spiegazione: il PM può attendere, ritentare ove ammesso o escluderla esplicitamente. Nessun consolidamento silenzioso di un sottoinsieme. Una domanda chat che richiede una fonte inclusa non pronta deve segnalare l'indisponibilità senza inventare una risposta documentata; può comunque salvare il messaggio e fornire un chiarimento sullo stato.
- **FR-012**: Analizza → correggi → conferma → Genera Markdown DEVE preservare revisioni attribuite, conflitti espliciti, conferma della revisione corrente e generazione separata della 001. La bozza conserva il legame con revisione e fonti effettivamente usate; un edit PM non è attribuito al documento.
- **FR-013**: Documenti validi entro i limiti di acquisizione DEVONO essere utilizzabili anche quando il singolo contenuto o l'insieme selezionato supera la finestra di contesto. Vale per chat, analisi e generazione; il solo rifiuto per eccesso di contesto non soddisfa il requisito. Non è richiesta capacità illimitata oltre quote/risorse dichiarate.
- **FR-014**: Nessuna fase DEVE troncare o omettere silenziosamente informazioni per rispettare il contesto. Se non può completare la richiesta, deve dichiarare limiti e parti non trattate senza presentare il risultato come completo. Esclusioni richiedono scelta PM registrata e non equivalgono al successo nominale che richiede la fonte esclusa.
- **FR-015**: Il PM DEVE poter verificare porzioni documentali → evidenze → analisi → revisione PM → bozza attraverso il sistema di copertura esistente, legato agli input esatti. Deve distinguere porzioni elaborate/non elaborate, assenza motivata di evidenze, evidenze non rappresentate e modifiche PM. Errori deterministici, rischi semantici stimati e valutazioni PM restano separati; elaborazione completa e collegamenti presenti non certificano fedeltà semantica.
- **FR-016**: Acquisizione/elaborazione DEVONO avere stati persistenti visibili: acquisizione, acquisito/in attesa, elaborazione, pronto, incompleto o fallito, con errore e azioni ammesse. Il lavoro prosegue senza browser; retry/recupero limitati e tracciabili non duplicano risultati né cancellano originali o ultimi esiti riusciti. Lettura e riconnessione non avviano tentativi.
- **FR-017**: Il PM DEVE poter selezionare/escludere fonti e scegliere la versione pronta applicabile, con preferenze persistenti nel progetto e conflitto su aggiornamenti concorrenti. Nuovi documenti inclusi per default sono indisponibili finché non pronti. Una nuova versione sostituisce la precedente nei nuovi comandi solo quando pronta, salvo selezione esplicita precedente; un documento escluso rimane escluso dopo aggiornamento.
- **FR-018**: Originali, versioni e risultati di estrazione storicamente usati DEVONO restare consultabili e identificabili dopo aggiornamenti, rielaborazioni, cambi selezione o riavvii. Genera documento usa solo revisione confermata e sue fonti congelate; non incorpora documenti successivi all'analisi.
- **FR-019**: Upload, elaborazione, versioni e selezioni NON DEVONO avviare analisi/generazione, cambiare risultati preesistenti o revocare implicitamente conferme storiche. La UI segnala nuove informazioni o differenze di fonti; per aggiornare serve il nuovo percorso esplicito PM.
- **FR-020**: Monitor del contesto e telemetria AI esistenti DEVONO comprendere le fonti documentali e i lavori associati: modello/fase, durata, attese, tentativi, token e budget disponibili, con provenienza misurata/calcolata/stimata/non disponibile. Distinguere documenti conservati da contesto effettivo; nessun contenuto integrale, segreto o ragionamento interno esposto nella telemetria.
- **FR-021**: Accesso, download, evidenze e uso AI DEVONO rispettare autenticazione e ambito progetto anche tramite riferimenti diretti. Documenti/macro/link/istruzioni sono dati non fidati: nessuna esecuzione, fetch remoto automatico, elevazione privilegi o invio a LLM esterni. Un'elaborazione nel progetto A non usa fonti del progetto B, anche se il PM può consultarli entrambi.
- **FR-022**: Il percorso DEVE essere utilizzabile da tastiera e su desktop/mobile, con etichette, focus, errori e stati percepibili senza solo colore. Progresso e fasi devono derivare da attività effettive, non simulazioni. Progetti senza documenti mantengono il comportamento della 001.

Precisazioni normative della revisione 2026-11-07 (mantengono gli ID e le soglie precedenti):

- **FR-010/012**: i conflitti effettivi DEVONO essere espliciti e aperti con alternative attribuite anche dopo retry; una domanda sulla discordanza non sostituisce il conflitto. Le alternative non si duplicano impropriamente nelle sezioni ordinarie. Una proposta respinta non è prova della correttezza di un conflitto.
- **FR-007/008/015**: ogni Gap derivato da domanda PM DEVE conservare originale, tipo, versione e provenienza. L'analisi DEVE rendicontarlo mediante valutazione derivata nello snapshot: `AnsweredInSnapshot` con riscontro pertinente sostenuto dalle evidenze, oppure `Open` con motivo strutturato (anche risposta parziale/ambigua). Valutazione e riferimenti devono essere consultabili nella coverage esistente; non sono supporto automatico di requisiti, conferma PM o risoluzione di conflitti. Collegamenti e visite non certificano la correttezza semantica del riscontro.
- **FR-014/016/021**: l'unico retry ammesso DEVE ricevere gli originali congelati, la prima risposta delimitata come proposta non affidabile e i motivi tipizzati, per riesame integrale di contenuti, classificazioni, fonti, conflitti, condizioni e qualificatori. Il risultato è completo e autonomo, validato per intero; nessun merge o conservazione automatica. Dimensione effettiva del nuovo input verificata prima dell'invio; nessun taglio, nuova chiamata di revisione o secondo retry. Deadline, budget, contatori, fencing, idempotenza e vincoli delle fonti restano invariati.

### Perimetro, continuità e dipendenze

Fonti: [Constitution](../../.specify/memory/constitution.md), [piano MVP](../../AI-Project-Documentation-MVP-Plan.md) (D04, D18, D28, D32, D49, A49–A52), [copertura](../../docs/roadmap/implementation-coverage.md), [spec 001](../001-project-draft/spec.md), [contratto HTTP](../001-project-draft/contracts/http-api.md) e [contratto AI](../001-project-draft/contracts/ai-workflow.md).

Si estendono sorgenti e tracciabilità senza duplicare chat, Evidence, revisione/conferma, generazione, recovery, monitor o telemetria. La 001 ammette oggi fonti messaggio/revisione PM e richiede un messaggio per Analizza: FR-009–FR-012 descrivono l'evoluzione funzionale necessaria. I contratti compatibili saranno definiti in plan, senza riscrivere gli artefatti della 001. Risultati precedenti restano leggibili senza inventare fonti documentali mancanti.

Microsoft Agent Framework, persistenza applicativa, originali indipendenti dalle sintesi e LLM solo interni restano vincoli ereditati dalla Constitution. Classi, database, API, parser, segmentazione e strategie oltre contesto appartengono a plan; le indicazioni tecniche già approvate nel piano MVP andranno valutate in quella sede entro il perimetro della 002.

Esclusi: KB condivise GLOBAL/PRODUCT/CUSTOMER, promozione automatica a KB, RAG, Qdrant, embedding, ricerca vettoriale, OCR generalizzato, ricerca web, nuovi export PDF/DOCX, editor avanzato, pubblicazione/approvazione cliente, amministrazione generalista e cancellazione definitiva documentale. Escludere fonti non cancella storia/originali. Nessuna stima AI di ore, costi, date o risorse.

La [001 resta accettata con riserve per il pilota](../001-project-draft/acceptance/decisione-accettazione.md): T052/T053 aperti, FAIL e limiti semantici/temporali invariati. Questa specifica non li chiude, non sana T058/T061/T062 e non altera attese storiche. I criteri della 002 sono obiettivi da verificare, non esiti acquisiti.

### Key Entities *(include if feature involves data)*

- **Documento di progetto**: fonte di un progetto con identità distinta dal nome e storia versionata.
- **Versione documentale**: originale immutabile attribuito all'autore dell'upload, con data, formato, dimensione e stato.
- **Contenuto estratto e porzione**: testo/struttura derivati con localizzazione ed esito; non sostituiscono l'originale.
- **Evidenza/riferimento documentale**: estensione delle evidenze con documento/versione, passaggio e pagina/sezione disponibile, mantenendo tipo, polarità, certezza e relazioni.
- **Selezione fonti**: inclusioni/esclusioni persistenti PM e selezione storica immutabile di ogni elaborazione.
- **Lavoro documentale**: elaborazione tracciabile con stati, tentativi, errori e risultati recuperabili.
- **Valutazione Gap nello snapshot**: interpretazione derivata e tracciabile della domanda storica, con esito, motivo e riscontri; non è un nuovo registro né uno stato globale dell'evidenza.
- **Copertura documentale**: estensione dei report esistenti da porzioni/evidenze ai risultati, distinguendo limiti e valutazioni umane.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Due PM completano indipendentemente carica → consulta → domanda chat → Analizza → correggi/conferma → Genera Markdown senza assistenza tecnica, entro 10 minuti di interazione attiva ciascuno, escluse preparazione file e attese. Entrambi riconoscono fonti e proprie correzioni; un tecnico estraneo ricostruisce requisiti, vincoli e punti aperti da bozza e riferimenti.
- **SC-002**: Per ciascun formato, il 100% degli originali accettati è riscaricabile identico e le versioni distinguibili in tre sessioni con riavvio. File validi al limite configurato sono accettati; oltre limite/quota rifiutati con motivo. Ripetere la stessa richiesta non crea una seconda versione.
- **SC-003**: In almeno cinque casi nominali con attese annotate prima delle prove, che coprono i quattro formati, tabelle/numeri/negazioni, conflitti documento-PM/documento-documento, nuove versioni ed esclusioni, il 100% dei requisiti/vincoli/esclusioni applicabili attesi è corretto in analisi e bozza. Zero inversioni di negazione, citazioni inventate, conflitti risolti automaticamente o omissioni sostanziali; classificazioni documentato/dichiarato/dedotto/ipotizzato verificabili dal revisore.
- **SC-004**: Due prove nominali oltre contesto riescono con modello interno reale: un documento singolo e un insieme di almeno tre documenti. Per entrambe il testo estratto totale supera almeno due volte il budget di input del modello usato per analizzarlo, restando nelle quote; la prima contiene almeno 12 fatti applicabili annotati distribuiti fra inizio, metà e fine. Il 100% delle attese arriva ad analisi/bozza e risposte chat su tre zone lontane hanno fonti corrette. Ridurre il file, aumentare la finestra per farlo rientrare, escluderlo o ottenere solo un errore non supera la prova.
- **SC-005**: Nel 100% delle citazioni dei casi nominali il revisore raggiunge documento/versione/passaggio corretto, con pagina/sezione ove disponibile, anche dopo aggiornamento. Numeri/unità, relazioni tabellari e vincoli annotati conservano il significato; assenza di pagina stabile dichiarata.
- **SC-006**: Nelle prove di file corrotto/protetto, PDF solo immagine, PDF misto illeggibile, AI indisponibile e limite risorse, ogni fonte ha esito esplicito e nessuna porzione non trattata risulta completa. Analizza con fonte inclusa non pronta è impedita nel 100% dei casi; l'esclusione esplicita non conta come successo nominale di quella fonte. In chat la fonte indisponibile è segnalata, senza contenuti documentati inventati.
- **SC-007**: Nel 100% delle prove di upload/versione/esclusione durante chat/analisi, ogni comando conserva la selezione accettata anche dopo retry/riavvio. Originali, risultati storici, revisioni confermate e bozze precedenti restano identici; solo un nuovo percorso esplicito aggiorna i risultati. Comandi successivi non usano indirettamente fonti escluse tramite sintesi/risposte precedenti.
- **SC-008**: Prove di interruzione dell'acquisizione, elaborazione e pubblicazione esito producono al massimo un risultato persistito per richiesta logica; stato e tentativi sono recuperabili e un elaboratore non più autorizzato non pubblica risultati. Ogni errore termina entro il limite configurato o è riconciliato alla prima ripresa utile, senza perdere originali accettati.
- **SC-009**: Ogni prova documentale dispone di copertura legata agli input esatti; tutte le porzioni note non trattate e le evidenze non collegate sono segnalate, separando cause, rischi stimati e modifiche PM. Il revisore distingue elaborato da semanticamente verificato; la fedeltà SC-003/004 è valutata separatamente.
- **SC-010**: Nel 100% delle prove senza autenticazione, fuori ruolo, con riferimenti a un altro progetto o istruzioni ostili, nessun dato non autorizzato è restituito e nessuna fonte estranea al progetto è usata. Nessuna esecuzione di contenuto attivo, fetch automatico o invio a LLM esterni.
- **SC-011**: Percorso completo eseguibile da tastiera a 1440 e 390 pixel, senza scorrimento orizzontale della pagina; stati/errori percepibili senza solo colore. Un progetto senza documenti conserva il percorso 001 e non avvia lavori alla semplice lettura.
- **SC-012**: Ogni elaborazione AI documentale espone modello, esito, durata, tentativi e token/budget con origine o indisponibilità dichiarata. Upload, preparazione, coda e fasi AI sono misurati separatamente. Secondo la decisione D1 del committente, il PM impartisce un solo comando e il sistema assegna automaticamente il regime: profilo nominale ordinario con riferimento 180 s dal comando; regime esteso asincrono con limite massimo iniziale configurato 60 minuti, non durata prevista o garantita. Un cambio di regime è comunicato, controllato e limitato, senza azzerare il tempo consumato; nessuna conferma tecnica aggiuntiva del PM. Stati e avanzamenti reali, navigazione e recupero dopo disconnessioni/riavvii; nessuna omissione o successo incompleto per rispettare la scadenza. Preparazione documentale ha il proprio limite configurato di 60 minuti dalla richiesta. I test distinguono profili e cambi di regime senza riclassificare ex post un nominale lento come superato; criteri e riserve della 001 invariati.

Integrazione di **SC-003/009** per la revisione T130/N4: nelle prove nominali e avverse annotate prima dell'esecuzione, il 100% dei Gap domanda è rendicontato con esito/motivo/riferimenti corretti, senza chiusure ingiustificate; falsi conflitti della prima proposta non sono conservati e conflitti reali restano espliciti. Provare anche risposta parziale/ambigua, esclusione/versione diversa, limiti del retry e compatibilità storica; stesso criterio nel percorso oltre contesto. Le soglie esistenti, il gold N4 e la distinzione fra controlli deterministici e giudizio semantico restano invariati.

**Stato temporaneo e criteri ancora aperti (2026-11-07, decisione C + D)**: nelle prove single-call i criteri sopra sono verificabili e invariati. Per il percorso oltre contesto il criterio «100% dei Gap rendicontati con esito/motivo/riferimenti corretti» **non è soddisfatto né verificabile** finché non sarà risolta la decisione su T138: nell'intervallo l'unico comportamento ammesso è il fallimento esplicito immediato (run `Failed` con `document_limit_exceeded` e motivo `hierarchical_gap_assessment_unsupported`, zero invocazioni LLM, nessuna revisione né anteprima incompleta, nessun WorkOutput) quando lo snapshot contiene Gap; senza Gap il percorso oltre contesto resta come in v4. Questo limite temporaneo non riduce FR-013, SC-004, US2 né il gold: SC-004 e FR-013 restano aperti (gate T082/T083/T090) e l'impatto di prodotto (run oltre contesto con Gap falliti subito) è un rischio residuo dichiarato, non un'accettazione.

## Assumptions

- Interfaccia/prove in italiano, account PM e modello interno predisposti; documenti autorizzati e attese annotate prima delle prove. Guasti e simulazioni non sostituiscono nominali reali.
- «Proprio progetto» indica il progetto aperto, non proprietà esclusiva: tutti i PM mantengono la visibilità approvata, con input AI isolati per progetto.
- Il PM opera autonomamente sugli allegati del progetto, senza approvazione Admin; l'Admin configura limiti pertinenti, senza nuova console amministrativa generale.
- Quote complessive e limiti di preparazione non numericamente fissati nelle decisioni approvate saranno esplicitati in configurazione e prove durante plan; nessuna nuova quota di prodotto inventata qui. Default file: 50 MB.
- Una sola versione pronta per documento è applicabile a ogni comando; precedenti selezionabili/consultabili. La più recente pronta diventa default salvo scelta esplicita PM (FR-017). Fonte esclusa resta esclusa.
- Consultazione comprende elenco, metadati, testo estratto strutturato, passaggio citato e download originale; non richiede editor o viewer con fedeltà tipografica completa.
- La sola compattazione 001 non è assunta sufficiente per i documenti oltre contesto. Plan dovrà dimostrare la fattibilità, anche della generazione da analisi confermata ampia, senza decidere qui strategie tecniche.
- Nessuna nuova funzione di cancellazione/retention: conservare originali/versioni necessari alla storia. La 002 non implica nuova accettazione generale della 001.
