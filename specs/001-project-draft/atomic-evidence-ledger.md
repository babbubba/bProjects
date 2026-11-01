# bProjects — Registro Atomico delle Evidenze (Atomic Evidence Ledger)

> **Documento di progetto e mandato di implementazione**  
> Stato: **da implementare, non PoC**  
> Repository: `babbubba/bProjects`  
> Percorso di destinazione: `specs/001-project-draft/atomic-evidence-ledger.md`  
> Branch di integrazione: `integration/requirements-quality-t052`  
> Data di riferimento: 31 ottobre 2026  
> Ambito: estensione permanente della prima slice e base riutilizzabile per funzioni future.

## 1. Obiettivo e motivazione

bProjects deve conservare **non soltanto il testo originale dei messaggi del project manager**, ma anche le informazioni elementari che tali messaggi esprimono, in forma strutturata, interrogabile, verificabile, storicizzata e rianalizzabile.

Oggi il backend conserva la conversazione originale in PostgreSQL e, al comando **Analizza**, produce `AnalysisContent 1.0.0` tramite un consolidamento del dialogo. Le prove MafReal hanno evidenziato omissioni e alterazioni semantiche: canali di notifica generalizzati impropriamente, strumenti separati dalle azioni, vincoli/alternative confusi, promemoria dimenticati e informazioni che possono sparire dopo la compattazione.

La soluzione richiesta è un componente strutturale di prodotto, non un esperimento su un ramo separato. Si introduce il **Registro Atomico delle Evidenze** (di seguito *registro*), che permette di:

- acquisire le evidenze da ogni messaggio PM, mantenendo la provenienza esatta;
- conservare fatti, qualificatori, negazioni e relazioni senza doverli ricostruire integralmente a ogni analisi;
- riconciliare messaggi successivi, rettifiche, alternative e conflitti;
- alimentare realmente il workflow di analisi esistente;
- rianalizzare in futuro i messaggi originali usando versioni differenti di modello, prompt e schema;
- confrontare le versioni e verificare quali informazioni sono rappresentate in analisi, revisione PM e documento.

**Principio fondamentale:** l'originale immutabile scritto dal PM è l'autorità fattuale; le evidenze estratte sono interpretazioni strutturate e verificabili, **non verità certificate**. Il registro riduce e rende diagnosticabili gli errori dell'AI, ma non presume che la prima estrazione sia infallibile.

## 2. Architettura e confini di integrazione

```text
ConversationMessage PM (originale append-only)
        |
        +--> risposta conversazionale esistente (non bloccata dall'estrazione)
        |
        +--> richiesta durevole di estrazione
                    |
                    v
             Estrattore AI (MAF)
                    |
                    v
             Validazione strutturale e delle fonti
                    |
                    v
             Registro atomico (PostgreSQL/JSONB)
                    |
                    +--> storico / rianalisi / diff / audit
                    |
                    v
             Riconciliazione delle evidenze
                    |
                    v
             AnalysisWorkflow MAF esistente
                    |
                    v
             AnalysisContent 1.0.0
                    |
                    v
             Correzione e conferma PM
                    |
                    v
             DocumentAgent --> Markdown
```

### 2.1 Elementi esistenti da preservare

- Backend ASP.NET Core/.NET 10, EF Core 10, PostgreSQL 18, Angular 22, Docker Compose.
- Microsoft Agent Framework .NET 1.26.0 come unico framework agentico/orchestratore AI.
- `ConversationMessage` e suoi identificativi, ordine di sequenza, cutoff e regole di immutabilità.
- Stati, claim, lease, fencing, idempotenza, retry, checkpoint e recovery già implementati: riutilizzarli **dove compatibili**, senza un secondo sistema di orchestrazione.
- `AnalysisContent 1.0.0`, `AnalysisRevision`, provenienza `Message`/`PmRevision`, conferma PM e `DocumentAgent`.
- Distinzione tra chat conversazionale, analisi esplicita, conferma PM e generazione esplicita del documento.
- Controlli di sicurezza, isolamento tra progetti, configurazione per fase e provider LLM esclusivamente interno.

Non si richiedono in questa iniziativa RAG, Docling, Qdrant, web search, broker, una nuova applicazione o un secondo workflow engine. Il registro deve essere **riutilizzabile in futuro**, senza implementare anticipatamente integrazioni non richieste.

### 2.2 Invarianti non negoziabili

1. Il POST del messaggio PM continua a essere transazionale e a funzionare anche con il modello offline: l'estrazione è **successiva e durevole**, non una dipendenza sincrona del commit originale.
2. Nessun messaggio assistant può diventare l'unica prova fattuale di un requisito o di una decisione PM.
3. Nessun dato con `Sequence` superiore al cutoff del run può entrare nell'estrazione o nella riconciliazione utilizzata da quel run.
4. Un'estrazione fallita non equivale a un messaggio senza evidenze. Il caso *zero evidenze* è un risultato esplicito, validato e distinto da errore, omissione presunta o lavoro incompleto.
5. Una rianalisi non modifica retroattivamente messaggi, estrazioni storiche, analisi già prodotte, revisioni confermate o documenti.
6. Le decisioni non confermate non diventano vincoli dichiarati per effetto della sola riconciliazione.
7. Il registro deve essere **consumato effettivamente** dal consolidamento, non soltanto scritto nel database.
8. Non si dichiarano PASS test mai eseguiti; non si modificano le attese N1–N5 per adattarle all'output del modello.

## 3. Modello concettuale delle evidenze

Lo schema esatto è responsabilità dell'implementazione, ma deve essere **tipizzato, versionato e validato**, con un nucleo minimo stabilito qui.

| Dimensione | Contenuto richiesto |
| --- | --- |
| Identità | ID evidenza, ID versione estrazione, ID progetto/conversazione/messaggio, schema version |
| Origine | `MessageId`, sequenza, ruolo `user`, estratto letterale e posizione nel testo originale |
| Tipologia | Obiettivo, contesto, requisito, azione/flusso, vincolo, esclusione, dipendenza/integrazione, assunzione, lacuna, alternativa, decisione, o categoria motivata |
| Struttura semantica | Soggetto/attore, azione, oggetto, ambito, scopo, strumento/canale e destinatario se presenti |
| Qualificatori | Quantità, unità, scadenza, durata, frequenza, minimo/massimo, condizioni, eccezioni, obbligatorietà/facoltatività |
| Polarità | Affermazione, negazione, esclusione, divieto o situazione non definita senza inversioni di significato |
| Certezza/provenienza epistemica | Dichiarato dal PM, inferito, ipotizzato, da chiarire; mai promozione automatica |
| Relazioni | Qualifica, conferma, contraddice, rettifica, sostituisce, dipende da, alternativa a, appartiene alla medesima regola |
| Diagnostica | Versione modello, prompt, schema, parametri effettivi, stato estrazione, motivi di rifiuto/validazione |

Il modello deve rappresentare **più evidenze per messaggio** e, quando indispensabile, **più fonti/spans per una singola evidenza**. I campi assenti non vanno inventati o completati con valori generici. Gli ID devono essere stabili nell'ambito di ciascuna versione, distinguendo identità della versione e possibili corrispondenze fra versioni.

### 3.1 Ancoraggio al testo originale

- Ogni evidenza dichiarata deve riferirsi a uno o più passaggi realmente presenti negli originali ammessi.
- Definire una convenzione univoca per gli offset (consigliata: indici UTF-16, `start` incluso / `end` escluso, coerenti fra .NET e TypeScript) e validarla confrontando la sottostringa con la citazione.
- Se un messaggio corregge implicitamente qualcosa detto prima, separare il passaggio che contiene la rettifica dalle fonti di contesto che ne chiariscono il referente; conservare i due ruoli.
- Non basta verificare parole chiave: bisogna preservare la relazione fra soggetto, predicato, oggetto e condizioni.
- Vietare citazioni inventate, fonti assistant spacciate per user, riferimenti fuori progetto o oltre cutoff e `MessageId` non esistenti.

### 3.2 Esempi di comportamento richiesto

**N2 — negazioni/canali**. «Niente notifiche push, solo email» delimita i **canali delle notifiche**, non tutte le comunicazioni: estrarre divieto push e vincolo email preservando il medesimo ambito.

**N3 — strumento collegato all'azione**. «Gli addetti registrano carichi e scarichi usando il lettore di codici a barre»: il lettore deve rimanere collegato alla registrazione di carichi/scarichi e all'attore corretto.

**N4 — alternative irrisolte**. Tempi e modalità di autenticazione proposti da soggetti diversi non diventano simultaneamente requisiti definitivi. Conservarli come posizioni alternative con fonti distinte e conflitto aperto.

**N1 — informazioni multiple**. «Conferma via email e promemoria il giorno prima»: le due funzioni e i relativi tempi/canali devono risultare verificabili separatamente, senza perdere il significato della frase originale.

Questi scenari sono esempi di regressione, **non regole specifiche nel codice**.

## 4. Persistenza, versioni e consistenza

### 4.1 Struttura dati

Utilizzare PostgreSQL e EF Core, con documenti `jsonb` per le estrazioni e metadati relazionali indicizzati per lookup, appartenenza, stato, ordinamento e versionamento. Evitare sia una mega-tabella senza vincoli sia una tabella per ogni campo semantico.

Il modello deve consentire di ricostruire:

- quale messaggio originale ha prodotto ciascuna evidenza;
- quale versione dell'estrattore/modello/schema l'ha generata;
- quando e con quale esito è stata generata;
- quali evidenze e **quali versioni** sono state selezionate per uno specifico run di analisi;
- quali rettifiche, relazioni e confronti sono stati successivamente prodotti;
- quali errori o estrazioni incomplete impediscono l'uso del registro.

### 4.2 Versioni e promozione

- Le versioni di estrazione sono **append-only**; nessuna cancellazione o aggiornamento distruttivo della precedente versione conclusa.
- La prima estrazione valida può diventare la versione corrente del messaggio secondo una regola documentata.
- Una rianalisi successiva produce una **versione candidata**, da confrontare. Non promuoverla implicitamente per cambiare il significato del registro corrente di progetti già utilizzati.
- La promozione deve essere un'operazione esplicita, autorizzata, atomica e tracciata; i run precedenti continuano a referenziare le versioni originali.
- Un run Analizza deve fissare durevolmente la selezione di versioni applicabile al cutoff, per garantire retry/resume deterministici anche se nel frattempo parte una rianalisi.

### 4.3 Idempotenza e concorrenza

Gestire doppio invio, retry, riavvio, worker concorrenti, lease scaduti e vecchi worker che terminano tardi. Nessun commit valido da fencing obsoleto. Nessuna contaminazione cross-project. Migrazioni incrementali e test su PostgreSQL reale.

## 5. Estrattore incrementale e scheduling

1. Il commit del nuovo `ConversationMessage` PM resta indipendente dalla disponibilità AI.
2. Registrare durevolmente il lavoro da eseguire nello stesso confine transazionale corretto o attraverso un meccanismo di recupero deterministico che non possa perdere messaggi dopo il commit.
3. Elaborare l'estrazione con il modello interno, tramite MAF e output strutturato validato.
4. Persistire una versione valida o un errore esplicito; registrare tentativi, scadenze, stato e informazioni diagnostiche utili.
5. Distinguere `Pending`, `Running`, `Completed`, `Failed` (o stati equivalenti già esistenti), e il caso `Completed` con zero evidenze.
6. Prevedere operazioni di retry controllato e recupero dopo crash, senza invocazioni infinite.

**Contesto:** l'estrazione è preferibilmente focalizzata sul nuovo messaggio, con accesso mirato alle evidenze e ai messaggi precedenti pertinenti quando occorre risolvere anafore o rettifiche («intendevo 10, non 5»). Non usare informazioni future né risposte assistant come fatti PM.

**Concorrenza:** il DGX attualmente lavora con `parallel=1`. Definire una politica realistica per priorità, serializzazione e backpressure; l'estrazione automatica non deve saturare il modello a scapito della chat. Non generare code infinite invisibili.

**Configurazione:** modello, enable_thinking, output, context window, temperatura, timeout, prompt e schema dell'estrazione vanno risolti e tracciati separatamente. Non presumere che il modello per l'analisi monolitica sia automaticamente quello ottimale per l'estrazione.

**Prestazioni:** preservare i criteri correnti e riportare il costo totale del percorso, incluso il tempo per rendere disponibili le evidenze necessarie. Nessun escamotage che escluda attese reali dal criterio nominale di 180 secondi.

## 6. Riconciliazione strutturata

Separare estrazione del singolo messaggio e riconciliazione fra messaggi:

- duplicati semantici e conferme, senza eliminare le fonti;
- condizioni aggiunte e qualificatori affinati;
- rettifiche esplicite di quantità, date, ruoli o decisioni;
- alternative incompatibili e conflitti irrisolti;
- esclusioni e negazioni;
- risoluzioni esplicitamente attribuite al PM.

Persistire le relazioni e il loro stato/provenienza; distinguere sempre relazione inferita dall'AI da decisione dichiarata. Evitare una sostituzione silenziosa del fatto più vecchio con il più recente. Le nuove relazioni non riscrivono le vecchie estrazioni.

## 7. Integrazione con l'AnalysisWorkflow esistente

Il flusso operativo deve consumare il registro come input strutturato del consolidamento, mantenendo MAF e l'attuale contratto esterno `AnalysisContent 1.0.0`.

1. `Analizza` acquisisce il cutoff immutabile secondo le regole attuali.
2. Il backend verifica che per ogni messaggio PM entro il cutoff sia disponibile una versione di estrazione valida e coerente con la selezione del run.
3. Se ci sono estrazioni mancanti o fallite, segnala lo stato in modo recuperabile: non produce un'analisi parziale silenziosa e non lascia un run bloccato indefinitamente. Progettare la gestione in modo coerente con coda, deadline e API esistenti.
4. Si produce una vista deterministica e tracciabile delle evidenze e delle relazioni selezionate.
5. Il workflow MAF consolida tale vista in `AnalysisContent`, senza dover ricostruire da zero la semantica dalla history narrativa.
6. La validazione verifica struttura, fonti, provenienza e regole di dominio prima del commit; ulteriori controlli di copertura confrontano le evidenze selezionate con l'output.
7. Il PM può ancora modificare, confermare e generare come ora; revisione e bozza mantengono i loro riferimenti persistenti.

**Importante:** non riprogettare `AnalysisContent` o l'intera UI senza necessità. Se servono collegamenti dai suoi elementi agli ID atomici, introdurre una mappa/versione interna persistita e auditabile, anziché rompere subito il contratto pubblico e i dati esistenti.

**Recovery:** congelare le versioni delle evidenze usate da ciascun run per garantire che un resume MAF non cambi input dopo una rianalisi successiva. Se cambia la definizione del workflow, applicare correttamente la compatibilità/versione dei checkpoint, senza riprendere checkpoint incompatibili.

## 8. Compattazione e lunghe conversazioni

La compattazione nativa MAF resta. Il registro è indipendente dagli snapshot della history compattata e conserva gli originali delle evidenze strutturate.

Una sintesi che omette un dettaglio non elimina l'evidenza persistita. La fase di consolidamento deve utilizzare il registro selezionato, non considerare il riassunto conversazionale come unica autorità. Verificare i budget reali dei token per input strutturati voluminosi: paginazione/chunking e consolidamento gerarchico devono essere progettati solo dove necessari, senza tagli arbitrari né duplicare un motore di compattazione.

## 9. Rianalisi storica e confronto versioni — funzionalità di prodotto

### 9.1 Operazioni richieste

- Rianalizzare **un messaggio PM** dal suo testo originale immutabile.
- Rianalizzare **un progetto** (con lavori durevoli, avanzamento reale, retry e isolamento).
- Avviare un **backfill** per i messaggi preesistenti; idempotente, interrotto/ripreso senza duplicazioni.
- Visualizzare tutte le versioni, confrontarne due e promuovere consapevolmente la candidata scelta.
- Ricalcolare le relazioni derivate in modo versionato dopo una promozione, senza modificare gli artefatti storici.
- Se il PM vuole aggiornare il documento, creare **esplicitamente** una nuova analisi/revisione e generazione: mai mutare bozze già confermate/generate.

### 9.2 Diff utile al PM

Il confronto deve distinguere evidenze aggiunte, eliminate dalla nuova estrazione, invariate e modificate (attore, azione, oggetto, polarità, condizioni, date, strumenti, canali, classificazione, fonti, relazioni). Non basta un diff letterale di JSON.

Il matching fra versioni deve essere esplicito e rendere visibili i casi ambigui, senza millantare l'identità di due fatti basandosi soltanto su termini comuni. Gli automatismi di confronto non approvano modifiche a nome del PM.

## 10. Verifica di completezza e provenienza

Implementare un servizio **riutilizzabile, non specifico dei test N1–N5**, che confronti:

```text
Messaggi originali -> evidenze atomiche -> AnalysisContent
                 -> revisione PM -> documento Markdown
```

Il servizio deve segnalare almeno: evidenza non rappresentata, fonte assente/non valida, qualificatore modificato, negazione invertita, strumento separato dall'operazione, conflitto aperto presentato come scelta, requisito della revisione confermata assente nel documento.

Distinguere nel report:

- **Errore deterministico:** ID, fonte, span, versione, stato, integrità, relazione o regola formalmente verificabile.
- **Rischio semantico stimato:** confronto che richiede interpretazione di linguaggio naturale, con output AI, evidenze e incertezza espliciti.
- **Valutazione PM:** conferma o correzione umana; non deve essere simulata come validazione automatica.

Il registro non autorizza la modifica silenziosa di analisi o documento per far passare i controlli. Le verifiche e i relativi artefatti devono essere versionati, recuperabili e associati a input esatti.

## 11. API, autorizzazioni e frontend

Riutilizzare autenticazione, autorizzazione di progetto e convenzioni REST esistenti.

Richiedere endpoint per:

- stato e lista delle estrazioni/evidenze per progetto e messaggio;
- dettaglio evidenza con fonte originale;
- elenco versioni e confronto fra due versioni;
- rianalisi messaggio/progetto, monitoraggio e retry autorizzato;
- promozione esplicita di una versione, con concorrenza ottimistica;
- avvio/stato backfill;
- esiti della verifica di copertura e provenienza.

Definire precisamente percorsi, payload, codici e autorizzazioni in `contracts/http-api.md` prima di codificare; niente endpoint placeholder.

Angular: introdurre una **vista leggera** delle evidenze nel progetto, coerente con Bootstrap/SCSS, con stato reale delle elaborazioni, filtri essenziali, collegamento al messaggio originale e span, storico, confronto versioni, azioni di rianalisi e risultati di copertura. Nessuna presentazione del reasoning del modello. Accessibilità e responsive come nella slice corrente.

## 12. Test e verifica di accettazione

### 12.1 Prima dei test DGX

Test deterministici unitari, integration PostgreSQL e fake HTTP/MAF per:

- uno o più fatti per messaggio, zero evidenze valido distinto dal fallimento;
- soggetto/azione/oggetto/strumento/finalità, condizioni, quantità, negazioni;
- anafore e rettifiche, fonti multiple e limiti temporali;
- span originali validi; rifiuto di span, ID o ruoli inventati;
- duplicati e conflitti, promozione senza perdita delle versioni precedenti;
- isolamento fra progetti e autorizzazioni;
- idempotenza, backlog, retry, crash, fencing, recovery e timeout;
- rianalisi di un messaggio, progetto intero e backfill storico;
- diff fra versioni e aggiornamento esplicito della versione corrente;
- versioni esatte congelate per ogni analisi anche durante retry/resume;
- integrazione effettiva nel workflow, conferma PM e documento;
- conservazione delle evidenze indipendentemente dalla compattazione;
- REST e UI/E2E, errori, retry, stato e navigazione alle fonti.

### 12.2 DGX reale

Dopo i gate deterministici, verificare la nuova pipeline reale con inferenze **seriali** (DGX `parallel=1`) e diagnostica persistente già sviluppata in `DiagnosticsHarness.cs`.

Riutilizzare N1–N5, senza cambiare le attese per ottenere PASS. Per ogni caso registrare tempi end-to-end e per fase, token, modello/parametri reali, estrazioni mancanti, copertura registro→analisi→documento, errori e versioni selezionate.

Conservare FAIL e SKIP reali. La verifica tecnica non sostituisce la valutazione umana di due PM e tecnico estraneo, tuttora richiesta da T052. Segnalare apertamente gli eventuali trade-off tra estrazione incrementale e limite nominale di 180 secondi; non modificare unilateralmente la soglia.

## 13. Spec Kit, task e disciplina di implementazione

Prima di scrivere codice, allineare **in modo coerente e non distruttivo**:

- `specs/001-project-draft/spec.md`;
- `plan.md`, `data-model.md` e `research.md` dove necessario;
- `contracts/ai-workflow.md` e `contracts/http-api.md`;
- `tasks.md` con **nuovi task identificati e dipendenze** per registro, estrazione, riconciliazione, rianalisi, copertura, API/UI e test;
- checklist canonica, quickstart e `HANDOFF.md`.

Preservare la cronologia e le attività T001–T051 già chiuse. T052/T053 restano aperti finché non soddisfano i rispettivi criteri. Non rigenerare distruttivamente `tasks.md` e non introdurre nuove ADR se il piano può documentare adeguatamente le decisioni.

La progettazione non è una fase terminale: **dopo l'allineamento Spec Kit, implementare davvero la funzionalità**, con migrazioni, codice backend, integrazione MAF, API, Angular, suite di test e documentazione.

### Milestone operative

1. Contratti, schema atomico, relazioni, selezione versioni e aggiornamento Spec Kit.
2. Persistenza, migrazioni e servizi di lettura/scrittura/versionamento.
3. Estrattore MAF incrementale, coda durevole, scheduling, recupero e telemetria.
4. Riconciliazione e integrazione reale nel consolidamento.
5. Rianalisi, confronto versioni, promozione e backfill.
6. Verifica di copertura/provenienza.
7. API, UI e controlli di sicurezza.
8. Test completi, verifiche DGX, documentazione ed esiti residui.

Concludere ciascuna milestone con test pertinenti e commit coerenti. `HANDOFF.md` deve registrare modifiche, decisioni, run, esiti, anomalie e prossimo passo. Usare ECC e subagent isolati quando utili; non saturare il contesto principale con log o test meccanici.

## 14. Git e vincoli operativi

- Repository di lavoro: `C:\source\repos\babbubba\bProjects-clean`.
- Branch **esistente**: `integration/requirements-quality-t052`; **non creare** un nuovo branch o worktree.
- Verificare prima di iniziare `git status`, HEAD e modifiche locali; non cancellare lavoro altrui.
- Commit piccoli e atomici, **senza `Co-Authored-By`**; push sul medesimo branch; nessun merge automatico della PR #3.
- Per shell usate per autenticarsi con GitHub, `GITHUB_TOKEN`/`GH_TOKEN` d'ambiente possono imporre l'account aziendale: rimuoverli nella shell del push e verificare l'identità GitHub effettiva `babbubba`. Non cambiare `git user.name` o remote per aggirare un 403. Se il push non riesce, documentare l'errore e proseguire lo sviluppo locale senza perdere commit.
- Nessun contenuto di conversazioni o output LLM con dati riservati in Git. Diagnostica locale esclusa e sanificata, niente credenziali né `reasoning_content`.

## 15. Definition of Done

La funzionalità è completata **soltanto quando**:

- ogni messaggio PM genera in modo durevole un lavoro di estrazione senza bloccare il salvataggio del messaggio;
- ogni estrazione valida è tipizzata, persistita, referenziata e storicizzata; gli errori sono distinguibili dall'assenza di evidenze;
- il consolidamento consuma realmente le versioni del registro selezionate per il run;
- riconciliazione e conflitti non sovrascrivono né falsificano fatti PM;
- rianalisi di messaggio e progetto, diff, promozione e backfill sono operativi;
- la provenienza è consultabile dal messaggio originale fino ad analisi e documento, con limiti dei controlli semantici espliciti;
- API e frontend sono utilizzabili e autorizzati;
- retry, crash, cutoff, isolamento, idempotenza e ripresa sono verificati;
- il percorso Chat → Analizza → Correggi → Conferma → Genera continua a funzionare;
- test e documentazione riportano fedelmente PASS, FAIL e SKIP, senza dichiarare chiusi T052/T053 in assenza dei gate richiesti;
- nessun modulo finto, test hardcoded per N1–N5 o infrastruttura superflua rimane come soluzione definitiva.

**Esito atteso:** un registro durevole e riutilizzabile, che renda le informazioni fornite dal PM persistenti, confrontabili e rianalizzabili nel tempo, migliorando la verificabilità del consolidamento AI senza sostituire la revisione umana.
