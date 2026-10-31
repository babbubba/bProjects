# Data model — 001-project-draft

**Updated**: 2026-10-28. Modello logico, senza migrazioni generate.
Si mantiene la base esistente: Identity, progetti, conversazioni, turni, bozze e
checkpoint. La precedente AnalysisRun è generalizzata in ProcessingRun con due Kind;
si aggiunge soltanto AnalysisRevision, indispensabile per anteprima e snapshot confermato.
Nessuna tabella KB, workflow engine o coda esterna.

## Identity e Project

Account/ruoli locali ASP.NET Core Identity, PM autorizzati su tutti i progetti.
RefreshSession: Id UUID, UserId, TokenHash, ExpiresAt, RevokedAt/ReplacedById opzionali;
rotazione/revoca atomiche, token raw soltanto nel cookie protetto.

Project: Id UUID, Name testo non whitespace (massimo 200 caratteri, non univoco),
Description opzionale (massimo 4.000), CreatedBy e CreatedAt UTC,
CurrentAnalysisId FK opzionale al ProcessingRun Analysis riuscito, LatestDraftId FK opzionale.
CurrentAnalysisId cambia solo al commit di un'analisi riuscita; tentativi falliti
non nascondono l'ultima anteprima riuscita. Le ultime richieste/stati sono query sui run.

## Conversation e ConversationMessage

Conversation: Id UUID, ProjectId FK UNIQUE, CreatedAt UTC, LastSequence bigint,
MafChatSessionState JSONB opzionale, MafVersion, ChatConfigVersion,
ContextThroughSequence bigint, ChatSessionRevision bigint.
È conservato soltanto l'ultimo snapshot derivato, non una copia dello storico per turno.

ConversationMessage: Id UUID, ConversationId FK, Sequence bigint, Role=user/assistant,
Body testo originale, CreatedAt UTC. Per user: ClientMessageId UUID, AuthorId FK.
Per assistant: AgentId, ReplyToMessageId FK a user e ChatTurnId FK.
Nessuna mutazione/cancellazione applicativa degli originali, né risposta AI spacciata per PM.

Unicità: (ConversationId, Sequence), (ConversationId, ClientMessageId) per user,
ReplyToMessageId per risposta assistant pubblicata.
Testo PM non whitespace, massimo 65.536 caratteri per singolo invio come protezione
del trasporto, non limite complessivo di conversazione né criterio di qualità.
Il salvataggio conserva esattamente il testo ricevuto.
Sequence/LastSequence, originale PM e ChatTurn sono committati insieme sotto lock.
Una risposta successiva usa lo stesso lock per la propria Sequence.
Analizza acquisisce il medesimo lock per fissare il cutoff senza note non committate.

## ChatTurn

| Campo | Regola |
| --- | --- |
| Id / ConversationId / TriggerMessageId | UUID e FK; TriggerMessageId user UNIQUE |
| SourceCutoffSequence | Sequenza del trigger assegnata nel commit originale, immutabile |
| Status | Queued, Running, Completed, Failed |
| RequestedAt / StartedAt / CompletedAt | UTC; ultimi due opzionali |
| DeadlineAt | RequestedAt + RunDeadline; non si azzera al restart |
| ResultMessageId | FK assistant, obbligatoria solo se Completed |
| ErrorCode / ErrorMessage | Redatti; valorizzati per Failed |
| AttemptCount / MaxAttempts | Persistenti, inclusivo del primo claim; default massimo 3 fissato nel turno |
| NextAttemptAt | UTC di eleggibilità, include backoff/retry-after |
| LeaseOwner / LeaseUntil / FencingToken | Proprietario temporaneo, scadenza, contatore monotono |
| PromptVersion / AgentConfig | Configurazione risolta, senza segreti |
| SessionRevisionAtStart | Revisione derivata usata per confronto al commit |

FIFO per TriggerMessage.Sequence: nessun turno successivo supera un precedente
Queued/Running, neppure durante backoff. Una sola riga Running con lease valido per
conversazione/progetto, protetta da vincoli e claim atomico. Replay ClientMessageId
recupera originale e turno esistenti; testo differente con stessa chiave produce conflitto.

Il provider ricostruisce user/assistant <= cutoff escludendo il trigger dalla history
prima di passarlo come input. Se ContextThroughSequence supera il cutoff, o versione/
configurazione sono incompatibili, si crea una sessione MAF nuova dagli originali
ammessi, senza riutilizzare sintesi/indici più avanzati. Lo stato del provider conserva
solo i riferimenti ordinati agli originali usati: il riuso è ammesso solo se formano
un prefisso della nuova history ammessa; inserimenti intermedi impongono ricostruzione. La sessione precedente non
è fonte autorevole. Nessun archivio di snapshot completi per messaggio.

Al completamento, append assistant, ResultMessageId, snapshot serializzato,
ContextThroughSequence (inclusa la risposta appena committata), ChatSessionRevision
e Completed sono una transazione unica protetta da lease/fencing e compare-revision.
Il provider non scrive anticipatamente la risposta fuori da tale transazione.
Commit rifiutato a vecchio owner non cambia lo stato corrente; il proprietario
valido continua. Se già Completed, riconcilia lo stesso ResultMessageId.

### Transizioni e recovery

Politica normativa in [contratto AI §2](contracts/ai-workflow.md#2-lease-recovery-e-retry--f-02):
lease 30 s, rinnovo 10 s, default 3 attempt, backoff 2/4 s, deadline 180 s,
tutti configurabili entro i vincoli documentati. Ora e scadenza dal database.

- Queued eleggibile → Running: nuovo owner/fence, incremento AttemptCount.
- Running con heartbeat valido resta Running e rinnova il lease, non la deadline.
- Running con lease scaduto → Running mediante reclaim automatico, entro limite;
  nuovo owner/fence e attempt, ripartenza da sessione committata/cutoff.
- Running con errore transitorio → Queued con NextAttemptAt e lease rilasciato;
  non permette il sorpasso del turno successivo.
- Errore permanente, deadline o attempt esauriti → Failed terminale.
- Risultato atomico → Completed terminale.
- Crash pre-commit può ripetere l'inferenza; crash post-commit non la rigenera.
- Failed non viene riattivato dalla lettura/replay HTTP; il PM può inviare un nuovo
  messaggio. Le sue informazioni originali restano disponibili per Analizza.

Non sono previsti checkpoint workflow intra-RunAsync del turno singolo: la sessione
committata non viene etichettata impropriamente come checkpoint. I checkpoint
nativi disponibili sono usati nelle elaborazioni formali sotto.

## ProcessingRun (generalizzazione della precedente AnalysisRun)

Una sola tabella per i due comandi formali; nessun coordinatore di fasi persistito.

| Campo | Regola |
| --- | --- |
| Id / ProjectId | UUID e FK |
| Kind | Analysis oppure Document |
| ConversationId / SourceCutoffSequence | Obbligatori per Analysis; input immutabile |
| AnalysisId / InputRevisionId | Obbligatori per Document; FK a run Analysis riuscito e sua revisione corrente confermata al momento dell'avvio |
| RequestKey / RequestedBy / RequestedAt | Idempotenza e paternità della richiesta |
| Status | Queued, Running, Completed, Failed |
| StartedAt / CompletedAt / DeadlineAt | Stessa politica temporale di ChatTurn, nessun tempo umano incluso |
| WorkflowDefinitionVersion | project-analysis-v2 oppure project-document-v1 |
| MafVersion / AgentConfig / PromptVersions | 1.26.0 e configurazione risolta immutabile, senza segreti |
| WorkflowSessionId | Identità MAF persistita quando disponibile |
| AttemptCount / MaxAttempts / NextAttemptAt | Controllo retry/reclaim; stessa politica del contratto AI |
| LeaseOwner / LeaseUntil / FencingToken | Claim/rinnovo/reclaim e protezione scritture |
| LastObservedStage / UpdatedAt | Proiezione informativa degli eventi MAF, mai prossimo step |
| ResultRevisionId / DraftId | Esattamente il risultato pertinente a Kind, obbligatorio per Completed |
| ErrorCode / ErrorMessage | Errore redatto per Failed |

Unicità (ProjectId, Kind, RequestKey); stessa chiave con input differente è conflitto.
Indice parziale ProjectId per Queued/Running impedisce elaborazioni formali concorrenti
di qualsiasi Kind sul progetto. Chat e workflow formale hanno stati/sessioni distinti.

Le transizioni Queued/Running/Completed/Failed e i limiti sono quelli sopra;
reclaim/resume formale usa l'ultimo checkpoint compatibile, altrimenti l'input originale
se la deadline consente. Nessun contatore si azzera dopo crash.
Completed e Failed sono terminali: un nuovo comando PM usa nuova RequestKey/Id.
Un commit risultato già riuscito prevale su un guasto successivo del checkpoint:
riconciliare Completed, non mutarlo in Failed.

Analysis completata crea la prima AnalysisRevision Unvalidated e aggiorna
Project.CurrentAnalysisId nella stessa transazione. Non crea Draft.
Document completata crea Draft e aggiorna Project.LatestDraftId nella stessa transazione.
InputRevisionId non cambia se il PM modifica successivamente l'anteprima.

## AnalysisRevision

| Campo | Regola |
| --- | --- |
| Id / AnalysisId | UUID e FK al ProcessingRun Kind=Analysis |
| Version | Intero >= 1, univoco per AnalysisId e crescente sotto lock |
| ParentRevisionId | FK opzionale alla revisione precedente della stessa analisi |
| Origin | Ai oppure PmEdit |
| CreatedBy / CreatedAt | Utente richiedente/editore e UTC; per Ai anche agente/prompt nel run |
| SchemaVersion | 1.0.0 |
| ContentJson | AnalysisContent del contratto AI, validato prima del commit |
| ValidationState | Unvalidated oppure PmConfirmed, coerente col campo JSON |
| ConfirmedBy / ConfirmedAt | Null finché Unvalidated; PM e UTC alla conferma |

La prima revisione AI è univoca per AnalysisId con Origin=Ai: replay del run non
ne crea una seconda. Tutte le revisioni persistono: originali, alternative ed edit PM
restano distinguibili senza una nuova tabella di audit o di requisiti.
La revisione corrente è la Version massima; non serve un altro puntatore.

PUT preview usa If-Match=Id della revisione corrente e lock dell'analisi; crea Version+1
Unvalidated con ParentRevisionId. Il backend attribuisce fonti PmRevision alle parti
modificate/aggiunte. Non muta originali o revisioni precedenti, anche se non confermate.
La conferma della revisione corrente cambia solo ValidationState e metadati di conferma,
con valida conformità dello schema e delle fonti; quindi congela tutto il JSON.
Il PM può confermare anche punti aperti/assunzioni, senza risolverli o trasformarli in fatti.

Dopo conferma, un edit crea una nuova Unvalidated. Si può ancora leggere o terminare
una generazione già avviata sulla precedente, ma non avviarne un'altra su revisione
non corrente/non confermata. Generazione e conferma acquisiscono lo stesso lock
dell'analisi per evitare race col salvataggio.
Solo l'analisi corrente del progetto è modificabile/confermabile/generabile.
Durante una nuova Analysis attiva il precedente preview resta leggibile ma non modificabile;
durante Document attiva gli edit successivi sono ammessi senza cambiare il suo input.

WaitingForPM è derivato da revisione corrente Unvalidated: nessun lease, job attivo,
deadline o oggetto workflow sospeso. Sopravvive per il solo fatto di essere persistito.
La conferma PM non registra alcuna approvazione cliente.

## Draft

Id UUID, ProjectId FK, ProcessingRunId FK Kind=Document UNIQUE,
AnalysisRevisionId FK, Markdown non vuoto, CreatedAt UTC,
Status=Proposed e ApprovalStatus=NotApproved.
AnalysisRevisionId coincide con InputRevisionId del run; a tale istante lo snapshot
è confermato e immutabile. La bozza conserva fonti Message/PmRevision e distinzione
di assunzioni/conflitti. Nessun vincolo o target basato sulle parole.

Commit Draft + Completed del run + Project.LatestDraftId atomico e idempotente.
Un errore non cambia LatestDraftId. Le bozze precedenti restano consultabili;
nessun editor avanzato o diff documentale richiesto.
HasNewNotes considera soltanto messaggi PM oltre cutoff; HasNewRevision confronta
AnalysisRevisionId con l'ultima revisione dell'analisi corrente.
La UI mostra se la bozza si riferisce a un'analisi/revisione precedente.

## WorkflowCheckpoint

ProcessingRunId FK sostituisce il precedente riferimento AnalysisRunId.
WorkflowSessionId, CheckpointKey, CheckpointInfo/ParentInfo JSON, Payload JSONB opaco,
CommitSequence, CreatedAt, MafVersion e WorkflowDefinitionVersion.
Unicità (ProcessingRunId, WorkflowSessionId, CheckpointKey); scrittura atomica con
fencing corrente e indice oldest-first. Nessun puntatore applicativo allo step successivo.

Sessioni agenti del workflow sono incluse nel payload dal binding nativo, senza
una seconda copia nella Conversation. Solo la chat viva usa lo snapshot Conversation.
L'attesa umana è in AnalysisRevision e non richiede un checkpoint sospeso.
Nessuna tabella di stato per ogni executor; niente entità future KB.

## InferenceCall (telemetria tecnica, migrazione additiva InferenceCalls)

Una riga per richiesta reale al modello: ProjectId FK; ChatTurnId oppure ProcessingRunId
(esattamente uno); Attempt e FencingToken del lease; Phase (Chat/Analysis/Document/Compaction);
ModelId, StartedAt, DurationMs; ContextWindowTokens (/props n_ctx solo se runtime Online, altrimenti
null); MaxOutputTokensRequested (max_tokens inviato, non confermato); ServerDefaultNPredict;
ReasoningRequested/Effective (on/off/unknown, mai il testo); Prompt/Completion/Total/CachedTokens
solo dall'usage del runtime (null se assente, mai stimati); FinishReason; State
(Running→Completed/Failed/Interrupted); IsFinal; Config JSONB numerico senza segreti.
Solo sulla riga finale: Composition JSONB {items[key,tokens,messageCount]} contata con /tokenize
sulla richiesta inviata, History JSONB {messageCount, originalTokens} dello storico entrato nella
compattazione, Compaction JSONB {at, tokensBefore, tokensAfter, messagesSummarized}.

Le righe del tentativo vincente si scrivono nella transazione del commit del risultato (stesso
controllo lease/fencing: commit rifiutato = nessuna riga); quelle di un tentativo fallito o
rifiutato dal validatore nella transazione del rilascio/Failed, mai finali. Un solo IsFinal per
turno/run (indici univoci filtrati). Il risultato espone la riga finale e quelle del suo tentativo
(calls, sintesi aggregate); turni/run senza righe → metrics null. Nessun testo, prompt o
ragionamento nelle colonne. Una metrica storica copia i valori del momento della chiamata.

## Invarianti e confini

1. Chat usa cutoff rigoroso; analisi usa dialogo user/assistant <= cutoff con ruoli distinti.
2. Fatti del documento sono supportati da user o edit PM attribuiti, non da sole risposte AI.
3. Conflitti conservano alternative; la loro risoluzione è un atto PM nella revisione.
4. Conferma e generazione richiedono la revisione corrente; la conferma congela il contenuto.
5. Ripresa formale richiede topologia, ID/Nome, schema/configurazione e input compatibili.
6. Retry/recovery possono ripetere chiamate ma non originali, revisione AI o bozza.
7. Gli originali, JSON confermati e riferimenti non vengono ridotti dalla compattazione.
8. Nessun contenuto integrale o segreto nei log; DB e serializzazioni derivati sono riservati.
