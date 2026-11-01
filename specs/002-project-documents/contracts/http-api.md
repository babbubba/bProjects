# Contratto HTTP — documenti e fonti 002

Disegno futuro. Base `/api/projects/{p}`; JSON camelCase, UUID, date UTC; bearer e ruolo PM come 001. Tutti i PM autorizzati vedono i progetti della baseline, nessuna ownership ACL. Ogni ID deve essere cercato sotto `{p}`: risorsa di altro progetto → 404. Endpoint Admin limiti separato, ruolo Admin; nessuna nuova UI amministrativa generale.

## Documenti

| Metodo/percorso relativo | Input | Esito |
| --- | --- | --- |
| GET /documents | cursor, limit 1–100 | 200 items/nextCursor, documentId, nome e versione applicabile, stato e motivo |
| POST /documents | multipart file, displayName opzionale; Idempotency-Key UUID | 201 DocumentVersionView e job di preparazione, originale già durevole; replay 200 stessa versione |
| POST /documents/{d}/versions | multipart file; Idempotency-Key UUID | 201 nuova versione, stessa identità documento, mai sostituzione bytes |
| GET /documents/{d} | — | 200 documento con versioni e preferenza sorgente |
| GET /documents/{d}/versions/{v} | — | 200 DocumentVersionView |
| GET /documents/{d}/versions/{v}/download | — | 200 bytes originali, attachment, filename sicuro, Content-Type verificato, nosniff, no-store |
| GET /documents/{d}/versions/{v}/preparations/{g} | — | 200 manifest, stati e issue senza prompt |
| GET /documents/{d}/versions/{v}/preparations/{g}/blocks | cursor, limit | 200 contenuto strutturato paginato, nessun HTML attivo |
| GET /documents/{d}/versions/{v}/preparations/{g}/passages/{b} | start/end opzionali | 200 testo estratto e passaggio evidenziabile, pagina/sezione/bbox disponibili e link download |
| POST /documents/{d}/versions/{v}/preparations | Idempotency-Key | 202 rielaborazione candidata, 409 se già attiva |
| POST /document-jobs/{j}/retry | Idempotency-Key | 202 nuovo job/richiesta collegato, solo se terminale fallito/incompleto e azione ammessa |
| GET /document-jobs/{j} | — | 200 stato reale, attempt, deadline, nextAttempt, progressi completati/totali, errore |
| POST /documents/{d}/versions/{v}/preparations/{g}/promote | expectedRevision del bundle DocumentVersion, reason? | 200 bundle corrente, 409 conflitto; mai promozione di incompleto |

`DocumentVersionView`: id, documentId, projectId, version, originalName, mediaType, byteLength, sha256, uploadedBy/At, processingStatus, applicationStatus, currentPreparationId, currentEvidenceVersionId, bundleRevision, candidatePreparations, latestJobId, included, selectionMode, isApplicable, previousReadyVersionId, issuesSummary, allowedActions. Parsing ed estrazione hanno stati distinti. Ready richiede copertura strutturale completa e ledger valido; empty motivato è un esito pronto senza evidenze, distinto da incompleto.

Upload stessa chiave/input diverso (hash bytes+metadata+target) → 409 idempotency_conflict; upload ancora in ricezione → 409 upload_in_progress con receiptId, nessuna falsa versione acquisita. Replay dopo crash riconcilia la stessa receipt. Nuovo caricamento deliberato con nuova chiave può creare altra versione; hash uguale non fonde documenti di progetti diversi. Nessun resume parziale byte-level promesso.

Errori sincroni: 400 multipart/chiave/revisione malformata, 401/403 auth/ruolo, 404 progetto o risorsa, 408 `upload_timeout` (ricezione del file oltre `Documents:UploadTimeoutSeconds`: nulla acquisito, ritentabile con la stessa Idempotency-Key), 409 conflitto/idempotenza/preparazione attiva, 413 file oltre limite, 415 formato non supportato o incoerente, 422 file vuoto/protetto/input non valido, 507 quota/spazio insufficiente, 503 persistenza/parser temporaneamente indisponibili. Formato/protezione scoperti dopo acquisizione sono errore asincrono: originale rimane scaricabile. Mai stack trace, storage path, endpoint riservati o contenuto completo negli errori.

## Selezione persistente e snapshot

GET `/source-selection` → `{revision, items:[{documentId,included,mode:LatestReady|Pinned,versionId?,resolvedVersionId?,preparationId?,availability}], blockingSources}` con ETag quotato della revisione.
PUT `/source-selection`, If-Match obbligatorio, corpo items completo → nuova revisione; missing If-Match 400, obsoleta 409 source_selection_conflict con currentRevision, versionId di altro documento/progetto 422/404. LatestReady risolve l'ultima versione completa; nuova versione non pronta non rimuove la Ready precedente. Pinned consente scelta esplicita della versione non pronta, ma impedisce Analizza finché non pronta/esclusa. Esclusione resta anche con nuove versioni. Nessuna modifica di run o conferme esistenti.

POST `/analyses` mantiene Idempotency-Key e endpoint esistente, senza prompt/input AI client. Per nuove richieste risolve snapshot documentale sotto lock alla richiesta; documento incluso non pronto → 409 sources_not_ready con elenco versione/stato/azioni e nessun run creato. Nessun documento pronto e nessun messaggio PM → 422 no_source_input; solo documenti pronti → cutoff 0 nel ramo 1.1. Snapshot del dialogo rimane cutoff rigoroso 001 e gate Evidence PM pendenti invariato. Replay restituisce il run originario prima di ricalcolare la selezione attuale; non è un nuovo comando.

POST `/messages` conserva testo, clientMessageId e salvataggio immediato. Crea ChatTurn con snapshot fonti anche quando indisponibili: il turno conserva il messaggio e segnala fonti non pronte senza inventarne contenuti. Non attende che diventino pronte e non allarga lo snapshot al retry. Le fonti pronte restano utilizzabili solo dichiarando quali fonti sono indisponibili; una domanda dipendente da queste non riceve risposta documentale inventata.

GET `/source-snapshots/{s}` → manifest di selezione storica; accesso soltanto nel progetto. Viste ChatTurn, ProcessingRun, Analysis e Draft aggiungono sourceSnapshotId, hasSourceChanges e fonti incluse/escluse consultabili. Cambi di fonte non alterano validationState storico né canGenerate sulla revisione corrente già confermata: generare quella revisione usa le sue fonti vecchie con avviso visibile.

## Contratti esistenti ed Evidence

Endpoint edit/confirm/document-generations invariati nei comandi e negli If-Match; `content.schemaVersion` guida 1.0/1.1. PUT deve mantenere la versione di schema della revisione base; non convertire una vecchia analisi per aggiungere un documento, richiedere nuova Analizza. Versione ignota → 422 unsupported_schema_version. Client aggiornato supporta entrambe, nessuna promessa di supporto 1.1 nel vecchio frontend.

Viste Evidence aggiungono origin.kind e fonte documentale solo nel payload documentale versionato; percorsi legacy `/messages/{m}/evidence/...` mantengono significato. GET `/evidence` aggiunge filtri originKind/documentId/documentVersionId; dettaglio espone origin e sources discriminate. Per nuove versioni di estrazione documentale, confronto/promozione restano nella vista Evidence: route `/documents/{d}/versions/{v}/evidence/versions` e `/evidence/versions/{a}/compare/{b}`, confronto ammesso solo sulla stessa Preparation o mapping strutturale verificabile, altrimenti 422 versions_not_comparable. Nessun matching semantico automatico fra file diversi.

Report `/analyses/{a}/coverage-reports` riusato: coverage-2 contiene sourceSnapshotHash, preparation manifest, range mancanti, collegamenti e categorie deterministico/stimato/PM; report coverage-v1 storici restano leggibili. Aggiungere GET `/documents/{d}/versions/{v}/preparations/{g}/coverage` per lo stato di preparazione prima dell'analisi; nessun nuovo checker concorrente. GET non avvia lavori.

## Stato, sicurezza e monitor

Evento `document.state` sullo stream progetto esistente, payload job/versionId/preparationId e stato. Best-effort, riconciliazione GET e polling 2s; niente replay token promesso, percentuali inventate o nuovi comandi alla riconnessione. `chat.delta` rimane provvisorio; citazioni della risposta definitiva validate prima del commit, frammenti streaming non sono evidenze.

`context-usage` conserva campi e provenienza dei valori, aggiunge composizione document_sources e snapshot fonti; quando modello di fase differisce mostra snapshot runtime pertinente, non cifra del globale attribuita al modello diverso. `InferenceMetrics` aggrega le chiamate del tentativo vincente, esponendo anche dettaglio per unità/fase; tentativi falliti restano diagnostica redatta. Storici null restano unavailable, nessun backfill inventato.

GET `/api/admin/document-limits`, PUT stesso percorso con If-Match e ruolo Admin espongono soltanto limiti non segreti di plan; applicazione atomica e validazione positività/coerenza. Nuovi lavori congelano config; quote correnti controllate a ogni acquisizione. Nessun endpoint può cambiare modello, path storage o parser command tramite contenuto documentale. Nessun endpoint delete, KB, ricerca o pubblicazione aggiunto.


## Regimi automatici, deadline e UX (D1/F1/F7)

POST messages/analyses/document-generations mantiene un unico comando funzionale e la stessa idempotenza. Nessun parametro regime, timeout o profondità accettato dal PM. Backend registra snapshot, t0 e classificazione senza attendere il LLM; risposta asincrona rimanda al medesimo ChatTurn/ProcessingRun. Preparazione avviata dall'upload sempre asincrona con limite root di 3600 s iniziali, non una durata prevista. Ramo 001 senza documenti invariato.

Nuovi campi nelle viste documentali di job/root: `execution:{initialRegime:Ordinary|Extended,regime:Ordinary|Extended,classificationStatus:Pending|Classified|Rejected,classificationVersion,classificationRecordHash,reason,requestedAt,ordinaryDeadlineAt:null|timestamp,hardDeadlineAt,effectiveDeadlineAt,promotionDueAt:null,promotedAt:null|timestamp,promotionReason:null|additional_work,elapsedMs,queueMs:null|integer,processingMs:null|integer,stage,completedUnits,plannedUnits:null|integer}`. ClassificationVersion document-regime-v2 con PolicyHash della correzione finale B1; Plan §Classificazione è normativo. Pending usa Extended provvisorio/preflight_pending senza inferenza; la prima classificazione tecnica fissa initialRegime e deadline dal t0, senza soglia temporale di promozione. Se la deadline determinata è già raggiunta termina Failed prima del claim. PromotionDueAt resta null. Valori misurati con disponibilità/provenienza dichiarate; queueMs e processingMs seguono gli intervalli distinti di Plan §Misure, non Running o durata del lease. plannedUnits aumenta solo dopo pianificazione effettiva di output intermedi. Nessun parametro regime/timeout/profilo prestazionale client.

Extended o passaggio Ordinary→Extended: descrizione «L'elaborazione richiede più tempo e prosegue in background; puoi continuare a usare l'applicazione». Non «attendi un'ora», non conferma aggiuntiva. Stream progetto aggiunge `execution.regime-changed` con rootId/at/reason/effectiveDeadlineAt; best-effort come gli altri eventi. GET espone sempre l'ultimo regime e consente riconciliazione dopo perdita evento o logout/ritorno. La nuova modalità non impone di tenere la pagina aperta né abilita comandi concorrenti vietati nel medesimo progetto; il PM può navigare e consultare i dati.

Regole temporali autoritative in Plan §Regimi: una sola promozione prima della scadenza corrente, hard cap dal t0 originale, nessun reset dopo riavvio/cambio regime. Se scade, errore persistito con motivo e copertura non completata, niente risposta/anteprima/bozza falsamente riuscita. Retry manuale esplicito crea nuova richiesta collegata dopo il terminale; non fa ripartire automaticamente la vecchia.

Nessun timer promuove un Ordinary a 120 s o per sola coda/inferenza lenta. Promozione automatica soltanto per additional_work con ricevuta server di piano derivato valida secondo Plan §Promozione: output già Committed, obbligazioni di copertura residue e almeno un nuovo batch AI necessario senza output compatibile riutilizzabile. Evento e GET espongono additional_work e riferimento/hash della motivazione tecnica, senza prompt; DeadlineAt ed effectiveDeadlineAt cambiano atomicamente nel medesimo root. La chiamata attiva conserva il timeout assegnato, i retry/riprese mantengono t0 e hard cap originari. Terminale/scadenza prevalgono come in Plan; nessuna promozione da input/output invalidi o per semplice attesa di commit. Deadline/limiti esauriti → Failed con copertura residua, mai Completed parziale.

Profilo di accettazione N/E e regime operativo sono distinti. Per le prove N conta il risultato completo valido e il tempo totale dal comando: a 150 s con tutti gli altri criteri soddisfatti PASS, anche dopo promozione tecnica; oltre 180 s FAIL prestazionale anche se un Extended completa funzionalmente. Il profilo fissato prima della prova non cambia con il regime. GET/telemetria conservano durata e risultato reali, senza attribuire il rispetto dei 180 s al solo Completed. Un Ordinary senza promozione valida scade a 180 s e rifiuta il commit tardivo; la coda resta visibile come attesa.

Riuso/recovery è applicativo e non richiede nuovi endpoint o scelte PM: GET non invoca il modello, replay della chiave legge lo stesso root; checkpoint arretrato riusa WorkOutput Committed compatibili prima dell'inferenza (AI §5). Retry esplicito terminale crea nuovo root con retryOfId e riferimenti di riuso verificati, mai reset del vecchio. Il rapporto di copertura coverage-2 comprende anche obbligazioni/candidati/patch non trattati di AI §4.5, con ID/motivo e senza prompt. Un conflitto preservato integralmente è visibile come punto aperto; un processo di riconciliazione incompleto non pubblica una nuova revisione completa.

## Bundle corrente e payload Evidence (F4/F6)

`currentPreparationId` e `currentEvidenceVersionId` sono entrambi null prima del primo successo; `bundleRevision` è 0 o la revisione crescente dello state della versione file. `candidatePreparations:[{preparationId,generation,processingStatus,evidenceVersionId:null|UUID,createdAt,canPromote}]` distingue candidate complete e fallite. GET preparazione riporta anche applicationStatus Current/Candidate/Superseded/Unselected, issues e allowedActions; Ready è solo processingStatus. UI non usa l'ultima riga Ready come fonte per propria iniziativa.

LatestReady sceglie la versione FILE massima avente bundle corrente completo; la preparazione corrente è quella dello state di quella versione, non la massima generazione Ready. Pin del PM riguarda la versione file, usa il suo bundle corrente al nuovo comando; snapshot precedenti mantengono il vecchio bundle. Promozione candidata richiede expectedRevision di quel DocumentVersion state, distinta dall'ETag della selezione progetto. Transazione cambia insieme parse ed estrazione, ritorna `{documentVersionId,bundleRevision,currentPreparationId,currentEvidenceVersionId,promotion}`. Replay di bundle già corrente → 200 senza nuovo evento; CAS obsoleto verso un altro bundle → 409 promotion_conflict con stato attuale. Nessuna promozione indipendente della sola estrazione documentale.

Le viste Evidence adottano il contratto chiuso AI §2. Richiesta `viewVersion=1.1.0` per lista/dettaglio/confronto aggiornata: lista `{items:[{evidenceViewVersion:"1.0.0"|"1.1.0",evidence}],nextCursor}`; dettaglio `{evidenceViewVersion,evidence,relations,sourceDocuments,sourceMessages}`. `evidence` 1.1 include id/versionId/projectId/ordinal/key/kind/polarity/certainty/statement/content con content completo di AI §2.2 e origin documentale, nessun campo omesso sostituito da MessageId fittizio. `sourceDocuments` risolve ogni sourceId in versione/preparazione/artefatto/blocco/locator e link al passaggio/download; versioni storiche restano risolvibili. Il client discrimina evidenceViewVersion e gestisce campi nullable, mai inferisce la provenienza dal testo.

Senza viewVersion oppure con 1.0.0: forma legacy invariata, origine Message soltanto; richiesta esplicita di origine Document o ID documentale con formato legacy → 406 evidence_view_not_supported, non un elemento deformato. Endpoint nuovi documentali restituiscono formato 1.1 esplicito. Formato ignoto → 400 unsupported_view_version. Nuovo frontend richiede 1.1 per vedere entrambe le origini; nessuna modifica dei JSON 1.0 persistiti. Relazioni hanno ID del run/estrazione e base attribuita; i locator sono derivati dai dati del server, non dall'AI.

Ogni citazione multi-blocco è presentata come parti ordinate (citationGroupId,part,sourceId), con quote e locator di ogni parte: API passaggio resta atomica per blockId, UI gruppo apre tutti i riferimenti. Gli header replicati rimandano al blocco originale. AnalysisContent usa più evidenceRefs, non una citazione letterale fittizia che fonde pagine diverse.
