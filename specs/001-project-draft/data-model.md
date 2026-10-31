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

## Registro atomico delle evidenze (migrazione additiva EvidenceLedger, 2026-10-31)

Metadati relazionali indicizzati + `jsonb` per il contenuto semantico di ciascuna evidenza; nessuna colonna per campo semantico.
Tutte le tabelle hanno `ProjectId` FK e ogni lettura filtra per progetto. Nessuna tabella esistente perde vincoli; `InferenceCalls`
acquisisce un terzo proprietario possibile.

**EvidenceExtractionJob** (lavoro durevole, stesse regole di claim di ChatTurn/ProcessingRun)

| Campo | Regola |
| --- | --- |
| Id / ProjectId / ConversationId / MessageId | UUID e FK; il messaggio è `User` della conversazione del progetto |
| MessageSequence | Sequenza del messaggio, copiata per ordinamento e gate di Analizza |
| Reason | `Initial` (POST messaggio), `Reanalysis` (PM), `Backfill` (PM o Analizza) |
| BatchId | FK opzionale a EvidenceBatch (rianalisi di progetto/backfill) |
| RetryOfJobId | FK opzionale al lavoro Failed di cui è il retry autorizzato |
| Status | Queued, Running, Completed, Failed |
| RequestedBy | Utente richiedente; null per il lavoro iniziale creato dal salvataggio |
| RequestedAt / StartedAt / CompletedAt / DeadlineAt | Tempo del database; DeadlineAt = RequestedAt + `Execution:ExtractionDeadlineSeconds` |
| AttemptCount / MaxAttempts / NextAttemptAt / LeaseOwner / LeaseUntil / FencingToken | Come ChatTurn |
| ExtractorConfig | JSONB congelato alla richiesta: modello, fase, prompt, schema, thinking, max output, temperatura, timeout |
| ResultVersionId | FK alla versione prodotta; obbligatoria se Completed |
| ErrorCode / ErrorMessage | Obbligatori se Failed (`invalid_agent_output`, `model_*`, `deadline_exceeded`, `attempts_exhausted`…) |

Indici: univoco parziale `MessageId` per Queued/Running (un solo lavoro aperto per messaggio); FIFO parziale sui lavori aperti;
`(ProjectId, MessageSequence)`; `BatchId`.

**EvidenceExtractionVersion** (append-only: nessun UPDATE/DELETE applicativo)

| Campo | Regola |
| --- | --- |
| Id / ProjectId / ConversationId / MessageId / MessageSequence | Origine esatta |
| Version | ≥ 1, univoco per MessageId, assegnato sotto lock del messaggio |
| JobId | FK univoca al lavoro che l'ha prodotta |
| SchemaVersion / PromptVersion / ModelId | `evidence-1.0.0`, `extraction-v1`, modello effettivo |
| Parameters | JSONB: configurazione effettiva (ExtractorConfig) e contesto usato (`contextThroughSequence`, id dei messaggi e versioni precedenti fornite, omissioni per budget) |
| EvidenceCount / NoEvidenceReason | `NoEvidenceReason` obbligatorio se e solo se `EvidenceCount = 0` |
| CreatedAt | Tempo del database |

**Evidence**: Id, VersionId FK, ProjectId, MessageId, Ordinal (univoco per versione), Key (`e1`…, univoca per versione), Kind
(Goal, Context, Requirement, Action, Constraint, Exclusion, Dependency, Assumption, Gap, Alternative, Decision, Other), Polarity
(Affirmed, Negated, Excluded, Prohibited, Undefined), Certainty (Declared, Inferred, Hypothesized, ToClarify), Statement, Content
JSONB (`kindRationale`, struttura soggetto/azione/oggetto/ambito/scopo/strumento/canale/destinatario, qualificatori `{type,text}`,
fonti `{messageId, sequence, role Primary|Context|Referent, start, end, quote}`). Indici `(ProjectId, MessageId)`, `(VersionId, Ordinal)`.

**EvidenceRelation** (immutabile): Id, ProjectId, VersionId (versione che asserisce la relazione), FromEvidenceId, ToEvidenceId (FK
Evidence: stessa versione oppure evidenza corrente di un messaggio precedente al momento dell'estrazione), Type (Qualifies, Confirms,
Contradicts, Corrects, Supersedes, DependsOn, AlternativeTo, SameRule), Basis (Declared = enunciata dal PM nel testo, Inferred =
interpretazione dell'AI), Rationale opzionale. Univoco `(VersionId, FromEvidenceId, ToEvidenceId, Type)`.

**EvidenceMessageState**: MessageId PK/FK, ProjectId, CurrentVersionId FK (versione dello stesso messaggio), Revision (intero, +1 a ogni
promozione: concorrenza ottimistica), UpdatedAt. Creata con la prima versione valida (regola automatica).

**EvidencePromotion** (append-only): Id, ProjectId, MessageId, FromVersionId (null alla prima), ToVersionId, PromotedBy (null = regola
automatica della prima versione valida), PromotedAt, Reason, RelationImpact JSONB (relazioni rimappate/stale verso il messaggio).

**EvidenceBatch**: Id, ProjectId, Kind (Reanalysis, Backfill), RequestKey (univoca per progetto e Kind), RequestedBy, RequestedAt,
ThroughSequence (ultimo messaggio PM al momento della richiesta), JobCount, SkippedCount. L'avanzamento è contato dai lavori.

**AnalysisEvidenceSelection** (una per run di Analysis, scritta una sola volta sotto fencing): RunId PK/FK, ProjectId, CreatedAt,
ViewHash (SHA-256 della vista), View JSONB (alias, evidenze, relazioni risolte/rimappate/stale). **AnalysisEvidenceSelectionItem**:
(RunId, MessageId) PK, VersionId FK: la versione selezionata per ciascun messaggio PM ≤ cutoff.

**AnalysisEvidenceLink**: (RunId, ItemId, EvidenceId) PK, ProjectId; ItemId = id dell'elemento di `AnalysisContent` della revisione AI;
scritte nella stessa transazione della revisione AI, mai modificate.

**EvidenceCoverageReport**: Id, ProjectId, AnalysisId FK, RevisionId FK opzionale, DraftId FK opzionale, CheckerVersion, InputHash,
CreatedBy, CreatedAt, contatori (DeterministicErrors, SemanticRisks, PmAssessments), Report JSONB. Univoco
`(AnalysisId, RevisionId, DraftId, CheckerVersion)`: la stessa richiesta rilegge il report esistente.

**InferenceCalls**: colonna `EvidenceExtractionJobId` FK opzionale; il vincolo di proprietà diventa «esattamente uno fra ChatTurnId,
ProcessingRunId, EvidenceExtractionJobId»; un solo `IsFinal` per lavoro. Fase `Extraction`.

## Invarianti e confini

1. Chat usa cutoff rigoroso; analisi usa dialogo user/assistant <= cutoff con ruoli distinti.
2. Fatti del documento sono supportati da user o edit PM attribuiti, non da sole risposte AI.
3. Conflitti conservano alternative; la loro risoluzione è un atto PM nella revisione.
4. Conferma e generazione richiedono la revisione corrente; la conferma congela il contenuto.
5. Ripresa formale richiede topologia, ID/Nome, schema/configurazione e input compatibili.
6. Retry/recovery possono ripetere chiamate ma non originali, revisione AI o bozza.
7. Gli originali, JSON confermati e riferimenti non vengono ridotti dalla compattazione.
8. Nessun contenuto integrale o segreto nei log; DB e serializzazioni derivati sono riservati.
9. Un'evidenza è un'interpretazione verificabile, non una verità: l'originale PM resta l'autorità; versioni, relazioni, promozioni,
   selezioni e mappe sono append-only o scritte una volta.
10. Nessun dato con sequenza superiore al messaggio estratto entra nella sua estrazione; nessun dato oltre il cutoff entra nella
    selezione di un run; nessun messaggio assistant è fonte primaria di un'evidenza.
11. Un lavoro Failed non è una versione con zero evidenze; una selezione non può contenere un messaggio senza versione corrente.
12. Una promozione cambia solo `EvidenceMessageState`; run, selezioni, revisioni e bozze esistenti non cambiano.
