# Contratto HTTP — chat, anteprima e generazione

**Updated**: 2026-10-31 (registro delle evidenze, §Registro). Base /api, JSON camelCase, UUID stringa, timestamp UTC ISO 8601.
PM autorizzato per tutte le risorse; tutti i PM vedono tutti i progetti.
Ruolo, autore, scope, cutoff e riferimenti di revisione sono validati dal backend.

## Sessione locale

| Metodo e percorso | Input | Esito |
| --- | --- | --- |
| POST /api/auth/login | username, password | 200 accessToken/expiresAt; refresh nel cookie protetto |
| POST /api/auth/refresh | cookie e protezione CSRF | 200 access token nuovo e refresh ruotato |
| POST /api/auth/logout | sessione e protezione CSRF | 204, revoca e rimozione cookie |

Identity locale, access token in memoria, refresh HttpOnly/Secure/SameSite;
XSRF-TOKEN leggibile e header X-XSRF-TOKEN validato sugli endpoint cookie-based.
Nessun signup pubblico o UI Admin. Le credenziali non entrano in sessioni/prompt AI.

## Progetti e dialogo

| Metodo e percorso | Input | Esito |
| --- | --- | --- |
| GET /api/projects | cursor opzionale, limit 1–100 | 200 items/nextCursor |
| POST /api/projects | name, description opzionale | 201 progetto/conversationId e Location |
| GET /api/projects/{p} | — | 200 stato progetto e riferimenti ai risultati |
| GET /api/projects/{p}/messages | afterSequence opzionale, limit 1–100 | 200 messaggi ordinati/nextAfterSequence |
| POST /api/projects/{p}/messages | clientMessageId, text | 201 messaggio salvato e chatTurnId; 200 replay identico |
| GET /api/projects/{p}/chat-turns/{t} | — | 200 stato/risultato/errore del turno |

Nome non whitespace, massimo 200 caratteri; descrizione massimo 4.000; singolo
invio PM non whitespace, massimo 65.536 caratteri. Questo è limite di trasporto,
non limite complessivo di conversazione o metrica di qualità. Originale non trim-mato.
POST salva PM e ChatTurn insieme prima dell'inferenza; il modello può essere offline.
Stessa ClientMessageId con testo diverso: 409 idempotency_conflict.
Il replay recupera lo stesso turno senza riaccodare; un Failed non riparte alla lettura.

Message: id, sequence, role, text, createdAt, authorId/authorDisplayName per user,
agentId per assistant; chatTurnId e replyToMessageId ove pertinenti.
Assistant è etichettato AI/non validato; client non può impostare ruolo/autore.
Testo renderizzato con escaping/sanitizzazione; niente HTML eseguibile o fetch remoto.
I comandi interni e le sintesi non vengono pubblicati come messaggi PM.

ChatTurnView: id, triggerMessageId, sourceCutoffSequence, status
(Queued/Running/Completed/Failed), requestedAt, startedAt, completedAt, deadlineAt,
attemptCount, maxAttempts, nextAttemptAt, resultMessageId oppure error redatto.
I turni sono FIFO; risposta tardiva resta collegata al trigger. Streaming SSE preferito
con riconciliazione GET e polling ogni due secondi come fallback sui turni pendenti
e sui nuovi messaggi; chiusura browser non cancella il lavoro.

### Stream UI del progetto

GET /api/projects/{p}/events restituisce text/event-stream, Cache-Control: no-cache,
con la medesima autorizzazione degli altri endpoint. Il client usa fetch e header
Authorization con bearer in memoria (non EventSource con token nella query).
Una connessione per progetto aperto, condivisa dalle viste; si chiude al cambio
progetto/logout. Nessun comando AI nasce da apertura o riconnessione dello stream.

| Evento SSE | Payload JSON e significato |
| --- | --- |
| chat.delta | chatTurnId, attemptCount, deltaIndex crescente nel tentativo, text; solo testo della risposta, provvisorio |
| chat.state | ChatTurnView corrente; Completed rimanda al resultMessageId persistito, Failed all'errore |
| processing.state | ProcessingRunView corrente; lastObservedStage solo se effettivamente osservato nel workflow MAF |

Gli eventi non sono un registro durevole e non garantiscono replay con Last-Event-ID.
All'apertura/riconnessione il client riconcilia progetto, messaggi e run tramite i GET
esistenti, scartando il buffer provvisorio; nessun nuovo invio o reset dei tentativi.
Finché connesso può mantenere polling di riconciliazione ogni due secondi per rilevare
anche commit avvenuti durante la sottoscrizione. Un tentativo nuovo elimina il buffer
del precedente; delta duplicati si ignorano per turno/tentativo/deltaIndex. Il server
emette solo dall'owner con lease/fence valido. Frammenti interrotti non sono messaggi
salvati; Completed sostituisce il buffer con il messaggio autorevole letto via GET,
deduplicato per ID. Riconnessione a turno già Running può attendere il testo finale:
non si persiste né si ricostruisce il flusso di token. Nessuna promessa di ripresa token.

In analisi: LoadDialog, Consolidating (invocazione agente), ValidatePersistAnalysis;
in generazione: LoadConfirmedRevision, Generating, ValidatePersistDraft. La UI mostra
etichette comprensibili soltanto quando osservate; Queued/Running/Completed/Failed
restano gli stati persistenti esistenti. lastObservedStage è informazione descrittiva,
mai un comando per il dispatcher. Nessun thinking, prompt, tool interno, JSON non
validato o documento parziale viene trasmesso. Errori di connessione/auth si gestiscono
come trasporto, separatamente dagli errori del turno/run; niente percentuali inventate.

ProjectView contiene id/name/description, latestSequence, latestUserSequence,
pendingChatTurnIds, activeProcessingRunId, latestAnalysisRunId,
currentAnalysisId, currentRevisionId, validationState, latestDraftId.
Gli ID di run conclusi/failed restano recuperabili; non servono chiavi in localStorage
per ritrovare l'errore. CurrentAnalysisId indica l'ultima analisi riuscita, distinta
dall'ultimo tentativo. WaitingForPM è derivato da Unvalidated della revisione corrente.

## Richiesta di analisi formale

| Metodo e percorso | Input | Esito |
| --- | --- | --- |
| POST /api/projects/{p}/analyses | Idempotency-Key UUID; nessun prompt/input AI client | 202 ProcessingRun Kind=Analysis con Location |
| GET /api/projects/{p}/processing-runs/{r} | — | 200 stato della fase Analysis o Document |
| GET /api/projects/{p}/analyses/{a} | — | 200 anteprima e revisione corrente, se disponibile |
| GET /api/projects/{p}/analyses/{a}/revisions/{v} | — | 200 revisione specifica, incluse provenienza e conferma |

Analizza fissa il cutoff sotto lock della conversazione e accoda solo il workflow
di analisi JSON. Senza almeno un messaggio PM: 422 no_pm_input. Non è necessario
attendere i turni chat pendenti; risposte oltre cutoff non entrano retroattivamente.
Non chiama DocumentAgent e non crea una bozza Markdown.

ProcessingRunView: id, kind, analysisId (per Document), sourceCutoffSequence (Analysis),
inputRevisionId (Document), status, requestedAt/startedAt/completedAt/deadlineAt,
attemptCount/maxAttempts/nextAttemptAt, lastObservedStage, resultRevisionId oppure
draftId, error. Completed richiede il risultato pertinente al Kind; Failed non
presenta un risultato parziale come successo.
Prima della prima revisione valida, GET anteprima risponde 404 preview_not_available:
lo stato/errore rimane nel ProcessingRun, non viene inventato un JSON riuscito.

AnalysisView: analysisId, projectId, currentRevisionId, version, schemaVersion,
content (AnalysisContent 1.0.0), validationState, origin, createdBy/createdAt,
confirmedBy/confirmedAt, sourceCutoffSequence, hasNewNotes, canEdit/canConfirm/canGenerate.
ETag è l'ID della revisione, in forma HTTP quotata. Revisione corrente = versione massima.
Le revisioni storiche sono leggibili e non modificabili.

## Modifica e conferma dell'anteprima

| Metodo e percorso | Input | Esito |
| --- | --- | --- |
| PUT /api/projects/{p}/analyses/{a}/preview | If-Match revisione corrente; content completo dal form | 200 nuova revisione Unvalidated e nuovo ETag |
| POST /api/projects/{p}/analyses/{a}/confirm | If-Match revisione corrente; nessun contenuto AI | 200 stessa revisione PmConfirmed e metadati conferma |

UI semplice a campi/liste per testi, requisiti, assunzioni, informazioni mancanti
e conflitti. Non è un editor Markdown/Word. Correzioni e aggiunte creano una nuova
revisione con autore/data e fonte PmRevision alle parti modificate.
Il backend non accetta cambi arbitrari di schemaVersion, analysisId, projectId o
cutoff; validationState è impostato dal server, Unvalidated ad ogni nuovo edit.
Non è permesso attribuire al modello o a messaggi originali il testo aggiunto dal PM.

Conflicting alternatives rimangono tracciabili nelle revisioni. Il PM può lasciare
Open oppure scegliere alternative esistenti e motivare Resolved. La conferma complessiva
è ammessa anche con punti aperti chiaramente esposti; non certifica fatti ignoti.

Modifica/conferma richiedono l'analisi corrente del progetto. If-Match mancante:
400 revision_required; base obsoleta: 409 revision_conflict con currentRevisionId.
PUT ripetuto con vecchio If-Match non crea altre revisioni: 409 e riconciliazione via GET.
Confirm ripetuto sulla stessa revisione già confermata e ancora corrente: 200 idempotente.
Il commit di conferma imposta PmConfirmed e congela ContentJson/metadati di conferma.

Durante una nuova Analysis attiva la vecchia anteprima è leggibile ma PUT/confirm
rispondono 409 processing_active. Durante Document attiva si possono invece creare
nuovi edit non confermati, senza modificare InputRevisionId del run già avviato.
Nessuna conferma avvia implicitamente la generazione; nessun record è approvazione cliente.

## Generazione esplicita del documento

| Metodo e percorso | Input | Esito |
| --- | --- | --- |
| POST /api/projects/{p}/analyses/{a}/document-generations | Idempotency-Key UUID e revisionId | 202 ProcessingRun Kind=Document con Location |
| GET /api/projects/{p}/draft | — | 200 ultima bozza riuscita, oppure 404 se assente |

Il comando controlla in transazione: a è l'analisi corrente, revisionId è la revisione
corrente e PmConfirmed, nessun altro ProcessingRun formale attivo nel progetto.
In assenza di conferma: 409 analysis_not_confirmed; riferimento non corrente:
409 revision_conflict. Nessuna inferenza DocumentAgent parte in questi casi.

Il run congela InputRevisionId; usa quel JSON integrale immutabile, mai una sintesi
successiva o il dialogo corrente. Edit posteriori non lo modificano. Una futura
generazione richiederà conferma della revisione allora corrente.

DraftView: id, projectId, processingRunId, analysisId, analysisRevisionId,
analysisVersion, markdown, createdAt, status=Proposed, approvalStatus=NotApproved,
hasNewNotes, hasNewRevision, label="Bozza AI — da revisionare".
Fonti originali: /projects/{p}/messages/{m}.
Fonti edit PM: /projects/{p}/analyses/{a}/revisions/{v} con riferimento all'elemento.
Il server verifica appartenenza e provenienza; niente riferimenti inventati.

Il renderer è in sola lettura, sanitizzato; sezioni adattive, nessun target di parole.
Bozza precedente resta disponibile fino al successo di una nuova generazione.
Nessun endpoint di approvazione cliente, pubblicazione, KB, upload o ricerca web.

## Idempotenza, errori e ripresa

RequestKey è univoca per (ProjectId, Kind). Stessa chiave e stesso input:
202 se attivo, 200 se terminale, con medesimo run e risultato; stesso key/input
differente: 409 idempotency_conflict. Altra chiave con un run formale attivo:
409 processing_active e riferimento al run esistente.
Dopo Failed, il PM può inviare un nuovo comando con nuova chiave. Riaprire il browser
non è un nuovo tentativo e non azzera contatori/deadline.
Una sola tabella ProcessingRun serve entrambi i Kind; non c'è una coda per ogni fase.

Problem Details: 400 malformed/revision_required, 401 sessione assente, 403 ruolo,
404 risorsa non disponibile, 409 conflitto, 422 input/payload non valido,
503 database temporaneamente indisponibile. Fallimenti asincroni sono nel turno/run:
llm_unavailable, context_overflow, compaction_failed, invalid_agent_output,
deadline_exceeded, attempts_exhausted, checkpoint_incompatible, processing_failed,
context_window_mismatch (finestra del runtime minore di quella configurata: nessuna richiesta inviata).
Schema/metadati non validi in PUT: 422 preview_invalid e dettagli di campo non riservati.
Niente stack trace, contenuti completi, endpoint sensibili o segreti nei log/errori.

Recovery dopo crash e retry transitorio sono interni e limitati come nel contratto AI;
non esiste endpoint per pilotare executor, checkpoint o una fase di orchestrazione.
Il PM può attendere la conferma per tempo indefinito: nessun run resta sospeso in memoria.

## Metriche di inferenza e monitor del contesto

Estensione autorizzata dopo la prima slice: telemetria tecnica *reale* delle richieste AI e monitor del contesto.
Ogni valore numerico è un `TokenValue` con provenienza esplicita; nessuna cifra è mai ricavata da `lunghezza / 4`.

```ts
type Source = 'measured'      // restituito da llama.cpp (usage, /props, /tokenize)
            | 'calculated'    // calcolato da valori misurati con formula esplicita
            | 'estimated'     // stima dichiarata, mai presentata come misura
            | 'unavailable';  // non ottenibile (value = null)
interface TokenValue { value: number | null; source: Source; }

interface InferenceMetrics {
  state: 'running' | 'completed' | 'failed' | 'interrupted';
  phase: 'chat' | 'analysis' | 'document' | 'compaction';
  modelId: string;                       // modello effettivamente usato in quella richiesta (storico immutabile)
  startedAt: string; durationMs: number | null;
  contextWindowTokens: TokenValue;       // ctx-size per slot verificato sul runtime (/props n_ctx) al momento della richiesta
  maxOutputTokens: { value: number; confirmed: boolean }; // max_tokens richiesto; confirmed=false: limite richiesto, non verificato sul server
  serverDefaultNPredict: number | null;  // n-predict di default del server, valore distinto
  reasoning: { requested: boolean; effective: 'on' | 'off' | 'unknown' }; // mai il contenuto del ragionamento
  promptTokens: TokenValue; completionTokens: TokenValue; totalTokens: TokenValue; cachedTokens: TokenValue;
  finishReason: string | null;
  calls: number;                         // chiamate al modello del risultato logico (retry esclusi)
  compaction: null | { summaryCalls: number; summaryPromptTokens: TokenValue; summaryCompletionTokens: TokenValue };
}
```

* `Message` (ruolo assistant), le viste `ProcessingRun` di analisi e documento, `AnalysisView` (metriche del run di analisi) e
  `DraftView` (metriche del run Document che l'ha generata) includono `metrics: InferenceMetrics | null`.
  `null` = record storico senza telemetria (mostrato come «non disponibile»). Una risposta fallita non ha messaggio né metriche.
  I tentativi/retry sono righe distinte in `InferenceCalls`, ma il risultato logico espone solo quelle del tentativo vincente.
  `calls` conta tutte le chiamate del tentativo vincente, sintesi comprese; `compaction` aggrega le sole chiamate di sintesi.
* Nessun nuovo evento SSE: `chat.state` e `processing.state` (inizio/fine) e la riconnessione fanno rifare il GET del contesto.

`GET /api/projects/{p}/context-usage` → 200 (isolato per progetto, stessa autorizzazione degli altri endpoint):

```ts
interface ContextUsage {
  generatedAt: string;
  runtime: { status: 'online' | 'offline' | 'unverified'; checkedAt: string | null; modelId: string;
             contextWindowTokens: TokenValue;         // /props n_ctx per slot; unavailable se offline/non verificato
             configuredContextWindowTokens: number;   // Ai:ContextWindowTokens
             serverDefaultNPredict: number | null; windowMismatch: boolean };
  limits: { safetyMarginTokens: number;
            phases: Record<'chat' | 'analysis' | 'document' | 'compaction',
                           { maxOutputTokens: number; enableThinking: boolean; inputBudgetTokens: number }> };
  last: null | { kind: 'chat' | 'analysis' | 'document'; refId: string; at: string; metrics: InferenceMetrics;
                 usedPercent: number | null;          // calculated: promptTokens / contextWindowTokens * 100, mai > 100 se la richiesta è rientrata
                 outputReservedTokens: number; safetyMarginTokens: number;
                 remainingTokens: TokenValue;         // calculated: ctx - prompt - outputReserved - margine
                 staleReason: null | 'newer_messages' };  // note PM successive all'ultima inferenza completata
  composition: null | { promptTokens: TokenValue;
                        items: { key: string; label: string; tokens: TokenValue; messageCount: number | null }[];
                        attributedTokens: number; unattributedTokens: TokenValue };  // template/token speciali: quota non attribuita, mai forzata
  history: { messageCount: number; originalTokens: TokenValue };   // messaggi originali conservati, distinti dal contesto operativo
  compaction: null | { at: string; tokensBefore: TokenValue; tokensAfter: TokenValue; tokensSaved: TokenValue; messagesSummarized: number };
}
```

Chiavi di composizione oggi realmente supportate: `system_instructions`, `user_messages`, `assistant_messages`,
`derived_summary`, `analysis_structured`. Le chiavi future (`document_markdown`, `document_pdf`, `knowledge_base`) si
aggiungono solo quando esiste la sorgente; il pannello rende qualunque `items[]` senza codice per categoria.

`last.refId` è l'ID del messaggio assistant (chat) o del ProcessingRun (analisi/documento); `last.at` è la fine della chiamata
finale. `history` descrive lo storico originale letto dall'ultima inferenza di chat/analisi (somma dei token misurati, *calculated*);
senza un'inferenza misurata riporta il numero di messaggi conservati e `originalTokens` unavailable. `compaction` è l'ultimo evento
di compattazione del progetto (prima = storico originale, dopo = messaggi della chiamata finale; risparmio solo se dopo ≤ prima).

## Registro atomico delle evidenze (2026-10-31)

Mandato: [atomic-evidence-ledger.md](../atomic-evidence-ledger.md); regole AI in [ai-workflow.md §10](ai-workflow.md).
Stessa autenticazione (bearer, ruolo PM) e stesse convenzioni: ogni ID è cercato **dentro** il progetto `{p}` e un ID di un altro
progetto risponde 404, mai 403 o il dato altrui. Idempotency-Key UUID obbligatoria sui POST di comando (400 `idempotency_key_required`);
stessa chiave → stessa risorsa (202 se ancora attiva, 200 se terminale). Nessun endpoint espone prompt, ragionamento o output non
validato del modello.

| Metodo e percorso | Input | Esito |
| --- | --- | --- |
| GET /api/projects/{p}/evidence/status | — | 200 `EvidenceStatusView` |
| GET /api/projects/{p}/evidence | `messageId`, `kind`, `polarity`, `certainty` opzionali | 200 `{ items: EvidenceView[] }` delle versioni correnti |
| GET /api/projects/{p}/evidence/{e} | — | 200 `EvidenceDetailView` (anche di versioni non correnti) |
| GET /api/projects/{p}/messages/{m}/evidence/versions | — | 200 `MessageVersionsView` |
| GET /api/projects/{p}/evidence/versions/{v} | — | 200 `ExtractionVersionView` |
| GET /api/projects/{p}/evidence/versions/{v1}/compare/{v2} | — | 200 `VersionDiffView`; 422 `versions_not_comparable` se di messaggi diversi |
| POST /api/projects/{p}/messages/{m}/evidence/reanalyses | Idempotency-Key | 202 `ExtractionJobView`; 409 `extraction_active` (+`jobId`); 404 se `m` non è un messaggio PM del progetto |
| POST /api/projects/{p}/evidence/jobs/{j}/retry | Idempotency-Key | 202 `ExtractionJobView` del nuovo lavoro; 409 `job_not_failed` o `extraction_active` |
| GET /api/projects/{p}/evidence/jobs/{j} | — | 200 `ExtractionJobView` |
| POST /api/projects/{p}/evidence/reanalyses | Idempotency-Key | 202 `EvidenceBatchView` Kind=Reanalysis (tutti i messaggi PM senza lavoro aperto) |
| POST /api/projects/{p}/evidence/backfills | Idempotency-Key | 202 `EvidenceBatchView` Kind=Backfill (solo messaggi PM senza versione corrente né lavoro aperto) |
| GET /api/projects/{p}/evidence/batches/{b} | — | 200 `EvidenceBatchView` |
| POST /api/projects/{p}/messages/{m}/evidence/promotions | `{ versionId, expectedStateRevision, reason? }` | 200 `PromotionResultView`; 409 `promotion_conflict` (+`currentVersionId`, `stateRevision`); 422 `version_not_of_message` |
| GET /api/projects/{p}/analyses/{a}/evidence-selection | — | 200 `EvidenceSelectionView`; 404 `selection_not_available` (run precedente al registro o non ancora avviato) |
| POST /api/projects/{p}/analyses/{a}/coverage-reports | `{ revisionId?, draftId? }` | 201 `CoverageReportView` (200 se già calcolato per gli stessi input); 409 `selection_not_available`; 422 `coverage_input_invalid` |
| GET /api/projects/{p}/analyses/{a}/coverage-reports | — | 200 `{ items: CoverageReportSummary[] }` |
| GET /api/projects/{p}/analyses/{a}/coverage-reports/{r} | — | 200 `CoverageReportView` |

**Viste** (campi camelCase; enum per nome come nel contratto AI):

- `ExtractionJobView`: id, messageId, messageSequence, reason (Initial/Reanalysis/Backfill), batchId, retryOfJobId, status, requestedAt,
  startedAt, completedAt, deadlineAt, attemptCount, maxAttempts, nextAttemptAt, resultVersionId, error `{code, message}`.
- `EvidenceStatusView`: `messages[]` per ogni messaggio PM in ordine di sequenza: messageId, sequence, createdAt, stateRevision (0 senza
  stato), currentVersion `{id, version, evidenceCount, noEvidenceReason, createdAt}` o null, candidateCount (versioni più recenti della
  corrente), latestJob `ExtractionJobView` o null, state (`none` senza lavoro né versione, `pending`, `running`, `extracted`, `empty`
  = zero evidenze valide, `failed`); `queue {queued, running, failed}` del progetto.
- `EvidenceView`: id, versionId, version, isCurrent, messageId, messageSequence, ordinal, key, kind, kindRationale, statement, subject,
  action, object, scope, purpose, instrument, channel, recipient, qualifiers `[{type,text}]`, polarity, certainty,
  sources `[{messageId, sequence, role, start, end, quote}]`.
- `EvidenceDetailView`: evidence `EvidenceView`; messages `[{id, sequence, role, text}]` (gli originali citati, per l'evidenziazione);
  relations `[{id, type, basis, rationale, direction (outgoing/incoming), otherEvidenceId, otherStatement, otherMessageSequence}]`.
- `MessageVersionsView`: messageId, sequence, stateRevision, currentVersionId, versions `[{id, version, createdAt, evidenceCount,
  noEvidenceReason, promptVersion, schemaVersion, modelId, jobId, reason, isCurrent}]`, promotions `[{id, fromVersionId, toVersionId,
  promotedBy, promotedAt, reason}]`.
- `ExtractionVersionView`: versione come sopra + parameters (configurazione e contesto usato) + evidences `EvidenceView[]` +
  relations `[{id, fromEvidenceId, toEvidenceId, type, basis, rationale}]`.
- `VersionDiffView`: fromVersionId, toVersionId, messageId, summary `{added, removed, unchanged, modified, ambiguous}`, entries
  `[{status (Added/Removed/Unchanged/Modified/Ambiguous), from EvidenceView|null, to EvidenceView|null, changes [{field, from, to}],
  candidates [evidenceId] (solo Ambiguous), overlap}]`. Campi confrontati: kind, polarity, certainty, statement, subject, action,
  object, scope, purpose, instrument, channel, recipient, qualifiers, sources, relations.
- `PromotionResultView`: messageId, stateRevision, currentVersionId, promotion `{id, fromVersionId, toVersionId, promotedBy, promotedAt,
  reason, relationImpact {remapped, stale}}`. Promuovere la versione già corrente: 200 senza nuova promozione.
- `EvidenceBatchView`: id, kind, requestedAt, throughSequence, jobCount, skippedCount, progress `{queued, running, completed, failed}`.
- `EvidenceSelectionView`: runId, createdAt, viewHash, items `[{messageId, sequence, versionId, version}]`, evidences
  `[{alias, evidenceId, …EvidenceView}]`, relations `[{fromAlias, toAlias, type, basis, open, remapped}]`, staleRelations `[...]`,
  links `[{itemId, evidenceIds[]}]` (vuoto finché la revisione AI non è committata).
- `CoverageReportView`: id, analysisId, revisionId, draftId, checkerVersion, inputHash, createdAt, createdBy, counts
  `{deterministicErrors, semanticRisks, pmAssessments}`, findings `[{category (DeterministicError/SemanticRisk/PmAssessment), code,
  message, evidenceId, alias, itemId, section, confidence (solo SemanticRisk: low/medium), details}]`, limits `[string]`.

**Stato in tempo reale.** Lo stream `GET /api/projects/{p}/events` aggiunge l'evento `evidence.state` con `ExtractionJobView` a ogni
transizione (best-effort come `processing.state`); la UI riconcilia con `GET .../evidence/status` e polling ogni due secondi mentre ci
sono lavori aperti.

**Errori asincroni del lavoro**: `invalid_agent_output`, `llm_unavailable`, `model_timeout`, `model_rejected`, `model_not_available`,
`context_overflow`, `deadline_exceeded`, `attempts_exhausted`, `processing_failed`. **Errore del run di analisi**: `evidence_unavailable`
(uno o più messaggi PM entro il cutoff senza versione corrente: elencati nel messaggio redatto, senza testo).

Nessuna operazione modifica messaggi, versioni, analisi, revisioni o bozze esistenti; per aggiornare il documento il PM avvia
esplicitamente una nuova analisi, revisione, conferma e generazione.
