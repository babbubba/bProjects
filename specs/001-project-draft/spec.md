# Feature Specification: Dal dialogo alla bozza Markdown verificabile

**Feature Branch**: `restart/speckit-clean`
**Created**: 2026-10-27
**Updated**: 2026-10-28
**Status**: Draft consolidato — decisioni F-01–F-07 recepite
**Input**: Prima vertical slice bProjects, aggiornata con analisi strutturata, anteprima
modificabile e conferma PM prima della generazione esplicita del documento.
Le decisioni correnti sostituiscono il precedente collegamento diretto Analizza → Markdown.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Conversare, verificare l'analisi e ottenere la prima bozza (Priority: P1)

Come PM voglio raccogliere e chiarire le informazioni in una chat persistente,
richiedere un'analisi, correggerne l'anteprima e confermarla prima di generare una
bozza, così che un tecnico estraneo alle conversazioni possa comprendere il lavoro.
Confermare l'analisi non equivale ad approvazione del cliente o del documento finale.

**Why this priority**: È un unico percorso utilizzabile end-to-end; salvare note,
produrre solo un JSON o generare un testo senza verifica PM non completa la slice.
**Independent Test**: Un PM crea un progetto, conversa, riprende il dialogo dopo
riavvio, seleziona Analizza, modifica e conferma l'anteprima, poi richiede Genera documento
e legge la bozza con evidenze. Nessun allegato, KB, web, editor avanzato o assistenza tecnica.

**Acceptance Scenarios**:

1. **Given** un PM autorizzato, **When** crea un progetto con nome non vuoto e descrizione facoltativa, **Then** lo ritrova con una propria chat, anche se esistono progetti omonimi. Nome whitespace è rifiutato con spiegazione. (FR-001, FR-002)
2. **Given** un progetto, **When** il PM invia testo con modello disponibile oppure indisponibile, **Then** il messaggio viene salvato prima dell'inferenza e ne riceve conferma; il turno AI ha un proprio stato. Un salvataggio fallito lascia recuperabile il testo e non lo dichiara salvato. (FR-003, FR-004, FR-008)
3. **Given** un'ambiguità concreta, **When** ConversationalAgent risponde, **Then** produce una risposta pertinente e, se necessario, al massimo una domanda mirata; usa informazioni già presenti e non ripete domande risolte. Non consolida né genera documenti. (FR-005, FR-018)
4. **Given** due invii ravvicinati U1 e U2 prima della risposta A1, **When** vengono elaborati, **Then** l'ordine è FIFO, ogni risposta è collegata al trigger e U2 non usa A1 se A1 è successiva al cutoff di U2, neppure tramite la sessione ripristinata. (FR-007, FR-019)
5. **Given** un turno interrotto, **When** scade il lease o si verifica un errore transitorio, **Then** recovery/retry controllati preservano messaggi ed esiti, rispettano i limiti configurati e non pubblicano due risposte. Un errore permanente o tentativi esauriti produce Failed. (FR-014, FR-020)
6. **Given** almeno un messaggio PM salvato, **When** il PM seleziona Analizza, **Then** parte soltanto l'analisi formale sullo snapshot assegnato; senza messaggi PM l'avvio è impedito. Invii duplicati non creano analisi concorrenti. (FR-006, FR-007)
7. **Given** un'analisi riuscita, **When** il risultato supera la validazione dello schema, **Then** il JSON versionato è salvato e l'anteprima è disponibile; nessun Markdown viene generato e lo stato indica attesa della verifica PM. (FR-008, FR-009, FR-025)
8. **Given** un'anteprima, **When** il PM corregge descrizioni/requisiti, aggiunge informazioni o modifica assunzioni, **Then** una nuova revisione persistita conserva paternità e provenienza delle modifiche, senza cambiare chat o risultato AI originale. Un salvataggio su revisione obsoleta segnala conflitto. (FR-010, FR-012, FR-021)
9. **Given** affermazioni incompatibili, **When** il PM verifica il conflitto, **Then** può scegliere esplicitamente una risoluzione motivata oppure lasciarlo aperto; alternative ed evidenze restano tracciabili. Nessuna scelta avviene automaticamente per data o ruolo della fonte. (FR-011, FR-012)
10. **Given** l'anteprima corrente, **When** il PM la conferma esplicitamente, **Then** la revisione confermata diventa immutabile e Genera documento è disponibile; conflitti lasciati aperti restano espliciti. La conferma non approva il cliente o il documento. (FR-011, FR-021, FR-025)
11. **Given** un'analisi non confermata o modificata dopo conferma, **When** viene richiesta la generazione, **Then** è impedita senza chiamare DocumentAgent finché non viene confermata la revisione corrente. (FR-021, FR-022)
12. **Given** la revisione corrente confermata, **When** il PM seleziona Genera documento, **Then** DocumentAgent usa esattamente quello snapshot e salva una bozza Proposed/NotApproved con riferimento alla revisione, fonti e questioni aperte. (FR-022, FR-023, FR-024)
13. **Given** una generazione già avviata, **When** il PM modifica l'anteprima, **Then** il run conserva il proprio snapshot; l'edit crea una revisione non confermata. La bozza eventualmente completata indica la versione usata e nessuna ulteriore generazione parte senza nuova conferma. (FR-021, FR-022, FR-023)
14. **Given** note, risposte, anteprima o bozza salvati, **When** browser/app vengono riavviati e il PM torna in tre sessioni successive, **Then** ritrova contenuti e metadati invariati; stati ancora pendenti possono avanzare tramite recovery, senza rigenerare risultati già committati. L'attesa della verifica umana sopravvive senza timeout di elaborazione. (FR-004, FR-008, FR-025)
15. **Given** errore del modello durante Analizza, **When** termina l'elaborazione, **Then** stato Failed ed errore sono visibili, senza falsa anteprima riuscita né perdita di originali e risultati precedenti. (FR-014, FR-016)
16. **Given** errore durante Genera documento, **When** termina l'elaborazione, **Then** lo snapshot confermato e l'ultima bozza riuscita restano disponibili; una nuova richiesta esplicita può ritentare la generazione. (FR-014, FR-023)
17. **Given** uno stesso invio/comando ripetuto per esito HTTP incerto, **When** viene ripresentata la medesima chiave, **Then** si recupera la stessa elaborazione senza nuovi originali, revisioni AI o bozze duplicate. Le inferenze interrotte possono essere ripetute senza duplicare risultati persistiti. (FR-019, FR-020, FR-023)
18. **Given** contesto che attiva la soglia di compattazione ridotta per la prova, **When** si esegue un turno o un'analisi, **Then** si attiva realmente la compattazione nativa MAF, il contesto rientra nel budget e originali/analisi confermate rimangono intatti. Un fallimento esplicito non produce omissioni silenziose. (FR-017)
19. **Given** due progetti e accessi di ruoli differenti, **When** vengono consultati o elaborati, **Then** i PM autorizzati vedono i progetti consentiti dalla baseline ma ciascun agente usa il proprio ambito; accessi anonimi/non autorizzati sono respinti. (FR-001, FR-015)
20. **Given** istruzioni malevole nel dialogo o assenza di LLM interno, **When** viene richiesta un'elaborazione, **Then** non cambiano le regole, non si contattano LLM esterni e non si effettuano approvazioni o pubblicazioni. (FR-011, FR-016)
21. **Given** informazioni disponibili e una revisione confermata, **When** viene generata la bozza, **Then** sono inclusi tutti i requisiti e vincoli applicabili, senza riempitivi, ripetizioni o sezioni non pertinenti; lunghezza e articolazione seguono la complessità, non un conteggio di parole. (FR-010, FR-024)
22. **Given** nuovi messaggi PM dopo il cutoff, **When** chat/anteprima/bozza vengono riaperte, **Then** le nuove informazioni sono segnalate ma non alterano risultati o conferme esistenti. Una nuova analisi esplicita produce un nuovo risultato da verificare. (FR-005, FR-007, FR-013)
23. **Given** il percorso nel browser a 1440 e 390 pixel di larghezza, **When** il PM passa da chat ad analisi, modifica, conferma e generazione, **Then** progetto e fase corrente restano riconoscibili, i controlli sono utilizzabili senza scorrimento orizzontale della pagina e ogni fase presenta una sola azione primaria. (FR-026)
24. **Given** una risposta chat in streaming o una fase formale in corso, **When** arrivano aggiornamenti, un errore o una disconnessione, **Then** testo provvisorio e risposta salvata sono distinguibili, le fasi mostrate corrispondono a eventi reali e la riconnessione recupera gli esiti persistiti senza nuovi comandi; errori e tentativi disponibili sono comprensibili. (FR-027)
25. **Given** navigazione da tastiera, lettore di schermo e preferenza di movimento ridotto, **When** il PM percorre le viste principali, **Then** focus, etichette, errori e cambi di stato sono percepibili e utilizzabili senza affidarsi soltanto al colore o alle animazioni. (FR-028)

### Edge Cases

- Risposta tardiva e sessione più avanzata: il cutoff prevale sempre; il contesto viene ricostruito dagli originali ammessi.
- Messaggio PM già committato con AI indisponibile: resta nello storico anche se il turno fallisce.
- Worker con lease scaduto: non può più committare, anche se la chiamata LLM termina successivamente.
- Recovery dopo crash, retry transitorio e nuovo tentativo PM sono eventi diversi; i limiti non si azzerano al riavvio.
- JSON invalido o riferimenti inventati: nessuna anteprima riuscita; niente riparatore AI automatico.
- Risposte assistant sono contesto, mai sole evidenze di fatti; le aggiunte PM nell'anteprima sono fonti attribuite alla revisione e all'autore.
- Conferma e modifica concorrenti: controllo della revisione corrente, nessuna sovrascrittura silenziosa.
- Conflitti aperti possono essere confermati come punti aperti; la conferma non li risolve.
- Nuovo edit dopo conferma: nuova revisione non confermata; gli snapshot già usati non cambiano.
- Contesto non riducibile nel budget: errore esplicito; nessun taglio nascosto degli originali o dell'analisi validata.
- Guasto in analisi o generazione: l'ultimo risultato riuscito rimane consultabile; nessun falso successo.
- Il tempo umano fra anteprima e conferma non consuma la deadline dei workflow AI.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Accesso riservato a PM autorizzati con account locali; tutti i PM vedono tutti i progetti secondo D03/D12. Nessuna ACL per singolo progetto o UI Admin richiesta.
- **FR-002**: Il PM DEVE creare e riaprire progetti con nome non vuoto e descrizione facoltativa, distinguibili anche se omonimi; cataloghi clienti/prodotti non sono prerequisiti.
- **FR-003**: La chat DEVE conservare originali user/assistant con ruolo, autore, ordine e data/ora, associando ogni risposta al proprio trigger e distinguendo contenuti AI non validati.
- **FR-004**: Il testo PM DEVE essere salvato immediatamente in PostgreSQL prima dell'inferenza, indipendentemente dal modello. Salvataggi falliti non DEVONO apparire riusciti; storico e risultati salvati DEVONO sopravvivere a sessioni e riavvii.
- **FR-005**: La chat può produrre risposte e chiarimenti, ma NON DEVE consolidare requisiti, modificare anteprime o rigenerare documenti. Lettura/riapertura non avviano nuove elaborazioni. Analizza e Genera documento sono comandi espliciti distinti.
- **FR-006**: Analizza DEVE richiedere almeno un messaggio PM salvato e produrre esclusivamente un'analisi strutturata con anteprima, senza generare Markdown.
- **FR-007**: Ogni elaborazione DEVE avere un input identificabile e immutabile: cutoff del dialogo per chat/analisi, revisione confermata per generazione. Messaggi successivi non entrano retroattivamente nel run. Una sola elaborazione formale attiva per progetto.
- **FR-008**: Turni chat, analisi e generazione DEVONO avere stato persistente e risultato/errore distinguibili. L'attesa della verifica PM DEVE essere visibile e distinta da una elaborazione AI in corso.
- **FR-009**: Il JSON di analisi DEVE avere schema minimo tipizzato/versionato, validato prima della presentazione o dell'uso documentale, rappresentando obiettivi, contesto/perimetro, requisiti, flussi rilevanti, vincoli/dipendenze, assunzioni, questioni aperte, conflitti, evidenze e stato di validazione.
- **FR-010**: Fatti e requisiti DEVONO conservare riferimenti a messaggi PM o modifiche PM attribuite. Domande/risposte AI possono chiarire il dialogo ma non essere sole evidenze fattuali. Negazioni ed esclusioni non DEVONO essere invertite.
- **FR-011**: Dichiarazioni, deduzioni e assunzioni DEVONO restare distinguibili. Conflitti conservano alternative ed evidenze senza risoluzione automatica. Conferma PM e bozza NON equivalgono ad approvazione cliente o pubblicazione.
- **FR-012**: L'anteprima DEVE essere leggibile e modificabile tramite UI semplice: correzioni a testi/requisiti, integrazioni, assunzioni e risoluzione motivata o rinvio dei conflitti. Le modifiche DEVONO persistere come revisioni attribuite, distinte da originali e risultato AI.
- **FR-013**: Nuovi messaggi PM dopo il cutoff DEVONO essere segnalati senza aggiornare analisi/bozza. Le sole risposte assistant non contano come nuove note PM. Nuova analisi soltanto su richiesta.
- **FR-014**: Guasti di chat, analisi, compattazione o generazione DEVONO produrre esiti espliciti senza perdita di originali o risultati riusciti. Un errore correttamente gestito NON costituisce successo del caso nominale.
- **FR-015**: Dati, revisioni e run DEVONO appartenere al progetto corretto; nessun recupero automatico da KB, web o altri progetti nella slice.
- **FR-016**: Solo LLM interni autorizzati DEVONO elaborare contenuti e sintesi; niente fallback esterno. Input non fidati non alterano istruzioni/policy. Niente stime AI di ore, costi, date o risorse.
- **FR-017**: La compattazione nativa MAF DEVE essere presente nella prima slice, con budget configurabile coerente col modello. Originali PostgreSQL e revisioni confermate non vengono sostituiti da sintesi; attivazione reale, rispetto del budget e fallimenti espliciti DEVONO avere verifiche dedicate.
- **FR-018**: ConversationalAgent DEVE rispondere pertinemente e, se necessario, porre al massimo una domanda mirata per turno, usando il contesto ammesso e senza ripetere chiarimenti già risolti.
- **FR-019**: I turni chat DEVONO essere FIFO, con un solo turno attivo per progetto e cutoff rigoroso. Una sessione più avanzata DEVE essere ricostruita dallo storico ammesso; replay non duplica messaggi o risposte pubblicate.
- **FR-020**: ChatTurn interrotti DEVONO avere recovery automatico controllato con lease rinnovabile, fencing e tentativi limitati/configurabili. Retry dopo errore transitorio è distinto dal recovery dopo crash; esaurimento tentativi produce Failed. Checkpoint MAF validi vengono usati quando disponibili; una chiamata non riprendibile può ripetersi senza duplicare l'esito.
- **FR-021**: Solo il PM DEVE confermare esplicitamente la revisione corrente dell'anteprima. Dopo conferma lo snapshot è immutabile; ogni modifica successiva crea una nuova revisione non confermata. Conferma/modifica su base obsoleta devono segnalare conflitto.
- **FR-022**: Genera documento DEVE essere disponibile soltanto per la revisione corrente confermata e richiedere un comando esplicito. Il documento usa esattamente quella revisione, anche se nel frattempo vengono create altre revisioni.
- **FR-023**: La bozza Markdown DEVE essere persistente, associata alla revisione confermata e sempre Proposed/NotApproved. Recovery/retry non DEVONO duplicarla; un fallimento non rimuove l'ultima bozza riuscita.
- **FR-024**: La bozza DEVE essere completa rispetto alle informazioni applicabili, chiara per un tecnico estraneo alla chat, precisa, documentata e senza ripetizioni. Struttura e lunghezza sono adattive: sintesi/obiettivi, contesto/perimetro, requisiti, flussi, vincoli/dipendenze/integrazioni, assunzioni/punti aperti; omettere sezioni non pertinenti. Le 2–5 pagine sono soltanto orientative.
- **FR-025**: L'attesa di revisione/conferma e lo stato della revisione DEVONO persistere senza un processo o browser aperto. L'avvio della generazione dopo conferma non deve dipendere da un workflow sospeso soltanto in memoria.

- **FR-026**: La UI DEVE adottare un design system minimale Modern Enterprise SaaS: tema chiaro, colori sobri e tipografia leggibile; desktop-first e responsive. Sidebar progetti, chat centrale e viste di anteprima/documento rendono evidente il percorso Chat → Analizza → Anteprima modificabile → Validazione PM → Genera documento. Pochi pulsanti e una sola azione primaria per fase; azioni non disponibili hanno una spiegazione. Su schermi piccoli la sidebar è richiudibile e le viste si dispongono su una colonna.
- **FR-027**: Le risposte conversazionali DEVONO essere mostrate progressivamente tramite streaming MAF, distinguendo il testo provvisorio dall'esito persistito. Spinner indeterminati e stati distinguono attesa, elaborazione, verifica PM, successo ed errore; analisi e generazione mostrano solo fasi reali, senza thinking grezzo, percentuali o progressi inventati. Errori chiari indicano il prossimo passo e consentono il retry esplicito dove previsto, senza duplicare invii. Animazioni minimali; una perdita dello stream non equivale a fallimento del lavoro.
- **FR-028**: WCAG 2.2 AA è l'obiettivo di accessibilità della slice: navigazione completa da tastiera, focus visibile e non occultato, etichette dei campi, errori associati, contrasto adeguato, target utilizzabili e annunci accessibili di stato. Non affidarsi soltanto al colore; rispettare la preferenza di movimento ridotto ed evitare annunci di ogni token. Prevedere test browser E2E dei percorsi principali, inclusi blocco della generazione senza conferma, errori e riconnessione.

### Scope e continuità con il prodotto approvato

Fonti: [baseline funzionale](../../AI-Project-Documentation-MVP-Plan.md),
[Constitution v1.0.0](../../.specify/memory/constitution.md) e decisioni correnti F-01–F-07.
Il nuovo passaggio di anteprima/conferma e il comando separato sostituiscono soltanto
il flusso precedente; requisiti di riservatezza e perimetro complessivo restano validi.

Inclusi: progetto minimo, chat MAF, analisi JSON, UI semplice di revisione/conferma,
generazione Markdown esplicita e consultazione, persistenza/recovery e compattazione.
La UI di anteprima è un form per i dati di questa analisi, non un editor documentale
o un sistema generalista di requirements management. Nessuna approvazione cliente.
Restano fuori: cataloghi, allegati, KB, ingestion, ricerca web, editor avanzato,
export multipli, review AI autonoma, pubblicazioni e amministrazione estesa.

### Evoluzione prevista

KnowledgeAgent MAF potrà essere richiamato come tool dagli altri agenti e usare
TextSearchProvider/function tools per KB progetto, cliente, prodotti/versioni e
fonti web pubbliche autorizzate. Restituirà evidenze con citazioni, provenienza,
versioni, conflitti e lacune. Docling interverrà nell'ingestion per parsing e
chunking prima dell'indicizzazione. ReviewAgent, tool Jira/GitHub e workflow
multiagente restano futuri; nessun adapter placeholder nella slice.
La compattazione nativa e la conferma PM semplice sono già correnti: soltanto
gestioni avanzate e pubblicazioni human-in-the-loop sono future.

### Key Entities *(include if feature involves data)*

- **Progetto e conversazione**: contenitore del dialogo originale e dei risultati.
- **Messaggio e ChatTurn**: evidenza append-only e richiesta conversazionale con cutoff, stato e risposta.
- **Elaborazione formale**: richiesta di analisi oppure generazione, con input congelato, stato e checkpoint MAF.
- **Revisione di analisi**: JSON versionato con origine AI/PM, provenienza e conferma PM; confermata diventa immutabile.
- **Bozza**: Markdown proposto, collegato alla revisione confermata e al run generativo.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Due PM completano indipendentemente il percorso nominale con cinque messaggi preparati, una correzione d'anteprima, conferma e generazione, senza assistenza tecnica; al massimo cinque minuti di interazione attiva, escluse attese AI e preparazione delle informazioni.
- **SC-002**: Nei cinque casi nominali preparati (frammenti, negazioni, informazioni mancanti, conflitti, progetti distinti), con servizio disponibile e senza altro carico, il 100% delle analisi produce un'anteprima valida e il 100% delle generazioni richieste dopo conferma produce una bozza. Ciascuna fase AI termina entro 180 secondi dal proprio comando. Un errore o timeout nel caso nominale è una prova non superata.
- **SC-003**: Tutti i requisiti, vincoli, esclusioni e decisioni applicabili annotati nelle attese del caso sono riportati correttamente nell'analisi/bozza; le vere lacune e alternative restano esplicite. Nessuna informazione disponibile può essere omessa o sostituita genericamente da un punto aperto per soddisfare il criterio.
- **SC-004**: In tre sessioni con riavvio, il 100% degli originali e dei risultati salvati resta identico; anteprima e conferme sono recuperabili. I run pendenti hanno esiti tracciabili e gli esiti completati non vengono duplicati.
- **SC-005**: Nessun turno chat o riapertura avvia un'analisi formale; nessuna analisi o conferma avvia automaticamente la generazione. Tutte le richieste senza revisione corrente confermata sono respinte prima di invocare DocumentAgent.
- **SC-006**: In ogni caso nominale un tecnico non presente al dialogo ricostruisce correttamente obiettivi, perimetro, flussi applicabili, requisiti/vincoli e punti aperti usando soltanto la bozza. La revisione rileva zero omissioni sostanziali, contraddizioni introdotte o ripetizioni sostanziali; entrambi i PM la giudicano utile come base di revisione.
- **SC-007**: Nelle prove di guasto dedicate a chat, analisi, compattazione e generazione, ogni esito è un errore esplicito entro la deadline configurata o alla prima ripresa utile dopo indisponibilità; originali, conferme e ultimo risultato riuscito rimangono intatti. Queste prove non sostituiscono SC-002.
- **SC-008**: Nei cinque scambi chat nominali, a servizio disponibile e senza altra coda, tutte le risposte riescono entro 180 secondi ciascuna, con chiarimenti pertinenti e al massimo uno per turno; nessuna domanda già risolta viene ripetuta. Nei casi FIFO/cutoff non compare alcun messaggio successivo nell'input ammesso.
- **SC-009**: Le prove di recovery/retry ai confini di commit producono un solo messaggio/risultato per richiesta logica; un vecchio worker non può persistere dopo perdita del lease. Raggiunti i limiti, il run è Failed e nessun tentativo ulteriore viene avviato implicitamente.
- **SC-010**: In prove dedicate della chat e dell'analisi la soglia tecnica ridotta attiva realmente la compattazione MAF; nella generazione il payload confermato è verificato integralmente contro il budget senza ridurlo. Il contesto finale è entro il budget di input; originali e snapshot confermati sono invariati, source IDs e vincoli delle attese sono conservati. Una compattazione fallita o fuori budget è un caso di errore esplicito, non un successo.
- **SC-011**: In tutte le prove di modifica/conferma/generazione, la bozza è riconducibile a una sola revisione confermata immutabile. Modifiche successive non la cambiano e una nuova generazione richiede nuova conferma. Le due operazioni su revisione obsoleta producono conflitto senza perdita di dati.

- **SC-012**: I percorsi browser E2E coprono creazione/riapertura progetto, chat in streaming, analisi, modifica e conferma, generazione e lettura bozza; coprono separatamente errore, blocco senza conferma e riconnessione senza duplicazioni. Le prove responsive a 1440/390 pixel e da tastiera verificano gli scenari 23–25. I controlli automatici di accessibilità sono integrati con verifica manuale di focus, contrasto e annunci; nessuna conformità è dichiarata prima delle prove.

## Assumptions

- Interfaccia browser e contenuti in italiano; account PM e un endpoint LLM interno sono predisposti per l'accettazione.
- Una conversazione per progetto; messaggi originali append-only. Nessuna collaborazione realtime, ma revisioni concorrenti sono protette.
- La verifica dell'anteprima riguarda i dati di questa analisi; conflitti aperti e assunzioni possono essere confermati come tali, senza diventare fatti certi.
- Non esiste una soglia di qualità o capacità basata sulle parole. Conversazioni lunghe sono provate separatamente mediante token e finestra del modello, anche oltre la soglia di compattazione.
- Tempi e criteri sopra sono obiettivi di accettazione da verificare, non prestazioni già misurate né sostituzione del pilota completo.
- Il companion storico di task citato dalla baseline non viene ricostruito. Questa fase aggiorna solo gli artefatti esistenti e non genera tasks.md.
