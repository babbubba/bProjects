# Contratto AI e workflow MAF — analisi con anteprima

**Updated**: 2026-10-28. Baseline MAF .NET 1.26.0.
API e fonti verificate: [research.md](../research.md). Dati: [data-model.md](../data-model.md).
Questa revisione sostituisce il precedente grafo unico Analizza → DocumentAgent.

## 1. Turno conversazionale, FIFO e cutoff — F-01

Il commit PM assegna Sequence, salva l'originale e crea ChatTurn Queued nella stessa
transazione PostgreSQL. SourceCutoffSequence è la sequenza del trigger appena salvato.
Il turno legge user/assistant con Sequence <= cutoff; il trigger è escluso dalla
history e passato una sola volta come input. Non sono ammessi dati successivi,
nemmeno se presenti in una sintesi o nella sessione ripristinata.

La FIFO è ordinata per sequenza del trigger; un turno precedente non terminale,
anche in backoff, impedisce il claim del successivo nella stessa conversazione.
Una sola conversazione per progetto e un solo ChatTurn con lease valido per progetto.

Conversation conserva solo l'ultimo snapshot derivato AgentSession con
ContextThroughSequence, MafVersion, ChatConfigVersion e revisione di commit.
ContextThroughSequence copre anche la risposta assistant incorporata nella sessione.
Il provider conserva nello stato MAF soltanto i riferimenti ordinati agli originali
usati e alla risposta committata. Il riuso richiede che tali riferimenti siano un
prefisso nello stesso ordine della nuova history ammessa, oltre a versione/configurazione
compatibili. Una semplice disuguaglianza del watermark non basta a provare compatibilità.
Se il watermark supera il cutoff, la condizione di prefisso fallisce o configurazione/provenienza
sono incompatibili, NON si deserializza tale contesto: CreateSessionAsync crea una
sessione compatibile e ChatHistoryProvider ricostruisce la vista dagli originali
ammessi. Altrimenti DeserializeSessionAsync può riusare lo stato derivato compatibile;
il provider rimane l'autorità della history e non duplica l'input.

Esempio normativo: U1(seq=1), U2(seq=2), A1(seq=3). T2 ha cutoff=2 e la sessione
post-T1 ha watermark=3: T2 ricostruisce U1/U2 senza A1 o sintesi di A1.
Dopo T2 lo snapshot unico può avanzare nuovamente; non si archiviano copie complete
dello storico per ogni messaggio. Originali e risposte rimangono append-only.

ConversationalAgent.RunStreamingAsync in modalità dialogo risponde e può porre una domanda
mirata, senza creare elaborazioni formali o modificare revisioni/bozze. La risposta,
il nuovo snapshot serializzato, il watermark e Completed del turno sono committati
atomicamente con controllo di lease, fencing e revisione. Il provider non pubblica
risposte definitive anticipatamente fuori da questo commit. Gli aggiornamenti testuali
MAF possono essere inoltrati come chat.delta SSE provvisori secondo il contratto HTTP:
non sono messaggi persistiti né evidenze utilizzabili da altri turni. Lease/fencing
proteggono emissione e commit; al cambio tentativo il client scarta il buffer precedente
e a Completed carica il messaggio salvato. Nessun evento di reasoning viene esposto.
La disconnessione del browser interrompe soltanto il trasporto, non il lavoro durabile.

## 2. Lease, recovery e retry — F-02

Politica server configurabile, inizialmente: LeaseDuration=30 secondi,
RenewInterval=10 secondi, MaxAttempts=3 (incluso il primo claim),
RetryBaseDelay=2 secondi, RetryMaxDelay=10 secondi, RunDeadline=180 secondi.
Il contatore è persistente e non si azzera al restart. Ogni claim/reclaim incrementa
AttemptCount e FencingToken; scadenze calcolate sul tempo PostgreSQL.
RenewInterval deve essere minore di metà lease; configurazioni incoerenti sono rifiutate.

| Evento | Transizione e responsabilità |
| --- | --- |
| Claim di Queued ammissibile | Running, nuovo owner/fence, AttemptCount + 1 |
| Rinnovo owner corrente | Running; estende LeaseUntil senza spostare DeadlineAt |
| Crash: Running con lease scaduto | Reclaim automatico Running → Running con nuovo owner/fence e tentativo, entro limite/deadline |
| Errore transitorio osservato | Running → Queued, lease rilasciato, NextAttemptAt persistente e backoff |
| Errore permanente, limite o deadline | Failed terminale, originali e risultati precedenti conservati |
| Commit riuscito | Completed terminale e risultato unico |
| Commit di vecchio owner/revisione | Rifiutato senza mutare stato o pubblicare risultato; il proprietario valido continua/reconcilia |

Backoff prima dei claim 2/3: min(RetryMaxDelay, RetryBaseDelay * 2^(AttemptCount-1)),
quindi 2 e 4 secondi con i default. Il reclaim da crash attende il lease, senza
aggiungere il backoff di un errore che non è stato osservato.
Transitori: trasporto interrotto, timeout di rete entro deadline, HTTP 408/429/5xx.
Autorizzazione/configurazione, schema/riferimenti invalidi, budget non rispettabile e
compattazione semanticamente invalida sono permanenti. Un guasto di trasporto durante
la sintesi segue la stessa classificazione, non un riparatore AI.
Retry-After può differire il prossimo tentativo entro la deadline; altrimenti Failed.

Gli ulteriori retry automatici del client HTTP/SDK sono disabilitati nella configurazione
del client: il budget non deve moltiplicarsi in layer diversi. Gli attempt sono
invocazioni logiche: la compattazione nativa può effettuare chiamate interne allo
stesso tentativo, sempre nel medesimo budget temporale e solo verso il modello interno.
Per il turno singolo RunStreamingAsync non è dichiarato un checkpoint workflow intra-chiamata:
si riparte dalla sessione committata compatibile e dal cutoff, ripetendo l'inferenza
se necessario. Non si chiama checkpoint una semplice serializzazione di AgentSession.

Le fasi formali applicano gli stessi limiti di dispatch e recuperano invece l'ultimo
checkpoint MAF valido del proprio ProcessingRun, se disponibile. Nessun checkpoint
di altro cutoff, revisione, definizione o configurazione è riutilizzabile.
Una sola esecuzione AI globale per l'istanza applicativa iniziale; un vecchio LLM
remoto potrebbe terminare dopo la perdita del lease, ma non ha più diritto di commit.
Fencing e unicità proteggono gli effetti, non promettono exactly-once delle inferenze.

Un replay HTTP con la stessa chiave recupera la stessa richiesta, senza nuovo attempt.
Dopo Failed un nuovo comando PM crea una nuova richiesta; la lettura non ritenta.
Non ci sono scheduler per executor, retry per fase custom o un secondo workflow engine.

## 3. Due fasi formali MAF e attesa umana — F-04

Gli ID logici restano stabili all'interno di ciascuna definizione versionata.
Gli agenti hanno AIAgent/ChatClientAgent, prompt e sessioni separati per modalità.

| Definizione | Grafo nativo e output |
| --- | --- |
| project-analysis-v2 | LoadDialog → ConversationalAgent (consolidamento JSON) → ValidatePersistAnalysis; termina con AnalysisRevision Unvalidated e ProcessingRun Completed |
| project-document-v1 | LoadConfirmedRevision → DocumentAgent → ValidatePersistDraft; termina con bozza Proposed/NotApproved e ProcessingRun Completed |

WorkflowBuilder e binding nativi AIAgentHostExecutor gestiscono nodi, messaggi e
sessioni/checkpoint. Ai confini deterministici usare protocollo .NET ChatMessage,
List<ChatMessage> e TurnToken, non DTO arbitrari verso il binding agente.
ForwardIncomingMessages=false evita di scambiare input e output. Buffer di raccordo
appartengono allo stato MAF checkpointato, non a un engine applicativo parallelo.

RequestPort/RequestInfoEvent/SendResponseAsync sono documentati in .NET per HITL,
ma per questa slice NON si mantiene un run sospeso durante la revisione.
La prima fase completa il calcolo; lo stato di attesa è AnalysisRevision Unvalidated
persistita. Il PM può tornare giorni dopo. Conferma e comando Genera documento
autorizzano una nuova fase MAF a input congelato. Nessun lease o deadline attraversa
l'attesa umana; nessuna ripresa dipende da memoria del processo.
Questa separazione di due comandi di dominio non interpreta o riproduce un grafo:
MAF resta l'unico coordinatore delle elaborazioni interne.

ProcessingRun generalizza la precedente entità AnalysisRun con Kind=Analysis/Document:
stessa tabella e stesso dispatcher, senza introdurre una tabella/coda per fase.
Il tipo sceglie soltanto la definizione MAF; stati interni e resume rimangono MAF.
WaitingForPM è uno stato UI derivato dalla revisione corrente Unvalidated, non un run Running.

## 4. Schema normativo minimo AnalysisContent 1.0.0 — F-03

Il seguente JSON Schema definisce nomi, tipi e obbligatorietà. Non è codice applicativo.
I controlli semantici sotto sono parte dello stesso contratto; non si assume che
l'endpoint locale supporti l'intero vocabolario JSON Schema. Il formato nativo usa
la proiezione compatibile del modello; entrambe le modalità passano la stessa
validazione tipizzata e semantica nel backend prima del commit o della visualizzazione.
In JsonSchema la proiezione è costruita per run e inviata invariata nel response_format
(l'adattatore OpenAI di M.E.AI la riscriverebbe per lo strict mode spostando pattern e
format nella description, e llama.cpp non vincolerebbe gli UUID): pattern UUID per gli
id nuovi; analysisId, projectId e sourceCutoffSequence ammettono solo i valori fidati;
id e messageId delle fonti ed evidenceRefs solo gli id dei messaggi fino al cutoff
(una fonte Message prodotta dall'AI ha l'id del proprio messaggio). È un aiuto alla
generazione, non una validazione: le regole sotto restano tutte verificate dal backend.

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "AnalysisContent",
  "type": "object",
  "additionalProperties": false,
  "required": ["schemaVersion", "analysisId", "projectId", "sourceCutoffSequence", "validationState", "sources", "goals", "contextAndScope", "requirements", "operationalFlows", "constraints", "dependencies", "assumptions", "openQuestions", "conflicts"],
  "properties": {
    "schemaVersion": {"const": "1.0.0"},
    "analysisId": {"type": "string", "format": "uuid"},
    "projectId": {"type": "string", "format": "uuid"},
    "sourceCutoffSequence": {"type": "integer", "minimum": 1},
    "validationState": {"enum": ["Unvalidated", "PmConfirmed"]},
    "sources": {"type": "array", "items": {"$ref": "#/$defs/source"}},
    "goals": {"$ref": "#/$defs/items"},
    "contextAndScope": {
      "type": "object", "additionalProperties": false,
      "required": ["overview", "included", "excluded"],
      "properties": {"overview": {"$ref": "#/$defs/items"}, "included": {"$ref": "#/$defs/items"}, "excluded": {"$ref": "#/$defs/items"}}
    },
    "requirements": {"$ref": "#/$defs/items"},
    "operationalFlows": {"$ref": "#/$defs/items"},
    "constraints": {"$ref": "#/$defs/items"},
    "dependencies": {"$ref": "#/$defs/items"},
    "assumptions": {"$ref": "#/$defs/items"},
    "openQuestions": {"$ref": "#/$defs/items"},
    "conflicts": {"type": "array", "items": {"$ref": "#/$defs/conflict"}}
  },
  "$defs": {
    "ids": {"type": "array", "uniqueItems": true, "items": {"type": "string", "format": "uuid"}},
    "items": {"type": "array", "items": {"$ref": "#/$defs/item"}},
    "item": {
      "type": "object", "additionalProperties": false,
      "required": ["id", "text", "classification", "evidenceRefs"],
      "properties": {
        "id": {"type": "string", "format": "uuid"},
        "text": {"type": "string", "minLength": 1},
        "classification": {"enum": ["Declared", "Inferred", "Assumed"]},
        "evidenceRefs": {"$ref": "#/$defs/ids"}
      }
    },
    "source": {
      "type": "object", "additionalProperties": false,
      "required": ["id", "kind", "messageId", "messageRole", "revisionId"],
      "properties": {
        "id": {"type": "string", "format": "uuid"},
        "kind": {"enum": ["Message", "PmRevision"]},
        "messageId": {"type": ["string", "null"], "format": "uuid"},
        "messageRole": {"enum": ["user", "assistant", null]},
        "revisionId": {"type": ["string", "null"], "format": "uuid"}
      }
    },
    "conflict": {
      "type": "object", "additionalProperties": false,
      "required": ["id", "description", "status", "alternatives", "resolution"],
      "properties": {
        "id": {"type": "string", "format": "uuid"},
        "description": {"type": "string", "minLength": 1},
        "status": {"enum": ["Open", "Resolved"]},
        "alternatives": {"type": "array", "minItems": 2, "items": {"$ref": "#/$defs/item"}},
        "resolution": {
          "anyOf": [
            {"type": "null"},
            {"type": "object", "additionalProperties": false,
             "required": ["selectedAlternativeIds", "reason", "evidenceRefs"],
             "properties": {
               "selectedAlternativeIds": {"type": "array", "minItems": 1, "uniqueItems": true, "items": {"type": "string", "format": "uuid"}},
               "reason": {"type": "string", "minLength": 1},
               "evidenceRefs": {"$ref": "#/$defs/ids"}
             }}
          ]
        }
      }
    }
  }
}
```

Regole semantiche obbligatorie:

1. UUID validi, ID di elementi/fonti unici nel payload; text/description/reason non
   whitespace. evidenceRefs e selectedAlternativeIds devono risolversi nel payload.
2. analysisId/projectId/cutoff/schema sono imposti dall'input server affidabile.
   Source Message: messageId presente, revisionId=null, ruolo coerente col messaggio
   dello stesso progetto e Sequence <= cutoff. Source PmRevision: messageId/ruolo=null,
   revisionId della stessa analisi/progetto con paternità PM verificabile (anche la revisione in creazione).
   Le nuove fonti sono attribuite al PM autenticato; quelle storiche conservano il loro autore.
3. L'AI produce soltanto validationState=Unvalidated, fonti Message, classification
   Declared/Inferred/Assumed e conflitti Open con resolution=null.
   Non può inventare fonti PM, conferme o risoluzioni.
4. Affermazioni fattuali Declared e alternative conflittuali richiedono evidenze user
   oppure PmRevision. Assistant è solo contesto; deduzioni/assunzioni rimangono marcate.
   openQuestions e vere informazioni mancanti possono avere evidenceRefs vuoto.
5. Ogni modifica/aggiunta PM significativa riceve una fonte PmRevision assegnata dal
   backend con autore/data della revisione. La classificazione rimane Declared,
   Inferred o Assumed secondo il significato, separata dalla paternità della modifica;
   gli elementi di assumptions sono Assumed. Le fonti originali non vengono mutate;
   versioni precedenti conservano le alternative.
6. Open richiede resolution=null. Resolved richiede scelta fra alternative esistenti,
   motivo non vuoto e riferimento alla modifica PM; l'AI non decide la scelta.
7. Tutti gli array sono obbligatori e possono essere vuoti se non applicabili;
   informazioni davvero mancanti sono esplicite in openQuestions. Nessun riempitivo.
8. validationState vale per l'intero contenuto: PmConfirmed significa verifica
   complessiva del PM, non certezza di ogni assunzione, risoluzione dei conflitti
   ancora Open o approvazione cliente. La classificazione rimane nel documento.
9. PUT dell'anteprima crea una nuova revisione Unvalidated. La conferma può cambiare
   soltanto validationState e metadati di conferma, quindi congela il JSON completo;
   dopo questo commit il contenuto è immutabile. Nessuna mutazione da un agente.

Non si anticipano riferimenti o tabelle KB: le fonti correnti sono Message/PmRevision.

## 5. Anteprima, conferma e snapshot documentale

La prima revisione validata strutturalmente è originata dall'AI e non confermata.
Le modifiche PM creano revisioni successive attribuite, usando la revisione corrente
come base e controllo di concorrenza. Il risultato AI iniziale e lo storico originale
restano disponibili. Per confermare non è obbligatorio risolvere tutti i conflitti:
quelli lasciati Open saranno riportati come tali.

Il comando Genera documento controlla in transazione progetto, analisi corrente,
revisione richiesta corrente e PmConfirmed. Crea un ProcessingRun Document con
InputRevisionId immutabile. La revisione non cambia al retry/resume. Un edit successivo
crea una nuova Unvalidated, senza mutare il run già partito; un nuovo comando sarà
ammesso solo dopo conferma della revisione ora corrente.
Una vecchia bozza resta leggibile con etichetta della revisione utilizzata.

## 6. DocumentAgent e lunghezza adattiva — F-05

DocumentAgent riceve l'intero contenuto confermato identificato da InputRevisionId,
non una sintesi della chat al posto dei requisiti confermati.
DocumentAgent non carica history accessoria e il suo client non registra il provider
di compattazione: nessuna riduzione nativa può riassumere involontariamente il JSON confermato. Se il contenuto necessario non
rientra nel budget, errore esplicito e nessuna omissione o documento parziale.

Il Markdown organizza le sezioni applicabili: sintesi/obiettivi, contesto/perimetro,
requisiti, flussi, vincoli/dipendenze/integrazioni, assunzioni/punti aperti. Omettere
sezioni non pertinenti senza omettere informazioni disponibili. Niente ripetizioni,
riempitivi, stime AI o obiettivo di parole/pagine; 2–5 pagine è orientamento editoriale.
Fatti rimandano a fonti user o PmRevision; ipotesi e conflitti aperti restano espliciti.
ValidatePersistDraft verifica formato essenziale, fonti, binding di revisione e
salva Proposed/NotApproved. La qualità semantica è valutata sui criteri SC-003/SC-006,
non certificata dal solo parsing.

## 7. Budget e compattazione nativa corrente — F-06

Per chat e analisi, CompactionProvider sperimentale MAAI001 è integrato sul ChatClientBuilder con
UseAIContextProviders e SummarizationCompactionStrategy nativa, modello interno.
Budget di input = ContextWindowTokens - MaxOutputTokens - SafetyMarginTokens;
le istruzioni e tutto il payload contano nell'input. Parametri positivi e somma
valida sono verificati all'avvio, coerenti con modello e tokenizer/stimatore ammesso.

CompactionTriggerTokens è configurabile fra 1 e il budget di input, default pari
al budget; al superamento si attiva la strategia nativa. Dopo la sintesi ricontare l'intero
contesto. Se resta fuori budget, fallisce con context_overflow. Una sintesi invalida
o errore non transitorio produce compaction_failed. Nessuna sliding window/troncamento
che ometta dati, nessun reducer sullo storico originale.
La generazione riceve lo snapshot confermato come input protetto non riassumibile:
per un eccesso già dovuto a tale snapshot si fallisce prima della chiamata.

La prova dedicata riduce la soglia in un ambiente di test, verifica l'attivazione
reale, il limite finale e la conservazione di originali/ID/vincoli. Una prova
separata copre il caso di input protetto fuori budget e i guasti di sintesi.
Nessuna lunghezza della conversazione o del documento viene inferita dalle parole.

## 8. Checkpoint e commit

CheckpointManager.CreateJson con ICheckpointStore<JsonElement> PostgreSQL conserva
payload MAF opaco, parent, identità e indice oldest-first per ProcessingRun.
RunStreamingAsync avvia, ResumeStreamingAsync ripristina un nuovo processo con
stessa topologia, agent Id/Name e configurazione; nessun campo di dominio decide
la prossima fase del grafo. Il checkpoint è a confine superstep, non nel token.

Commit di revisione AI + Completed dell'analisi, e commit bozza + Completed della
generazione, sono ciascuno idempotenti e atomici. Se il commit è riuscito ma il
checkpoint successivo fallisce, il risultato di dominio resta Completed e viene
riconciliato, non sovrascritto Failed. Revisioni AI e bozze hanno unicità sul run.
Nessun checkpoint resta necessario per attendere la conferma PM: la revisione DB
è il confine durevole fra le due fasi.

## 9. KnowledgeAgent — evoluzione non implementata

Futuro AIAgent richiamabile come tool con AsAIFunction/function tools e retrieval
TextSearchProvider per KB progetto/cliente/prodotto/versione e web pubblico autorizzato.
Evidenze con citazioni/locator, provenienza, versioni, conflitti e lacune; scope e
permessi sono verificati prima del retrieval, non scelti dal modello.
AsAIFunction restituisce testo: un payload strutturato futuro richiederà validazione.
Docling: parsing → chunking → indicizzazione nell'ingestion futura.
Nessun tool/provider, schema KB o adapter placeholder è registrato nella slice.

## 10. Registro atomico delle evidenze — estrazione, selezione e consolidamento (2026-10-31)

Mandato: [atomic-evidence-ledger.md](../atomic-evidence-ledger.md). Le evidenze sono interpretazioni verificabili, mai verità:
l'originale PM resta l'autorità e nessuna evidenza ha come fonte primaria un messaggio dell'assistente.

### 10.1 Lavoro di estrazione e scheduling

- Stessa macchina a stati, lease, rinnovo, reclaim, fencing, sweep, backoff e `MaxAttempts` di §2, sulla tabella
  `EvidenceExtractionJobs`; stesso lock di claim e stesso limite AI globale (uno). Deadline: `Execution:ExtractionDeadlineSeconds`
  (default 3600) dalla richiesta.
- Priorità al claim: turni e run eleggibili come in §1–§3; un'estrazione solo se nessuno dei due è eleggibile. Fra le estrazioni:
  quelle che bloccano un run di Analysis Queued, poi `Initial`, poi `Reanalysis`/`Backfill`; a parità FIFO per richiesta.
- Un run di Analysis non è eleggibile finché esiste un lavoro di estrazione Queued/Running per un messaggio PM con
  `Sequence <= SourceCutoffSequence` della sua conversazione.
- Errori del modello: stessa classificazione di `ModelFailure` (transitorio → Queued con backoff; permanente → Failed). Output non
  valido: Failed `invalid_agent_output` con le violazioni (troncate a 1.000 caratteri), nessuna riparazione né seconda chiamata.

### 10.2 Agente e input (fase `Extraction`, prompt `extraction-v1`, schema `evidence-1.0.0`)

`ChatClientAgent` MAF senza history né strumenti, temperatura 0, `json_schema` per lavoro; configurazione `Ai:Phases:Extraction`
(modello, `enable_thinking`, `max_completion_tokens`, timeout) risolta e congelata in `ExtractorConfig` alla richiesta del lavoro.
Un solo messaggio utente con, nell'ordine: dati del server; le evidenze correnti dei messaggi PM precedenti (alias `P1…Pn`, tipo,
polarità, enunciato, sequenza: solo per collegare anafore, conferme, rettifiche e alternative); gli originali PM precedenti più recenti
entro il budget della fase (marcati come contesto); l'eventuale ultimo messaggio dell'assistente precedente al messaggio (marcato come
«referente, non fonte di fatti»); infine il messaggio da estrarre. Nessun messaggio con sequenza maggiore entra mai. Le omissioni dovute
al budget sono registrate in `Parameters` della versione.

### 10.3 Output del modello (JSON Schema per lavoro)

Generato dai DTO di `Evidence/EvidenceExtraction.cs`; forma normativa:

```json
{
  "type": "object", "additionalProperties": false, "required": ["evidences", "noEvidenceReason"],
  "properties": {
    "evidences": { "type": "array", "items": {
      "type": "object", "additionalProperties": false,
      "required": ["key","kind","kindRationale","statement","subject","action","object","scope","purpose","instrument","channel",
                   "recipient","qualifiers","polarity","certainty","sources","relations"],
      "properties": {
        "key": { "type": "string", "pattern": "^e[1-9][0-9]*$" },
        "kind": { "enum": ["Goal","Context","Requirement","Action","Constraint","Exclusion","Dependency","Assumption","Gap",
                           "Alternative","Decision","Other"] },
        "kindRationale": { "type": ["string","null"] },
        "statement": { "type": "string", "minLength": 1 },
        "subject|action|object|scope|purpose|instrument|channel|recipient": { "type": ["string","null"] },
        "qualifiers": { "type": "array", "items": { "type": "object", "additionalProperties": false, "required": ["type","text"],
          "properties": { "type": { "enum": ["Quantity","Deadline","Duration","Frequency","Minimum","Maximum","Condition",
                                             "Exception","Obligation","Optionality","Exclusivity","Timing","Other"] },
                          "text": { "type": "string", "minLength": 1 } } } },
        "polarity": { "enum": ["Affirmed","Negated","Excluded","Prohibited","Undefined"] },
        "certainty": { "enum": ["Declared","Inferred","Hypothesized","ToClarify"] },
        "sources": { "type": "array", "minItems": 1, "items": { "type": "object", "additionalProperties": false,
          "required": ["messageId","quote","occurrence","role"],
          "properties": { "messageId": { "enum": ["<id del messaggio estratto e dei messaggi forniti>"] },
                          "quote": { "type": "string", "minLength": 1 },
                          "occurrence": { "type": "integer", "minimum": 1 },
                          "role": { "enum": ["Primary","Context","Referent"] } } } },
        "relations": { "type": "array", "items": { "type": "object", "additionalProperties": false,
          "required": ["type","target","basis","rationale"],
          "properties": { "type": { "enum": ["Qualifies","Confirms","Contradicts","Corrects","Supersedes","DependsOn",
                                             "AlternativeTo","SameRule"] },
                          "target": { "type": "string", "pattern": "^(e[1-9][0-9]*|P[1-9][0-9]*)$" },
                          "basis": { "enum": ["Declared","Inferred"] },
                          "rationale": { "type": ["string","null"] } } } }
      } } },
    "noEvidenceReason": { "type": ["string","null"] }
  }
}
```

La riga `subject|…|recipient` abbrevia otto proprietà con lo stesso tipo.

### 10.4 Validazione (deterministica, prima del commit)

1. JSON stretto (nessuna proprietà extra, enum per nome); `key` univoche; `statement` non whitespace; `kind = Other` ⇒ `kindRationale`
   non whitespace; testi opzionali vuoti trattati come assenti.
2. Zero evidenze ⇔ `noEvidenceReason` non whitespace; con evidenze `noEvidenceReason` è null.
3. Ogni fonte: `messageId` è il messaggio estratto, un messaggio PM precedente fornito (ruolo `Context`) o il messaggio assistant
   fornito (solo ruolo `Referent`); `quote` è sottostringa dell'originale (esatta, oppure con la sola normalizzazione di spazi e
   maiuscole: gli offset si ricalcolano sull'originale); l'occorrenza `occurrence` esiste. Offset persistiti in unità UTF-16, `start`
   incluso, `end` escluso; `Body.Substring(start, end - start)` è la citazione persistita.
4. Ogni evidenza ha almeno una fonte `Primary`, e `Primary` è ammesso solo sul messaggio estratto.
5. Relazioni: `target` è una `key` di questa uscita o un alias `P…` fornito; niente autoreferenze né duplicati.
6. Il server non può verificare che i campi non siano stati «completati» con valori generici: il prompt chiede `null` per l'assente e
   il report di copertura segnala gli scarti misurabili.

### 10.5 Commit dell'estrazione

Una transazione sotto owner/fencing/lease/deadline del lavoro: lock del messaggio, `Version = max + 1`, inserimento di versione,
evidenze, relazioni (target `P…` risolti nelle evidenze correnti fornite), lavoro Completed con `ResultVersionId`, telemetria del
tentativo. Se il messaggio non ha versione corrente: stato creato con la nuova versione corrente e `EvidencePromotion` automatica
(`PromotedBy = null`, motivo «prima estrazione valida»). Un worker obsoleto non scrive nulla.

### 10.6 Selezione del run e vista per il consolidamento

Al primo tentativo del run (prima di costruire il grafo) si scrive una sola volta, sotto fencing, `AnalysisEvidenceSelection` con le
versioni correnti dei messaggi PM ≤ cutoff; i tentativi successivi la rileggono. Messaggio senza versione corrente ⇒ Failed
`evidence_unavailable` (permanente) con l'elenco delle sequenze. La vista è deterministica (ordine per sequenza, poi ordinale; alias
`E1…En`): per ogni evidenza tipo, polarità, certezza, enunciato, struttura non nulla, qualificatori, citazioni e relazioni verso altri
alias (con la base e, per `AlternativeTo`/`Contradicts`, lo stato «aperto» finché nessuna evidenza `Declared` di tipo `Decision` le
rettifica o sostituisce). Relazioni verso versioni non selezionate: rimappate se l'abbinamento del confronto è univoco, altrimenti `stale`
(escluse dalla vista, riportate nella selezione). La vista entra nel messaggio finale di LoadDialog, che la compattazione nativa
preserva sempre.

### 10.7 Consolidamento `project-analysis-v3` / `analysis-v4`

Il contratto persistito resta `AnalysisContent 1.0.0`. Lo schema del run aggiunge a ogni oggetto elemento (`id`, `text`,
`classification`, `evidenceRefs`) la proprietà obbligatoria `evidenceKeys`: array di alias della vista (enum). ValidatePersist estrae
la mappa, rimuove `evidenceKeys` e valida il resto con le regole di §4. Alias inesistenti (modalità ValidatedTextJson) ⇒
`invalid_agent_output`. Il commit della revisione AI scrive nella stessa transazione `AnalysisEvidenceLinks`. Le istruzioni chiedono di
rappresentare ogni evidenza selezionata nella sezione coerente con tipo e polarità, di riportare le alternative aperte solo in
`conflicts` e di non promuovere `Inferred`/`Hypothesized` a dichiarato; la verifica resta del report di copertura (§10.8).

**Registro oltre il budget: consolidamento incrementale.** Prima di costruire il grafo il server pianifica con i token reali del modello
(`AnalysisConsolidation.PlanAsync`). Passo singolo (grafo sopra, invariato) se la chiamata sta nel budget della fase Analysis senza
compattazione oppure con la compattazione nativa, la cui sintesi deve a sua volta stare nel budget della fase Compaction (finestra −
output − margine). Altrimenti il grafo nativo diventa LoadDialog → agente₁ → passo₁ → agente₂ → … → agenteₙ → ValidatePersist: ogni passo
copre un intervallo di sequenze entro il cutoff con gli originali interi e le loro evidenze intere (ciascuna in un solo passo; le relazioni
nel passo della più recente), più l'analisi corrente con `evidenceKeys` (uscita validata del passo precedente, riservata al tetto di
output della fase). Le evidenze di un messaggio che non stanno in un passo si distribuiscono su più passi ripetendo l'originale; un
messaggio che non sta con una sola sua evidenza è `context_overflow` esplicito. Ogni passo valida l'uscita come §4 (alias ammessi: quelli
introdotti fin lì) e non può perdere alias già rappresentati (stato nativo del workflow, nei checkpoint): perdita ⇒ `invalid_agent_output`.
Nessun limite al numero di evidenze, nessuna sintesi, nessun troncamento; il piano è deterministico (stessa topologia al resume) e ogni
passo è un superstep con checkpoint, quindi un retry riprende dall'ultimo passo completato entro la stessa deadline del run.

### 10.8 Verifica di copertura e provenienza (`coverage-v1`)

Input esatti: selezione e vista del run, mappa, revisione AI, revisione target (default: corrente), bozza opzionale (deve derivare
dalla revisione target). Esiti per categoria:

| Categoria | Codici |
| --- | --- |
| Errore deterministico | `evidence_unrepresented` (nessun elemento collegato), `evidence_source_mismatch` (l'elemento collegato non cita il messaggio dell'evidenza), `evidence_span_invalid`, `open_conflict_as_decision` (alternativa aperta collegata a requisiti/flussi/vincoli/dipendenze), `relation_stale`, `revision_source_not_cited` (fonte di un elemento confermato assente dai marcatori della bozza) |
| Rischio semantico stimato | `qualifier_changed` (numero del qualificatore assente dal testo collegato), `negation_inverted` (evidenza negativa collegata a testo senza marcatori di negazione fuori da esclusioni, o evidenza affermativa collegata solo a esclusioni), `instrument_detached` (strumento e azione mai nello stesso elemento collegato), `revision_item_missing_in_document` (parole significative dell'elemento confermato in gran parte assenti dalla bozza) |
| Valutazione PM | `item_removed_by_pm`, `item_rewritten_by_pm` (elementi collegati assenti o con testo cambiato nella revisione target) |

I rischi sono euristiche lessicali con incertezza dichiarata, mai giudizi semantici certi; nessun LLM li valuta in questa versione.
Il report non modifica analisi, revisioni o bozze ed è persistito con `CheckerVersion` e hash degli input.
