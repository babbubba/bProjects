# Data model — 002-project-documents

Disegno futuro, nessuna migrazione eseguita. PostgreSQL/EF Core esistenti; UUID, timestamp UTC da DB, JSONB per manifesti versionati. Tutti gli owner hanno ProjectId e FK composte per impedire riferimenti fra progetti. Originali/parse/estrazioni/snapshot selezionati sono append-only; stato corrente e lease sono proiezioni mutabili con revisione. Riferimenti progetto/file non equivalgono a nuove ACL.

## Documenti, storage e preparazione

> Il delta T130/N4 seguente è pianificato, non presente nel codice a 65a9204. Non muta le entità storiche qui sotto.

### Delta derivato T130/N4 (2026-11-07)

Summary coverage-3 aggiunge expectedGaps/assessedGaps/answeredGaps/openGaps/unassessedGaps, non negativi; expected=assessed+unassessed e assessed=answered+open. Distinti da supporto item. Nessun Gap validamente valutato è evidence_unrepresented soltanto perché privo di item sostantivo; per non-Gap la regola precedente resta invariata.

Normativa [AI §13](contracts/ai-workflow.md#13-revisione-approvata-t130n4--a--c-minima-2026-11-07), solo run v5. Nessuna nuova tabella/migrazione: JSONB WorkOutput e CoverageReport esistenti, schemi e decoder versionati. Ledger, messaggi e AnalysisContent 1.1.0 invariati.

**GapAssessment `gap-assessment-1`**: `gapEvidenceId`, `gapEvidenceVersionId`, `sourceSnapshotHash`, `outcome` (`AnsweredInSnapshot|Open`), `reason` (`Answered|NoAnswer|Partial|Ambiguous`), `rationale` stringa non vuota, `supportingEvidenceIds` distinti, `openItemId`/`openConflictId` nullable, `originBatchId`. Liste `gapAssessments` obbligatorie nei nuovi DTO; server risolve alias/versioni dal catalogo. Vincoli AI §13.2: Answered ⇒ reason Answered, almeno un supporto non-Gap dello snapshot, riferimenti aperti null; Open ⇒ reason NoAnswer/Partial/Ambiguous, esattamente un riferimento aperto finale, supporti obbligatori salvo NoAnswer. Missing ⇒ gap_not_assessed; record/riferimenti invalidi ⇒ work_output_invalid. La pertinenza resta giudizio semantico.

**WorkOutput v5**: DTO/catalogo/canonico più valutazioni canoniche e `gapAssessmentsHash`. Hash output/composizione/coverage comprendono i campi nuovi. Nei nodi gerarchici valutazioni e provenienza batch secondo AI §13.4, mai union implicita dei supporti; divergenze ⇒ gap_assessment_unresolved. Assembly esige un esito finale per Gap e riferimenti finali validi. Niente archivio parallelo o mutazione di output Committed.

**Marker retry v5**: DerivedBatch analysis_retry unico/fenced senza WorkOutput; metadati tipizzati, firstOutputHash, retryPayloadHash, misura/qualità, nessun testo respinto. Marker solo dopo preflight valido, non riabilitato dal crash. InputHash originario distinto dal retryPayloadHash.

**CoverageReport coverage-3**: proietta i record dalla revisione AI originaria (AiRevisionId), distinguendo snapshot/versioni/batch, supporti item, revisione PM corrente e categorie deterministiche/semantiche/PM. Include gapAssessmentsHash nell'identità input. Edit/conferma non mutano valutazioni o vecchi report. Coverage-v1/coverage-2 restano leggibili senza retrocompilazione.

`DocumentPolicy` conserva la configurazione Admin non segreta con Revision,
UpdatedBy/At e limiti validati, una sola riga globale. I default iniziali provengono
dalla configurazione server; gli aggiornamenti Admin sono persistenti, atomici e
versionati. Le nuove richieste congelano il policy hash, quelle attive conservano
i propri timeout/limiti; la quota spazio reale resta verificata a ogni acquisizione.

| Entità nuova | Campi e relazioni principali | Vincoli |
| --- | --- | --- |
| ProjectDocument | Id, ProjectId, DisplayName, CreatedBy/At, LatestReadyVersionId | Nome non identità, no unique sul nome; puntatore Ready solo a versione dello stesso documento |
| DocumentVersion | Id, ProjectId, DocumentId, Version, OriginalName, MediaType, ByteLength, Sha256, StorageKey, UploadedBy/At, UploadReceiptId | Unique(DocumentId,Version), original bytes immutabili, StorageKey generata/univoca, size >0, FK Restrict |
| UploadReceipt | Id, ProjectId, DocumentId opzionale, RequestKey, InputHash, ReservedBytes, Status Receiving/Committed/Aborted, ExpiresAt, ResultVersionId | Unique(ProjectId,RequestKey); input hash comprende file+metadata+target; acquisizione solo Committed con versione/job; contesa quota serializzata |
| DocumentPreparation | Id, ProjectId, DocumentVersionId, Generation, ParseJobId, ParserVersion, ConfigHash, ManifestHash, Status, CurrentEvidenceVersionId, ErrorCode/Message, CreatedAt | Unique(versionId,generation), una generazione aperta/versione; Ready solo dopo tutte le porzioni e ledger validi |
| DocumentJob | Id, ProjectId, PreparationId, Kind Parse/ExtractUnit, UnitId opzionale, RequestKey, RetryOfId, Status, RequestedBy/At, Started/Completed/DeadlineAt, AttemptCount/MaxAttempts/NextAttemptAt, LeaseOwner/Until/FencingToken, ConfigJson, WorkflowDefinitionVersion | Stessa macchina lease 001, unique owner/unità logica, Completed richiede risultato; Failed richiede errore. Parse è CPU, ExtractUnit è AI; commit finale solo EvidenceExtractionJob documentale |
| ParsedArtifact | Id, ProjectId, PreparationId, SchemaVersion, StorageKey, Sha256, ParserConfigHash, BlockCount, IssueCount, CreatedAt | Una pubblicazione sotto il fence del job Parse per preparazione, originale Docling JSON + manifest normalizzato immutabili |
| DocumentBlock | Id, ProjectId, ArtifactId, Ordinal, ParentId, Type, CanonicalText, TextHash, HeadingPath, Page/Bbox nullable, DoclingRef o SourceLineRange, TableCellsJson | Unique(ArtifactId,ordinal/ref), ordine di lettura; no pagina inventata; celle con righe/colonne e span mantenuti |
| DocumentPortion | Id, ProjectId, PreparationId, Ordinal, RangesJson, ContextRangesJson, TextHash, Status, JobId, ResultHash, NoEvidenceReason | Ogni range assegnato; copie di header/contesto marcate non proprietarie; unità originale non modificata da retry |
| PreparationIssue | Id, PreparationId, BlockId/Page nullable, Code, Severity, Reason | Pagina immagine/informazione non trattata rende Incomplete; niente percentuale fittizia |

StorageKey non accettata dal client; percorso risolto sotto root privata senza symlink/path traversal. Una chiave non è condivisa fra progetti per deduplica globale. ByteLength verificato dallo stream reale, hash verificato al download e prima del parsing. Quota su tutte le versioni e receipt riservate, derivati con contatore separato; nessun decremento per sola esclusione. Tutti gli oggetti referenziati protetti dallo sweep.

Upload: riserva quota+receipt sotto lock progetto → stream temporaneo limitato → hash/verifica formato → flush+rename sul volume privato → transazione versione+Parse job+receipt Committed. Crash dopo rename prima del DB lascia receipt riconciliabile, mai falso successo; crash prima di rename lascia upload non acquisito. Reconciler verifica hash e receipt, finalizza solo input già validato e metadata completi, altrimenti Aborted e pulisce il temporaneo/orfano dopo scadenza. Non cancella un file con riferimento DB. La receipt consente retry ma non promette resume byte-level del caricamento.

Preparazione: Queued → Parsing → Extracting → Ready; Parsing/Extracting → Failed (errore) oppure Incomplete (porzioni note non trattate/limite). Stati job Queued/Running/Completed/Failed distinti dalla proiezione preparazione e dal puntatore corrente. Ready non equivale a promossa (vedi lifecycle F6). Retry esplicito di terminale crea una richiesta collegata e riusa solo artefatti completi con hash/config compatibili; risultato di generazione già completata non sovrascritto. Nuovo parse/config produce nuova Preparation. Ready richiede manifest inventariato, zero issue bloccanti e una versione Evidence completa; zero evidenze richiede motivazione di tutte le porzioni.

## Estensione del ledger esistente

Nessuna tabella alternativa DocumentEvidenceItems. Modificare le entità Evidence esistenti con discriminante OriginKind Message/Document; per Document la sorgente di estrazione è una Preparation del documento, non ogni chunk come messaggio.

| Entità esistente | Delta |
| --- | --- |
| EvidenceExtractionJob | MessageId/MessageSequence nullable, PreparationId nullable; XOR Message oppure Preparation; unico owner aggregatore documentale, claim solo dopo unità valide; non occupa lease/slot durante estrazioni (vedi §Commit aggregato) |
| EvidenceExtractionVersion | MessageId/MessageSequence nullable, PreparationId nullable, OriginKind, manifest hash; Unique per owner+Version, JobId unico; schema documentale evidence-1.1.0 |
| EvidenceItem | MessageId nullable, PreparationId nullable, OriginKind; VersionId/ProjectId restano; stessi tipo/polarità/certezza; Content 1.1 con sources discriminate |
| EvidenceRelation | Stesse FK verso EvidenceItem, relazioni documentali e PM-documento ammesse solo in selezione/run compatibile; basi dichiarata/inferita conservate |
| EvidenceMessageState/Promotion | Righe storiche intatte; aggiungere EvidenceDocumentState/DocumentPromotion per puntatore alla preparazione ed estrazione correnti della DocumentVersion, stessa semantica candidatura/promozione/concorrenza, non altro registro |
| EvidenceCoverageReport | CheckerVersion coverage-2, report JSON comprende manifest/porzioni e nuove fonti; report coverage-v1 immutati |

Ogni source Content documentale identifica documentId/versionId/preparationId/artifactId/blockId/start/end/quote e locator verificato dal backend. Range [start,end) UTF-16 su CanonicalText immutabile, non sul PDF binario; la citazione è presentata come testo estratto collegato all'originale e pagina/box disponibili. Markdown/TXT hanno anche righe/range del sorgente, DOCX item path strutturale. L'AI non assegna pagina o bbox. Distinguere OriginKind documentale da EvidenceCertainty: un'ipotesi trovata in un documento non è fatto confermato.

Estratto parziale resta output di unità non pubblicato nel ledger corrente: una sola EvidenceExtractionVersion aggrega tutti i risultati validi della Preparation sotto il fence del relativo EvidenceExtractionJob. EvidenceItem IDs assegnati stabilmente al primo output validato, unique(versionId,key) e ordinale stabile; overlap non crea doppio item per stesso passaggio/fatto, fonti multiple conservate. Se fallisce l'ultima unità nessuna versione completa fittizia viene promossa.

Le relazioni cross-documento/PM nate durante Analizza sono conservate come relazioni interpretative del run (AnalysisEvidenceRelations: runId, from/to EvidenceId, type/basis/rationale), non modificano estrazioni pregresse. Vincoli di progetto e selezione su entrambi i bersagli.

## Selezioni, run, risultati intermedi

| Entità | Disegno |
| --- | --- |
| ProjectSourceSelection / Item | ProjectId, Revision, UpdatedBy/At; item DocumentId, Included, Mode LatestReady/Pinned, PinnedVersionId nullable; unique(ProjectId,DocumentId), confronto Revision obbligatorio |
| SourceSnapshot / Item | Id, ProjectId, SelectionRevision, Hash, CreatedAt; tutte le inclusioni/esclusioni, DocumentVersionId, PreparationId, EvidenceVersionId nullable e Availability alla richiesta; solo Ready ha estrazione selezionabile |
| ChatTurn / ProcessingRun | SourceSnapshotId nullable per legacy; selezione documentale fissata alla richiesta sotto lock progetto, mai al claim; documento non pronto non entra dopo. Cutoff del dialogo separato |
| AnalysisEvidenceSelectionItem | Estendere con discriminante Message/Document e riferimento evidence version, XOR nullable MessageId / PreparationId; storico preservato |
| WorkflowCheckpoint | ProcessingRunId nullable; aggiungere ChatTurnId e DocumentJobId nullable con XOR e FK/progetto; writer fenced per owner; stessi payload MAF opachi |
| InferenceCalls | Aggiungere DocumentJobId nullable come quarto owner; XOR esteso e indici finali per owner, Phase resta Extraction/Chat/Analysis/Document/Compaction |
| WorkManifest | OwnerKind/Id, ConfigHash, SourceHash, RuntimeFingerprint, DefinitionVersion, PlanJson, Hash |
| WorkOutput | OwnerKind/Id, BatchId, NodeId (executor ID), InputHash, OutputHash, PayloadJson/StorageKey, CoveredIdsJson, SchemaVersion, ManifestHash, Status Committed, ValidationVersion, CommittedAt, CommittedFence, DerivedFromOutputHash nullable |

WorkManifest iniziale immutabile congela fonti, policy, algoritmo e topologia fissa, non tutti i futuri batch. DerivedBatch append-only dipende da output validati già persistiti; WorkOutput univoco(owner,batchId,inputHash), non soltanto nodeId perché lo stesso executor può ricevere più batch. Checkpoint nativo conserva cursori/passate/output hash; il replay rilegge output e figli, senza duplicare effetti. Vedi AI §5.

AnalysisRevision/Draft esistenti restano autorità del risultato finale. SchemaVersion già presente: 1.0.0 o 1.1.0 con decoder dedicato. Pubblicazione di una revisione completa o bozza completa e Completed è atomica; nessun output intermedio diventa anteprima riuscita. Fonti di edit PM restano PmRevision e non cambiano gli originali.

**Nota T125 (2026-11-03)**: per le analisi documentali v3 il payload del WorkOutput contiene, oltre al canonico validato, il DTO AI con alias (`aiOutputHash`), il catalogo immutabile alias→(EvidenceId, SourceId, Document/Version/Preparation, Artifact/Block, range, citazione, CitationGroupId/part, provenienza, relazioni note) con `catalogHash` e il `canonicalHash` composto; `SchemaVersion` del WorkOutput `document-analysis-alias-1`. Gli id di item e conflitto sono UUID deterministici (`canon-ids-1`) da (root, sourceHash, chiave locale), non casuali né del modello. Nessuna nuova tabella o colonna; WorkOutput e run v2 storici restano con la propria definizione e non sono riscritti. Vedi [AI §7](contracts/ai-workflow.md).

## Migrazioni e compatibilità

1. Creare tabelle documentali e snapshot con FK composite progetto, indici univoci/partial per lavori aperti, lease/deadline/attempt coerenti; nessun dato 001 modificato.
2. Espandere le tabelle Evidence e owner checkpoint/InferenceCalls; rendere nullable i soli campi Message del ramo documentale. Backfill OriginKind=Message sulle righe esistenti; JSON, ID, versioni, source span, hash e conferme 001 invariati. CHECK XOR e completed-result, FK Restrict, indici legacy filtrati su Message, nuovi su Document.
3. Registrare dispatcher/decoder compatibili 1.0/1.1 e grafi versionati; vecchi run mantengono topologia/prompt/options e politica 001, non vengono convertiti in 002 al resume. Le nuove analisi con documenti adottano il nuovo schema; cutoff=0 ammesso soltanto con snapshot documentale utilizzabile.
4. Provare creazione da zero e upgrade da DB 001 con dati, run/bozze confermati e checkpoint. Confronto payload storici tramite normalizzazione JSON canonica e hash, non formattazione JSONB; download bytes tramite SHA-256.

Ordine lock uniforme: progetto/selezione → conversazione → owner job/run → versione. Transazioni corte, mai lock SQL aperto durante upload/Docling/LLM. Advisory lock claim riusato per limite AI globale. SQL check non basta per invariant cross-row: validazione server nella stessa transazione con lock/FK e test concorrenza reali.

Deployment stop-the-world della app per espansione e versione software compatibile, backup DB+volume coerente prima di migrare; nessuna migrazione automatica startup. Down distruttiva non usata in produzione: una volta presenti dati documentali/1.1, vecchio binario 001 non è rollback sicuro; usare restore coerente DB+volume oppure release compatibile correttiva. Nessun annullamento che perda fonti referenziate.


## Campi temporali e manifesti derivati (F1/F2)

Preparation, ChatTurn e ProcessingRun documentali contengono i campi root di Plan §Regimi: RequestedAt, InitialRegime, Regime, ClassificationStatus Pending/Classified/Rejected, ClassificationVersion/Reason, PolicyHash, OrdinaryDeadlineAt nullable per preparazione, HardDeadlineAt, EffectiveDeadlineAt, PromotionDueAt nullable per preparazione, PromotionCount, PromotedAt/Reason nullable, CallCount, RetryOfId. CHECK cap finito e Effective≤Hard; PromotionCount in 0/1. Per i nuovi ChatTurn/ProcessingRun documentali CHECK DeadlineAt=EffectiveDeadlineAt: la proiezione legacy cambia atomicamente con la promozione, non il cap Hard. Claim/sweep/renew/release/commit/checkpoint leggono l'autorità attuale, nessun timer root basato sul vecchio claim. Record legacy hanno nuovi campi nullable e mantengono DeadlineAt/regole 001. Nuovi comandi senza documenti mantengono il ramo storico. Tabella RegimeTransition append-only (rootId,from,to,at,reason,oldDeadline,newDeadline,elapsedMs,policyHash) con unique(rootId,to=Extended) evita reset/promozioni multiple. Nessun campo modificabile dal PM.

ClassificationRecord append-only: owner, ordinal, version=document-regime-v2, inputHash, policyHash, observedAt, outcome AdmitOrdinary/AdmitExtended/Reject/Fail, reason, runtimeFingerprint, W/O/M/B/T con provenance, shapeFloorTokens/shapeSkeletonHash/visibleReserve nullable, plannedCalls nullable, unknownReasons. Unique(owner,ordinal); decisione riproducibile dalla snapshot dei dati osservati, senza usare velocità/durata LLM come predicato. Pending nasce prudenzialmente Extended, prima classificazione può fissare InitialRegime Ordinary solo prima del lavoro e prima di PromotionDueAt; dopo Classified nessun downgrade. Reclassificazioni e transizioni preservano il record iniziale. Policy/limiti validati come Plan, non default divergenti nel DB.

DocumentJob ha PreparationId come parent root; DeadlineAt non supera HardDeadlineAt e si controlla insieme a EffectiveDeadlineAt attuale del padre. Aggregatore documentale EvidenceExtractionJob stessa relazione padre. Lo sweep del padre inibisce i figli e li rende Failed con parent_deadline_exceeded quando riconciliati. Tutti i claim/renew/commit includono la condizione di vita del padre: una relazione FK da sola non basta. Timeout locale del tentativo è distinto dal lease, non cambia la deadline root. Per profili e formule usare Plan, non creare un secondo set di default nel DB.

WorkManifest aggiunge AlgorithmVersion, FrozenSourceHash, PolicyHash e RuntimeFingerprint. DerivedBatch: ownerKind/ownerId/batchId, stage, view/pass/level, ordinal, parentOutputHashes ordinati (o root hash per iniziali), inputIds/ranges, measuredTokens/tokenProvenance, inputHash, schema/promptVersion, createdAt; unique(owner,batchId), append-only. Il BatchId è hash tipizzato dell'identità di elaborazione di AI §5.2; nessun Guid nuovo al replay. WorkOutput chiave owner+batchId+inputHash e validazione fenced, payload/resultHash/coveredIds/relationDisposition, mappe item/source e ricevuta di espansione. Unique(owner,batchId) impedisce anche inputHash diverso sullo stesso batch; hash discordante è errore, non update. Payload completo durevole prima del riferimento DB; diagnostica incompleta non è WorkOutput Committed.

Preparare batch è trasformazione dei dati nel grafo MAF, non schedulazione di executor; nessuna tabella NextExecutor. Pubblicare WorkOutput e figli già determinabili/ricevuta di espansione nello stesso commit fenced; figli dipendenti da più output si pubblicano nel nodo PrepareBatch solo dopo tutti i genitori, con unique e parent hashes. QueueStateUpdate del riferimento MAF seguito da checkpoint; i due commit non sono atomici fra loro. Lookup prima dell'agente **e** guard all'ingresso InvokeBatch impediscono reinferenza dopo commit anche con checkpoint che ripresenta un vecchio Miss. Sessioni AIAgent nuove per ogni miss/invio, senza storico cumulativo; non si persiste uno scheduler di sessioni. WorkflowSessionId rimane stabile sul root, AgentSession non è identità di dominio né resume di token in volo.

Ogni invio ha ricevuta InferenceCalls/contatore root riservata prima della rete con batchId, inputHash, attempt e fence. Un invio incerto dopo crash consuma budget; output non committato può richiedere nuova inferenza. Worker corrente riusa output validamente committati da fence precedenti; fence vecchio non può scriverne nuovi. RetryOfId non implica compatibilità: riuso cross-root solo dopo confronto di tutte le componenti di AI §5.3, con nuova ricevuta di import e DerivedFromOutputHash; ID/fonti storici restano invariati, mapping dei soli ID run deterministico. Un risultato legato al run non rimappabile non si importa.

## Cataloghi, candidati e patch del run (B3)

Schemi normativi e procedura in AI §4.1–4.5, versione reconcile-1/global-reconcile-v1. Persistenza in WorkOutput/DerivedBatch e proiezioni FK minime, non duplicazione di EvidenceItem.Content. CatalogEntry referenzia EvidenceItem/Version e sourceId/hash originali; CatalogNode/Page/Set referenziano membership esatta, key pages e parent hash. Pagine ordinal contigue e hash concatenati; union disgiunta dei membri per livello/vista, cardinalità verificata. Ordinamento/canonicalizzazione/hash identici ad AI §4.1; nessun hash della sola sintesi.

RunCandidate: runId, candidateId, payload reconcile-1, inputHash; unique(runId,candidateId), provenienze multiple append-only. CandidateDisposition versionata per passaggio con parent/child IDs e comparisonOutputHashes; Expanded richiede espansioni complete e figli terminali per la completezza finale. Relazioni Evidence già note sono obbligazioni obbligatorie, non eliminate perché il modello non le ripropone. Pending/NotProcessed residui impediscono Completed. CatalogSet è solo membership per routing esaustivo, non relazione di dominio né indice condiviso.

RunPatch: runId, patchId, baseHash, targetId/expectedHash, payload, originOutputHashes; unique(runId,patchId), immutabile. PatchDisposition: Applied/Duplicate/PreservedAsConflict/Stale/Invalid/NotProcessed con resultHash e reason; incompatibilità è registrata nel gruppo con tutte le patch e il conflict item risultante. Soltanto le prime tre sono terminali ammesse per completare, con alternative/fonti intatte. Stessa patchId/payload diverso è integrity_conflict. Tutte le patch di un passaggio guardano la medesima base immutabile, composizione insiemistica, niente priorità per ordine/ultimo writer. Risoluzioni PM soltanto nelle revisioni/decisioni già applicative, non nelle patch AI.

ReconciliationCoverage nel WorkOutput/report coverage-2: expected/visited EvidenceIds per A/B, pagine e livelli attesi/prodotti, relazioni note, candidate/disposition IDs, patch/disposition IDs, mappe Evidence→item, frontier mancante, round/level/call/byte count e hash. Dati calcolati dal server, non Complete restituito dal modello. Max livelli 32, candidati 100000 e un solo round finale congelati nel WorkManifest insieme ai limiti Plan; raggiungerli con residui rende coverage_incomplete. Un conflitto Open/PreservedAsConflict rappresentato integralmente non è un errore di copertura né una decisione automatica sul fatto.

## Commit aggregato Evidence (F3)

Un solo EvidenceExtractionJob OriginKind=Document per PreparationId (unique partial), risultato una sola EvidenceExtractionVersion per JobId. Gli ExtractUnit sono DocumentJob distinti, non EvidenceExtractionJob concorrenti. L'aggregatore resta Queued non eleggibile finché parse e unità non hanno output validi (il remapping locale di key/overlap è deterministico; la riconciliazione globale semantica appartiene al run Analizza), oppure viene chiuso Failed se padre/figlio necessario fallisce. Nessun lease dell'aggregatore tenuto vivo aspettando i figli; non conta come inferenza attiva. La claim finale usa la stessa infrastruttura lock/owner/fence/lease e decide lavoro deterministico senza prenotare slot DGX per un'ora.

Il solo worker proprietario del lease corrente dell'aggregatore può pubblicare. Transazione corta con ordine progetto → Preparation → EvidenceExtractionJob → DocumentVersion/current state. Sotto lock controlla: padre attivo, deadline root e job non trascorse sul clock DB, owner/fence del job corretti e lease valido; tutte le unità attese Completed con hash/input/config coerenti, nessuna issue bloccante, copertura range completa. Calcola la numerazione sotto lock e inserisce versione estrattiva, item, sources/gruppi e relazioni append-only; imposta ResultVersionId+Completed dell'aggregatore e Ready+risultato della Preparation. Se prima preparazione completa della versione file, aggiorna atomico anche il puntatore corrente e promozione automatica tracciata; se candidata, non lo cambia. Tutto o nulla.

La richiesta successiva col medesimo job Completed legge lo stesso risultato. Se il commit è avvenuto ma il checkpoint/trasporto fallisce, Completed prevale e non si inserisce un'altra versione. Unique(JobId) e unique(ownerVersion,ordinal/key) proteggono i replay; un conflitto di pubblicazione rilegge il risultato già committato. Worker con fence scaduto non può né scrivere item/Ready/promozioni né pubblicare un evento definitivo: il risultato AI eventualmente valido non gli restituisce autorità. Eventi best-effort soltanto dopo commit, GET resta autorevole.

Gli output parziali validi sono WorkOutput immutabili, non versioni correnti Evidence. Una Preparation Incomplete/Failed non ha versione completa fittizia; eventuali estrazioni parziali sono consultabili come diagnostica di copertura, mai fonti Ready. Vecchie versioni correnti e storici non vengono aggiornati/cancellati dalla nuova pubblicazione.

## Lifecycle applicabile e preparazioni candidate (F6)

Separare due stati ortogonali: ProcessingStatus Queued/Parsing/Extracting/Ready/Incomplete/Failed e ApplicationStatus Unselected/Current/Candidate/Superseded. Ready indica trattamento completato, non autorizza automaticamente una rielaborazione candidata a sostituire quella corrente.

- Per ogni DocumentVersion, EvidenceDocumentState è keyed da DocumentVersionId/ProjectId, con CurrentPreparationId/CurrentEvidenceVersionId nullable, Revision iniziale 0. Entrambi i puntatori null o entrambi non null, FK verso la stessa versione/progetto. Una preparazione non può spostare una selezione PM ad altra versione file.
- Prima preparazione completa della versione: commit aggregatore pubblica Ready e Current, Revision+1, promozione automatica con motivo prima estrazione valida. Nessun consenso Admin aggiuntivo per sorgenti del progetto.
- Rielaborazione della stessa versione: nuova Preparation/Generation con RetryOfId se pertinente. Anche se Ready diventa Candidate: il puntatore corrente resta invariato finché il PM promuove esplicitamente. Se fallisce resta Failed/Incomplete non applicabile; versione corrente precedente resta utilizzabile.
- Nuova versione FILE: DocumentVersion distinta. La sua prima preparazione completa è Current per quella versione; il documento in modalità LatestReady risolve la versione file col numero più alto che ha puntatori correnti completi. Questa regola già approvata non impone una promozione PM supplementare per v2 file. Mode Pinned continua a usare la versione scelta dal PM; Included=false continua a escludere l'intero documento anche dopo nuove versioni.
- LatestReadyVersionId del documento è una cache transazionale derivata dai current state delle versioni file, mai dal massimo di tutte le preparazioni Ready. In assenza di versione applicabile, fonti incluse bloccano Analizza. Una candidata non promossa non può rendere applicabile una versione né sostituirne il current evidence bundle.
- Promozione PM: una sola transazione con expectedRevision dello state della versione file, candidata Ready appartenente a quella versione e copertura completa; cambia insieme CurrentPreparationId e CurrentEvidenceVersionId, registra DocumentPromotion con from/to bundle, autore/data/motivo, incrementa Revision. Vecchio bundle Superseded solo come proiezione, dati immutati. Concorrenza → un vincitore, altro 409; replay sul bundle già corrente restituisce 200 senza una seconda promozione. Non esiste promozione indipendente del solo estratto che disallinei il parse.
- Snapshot del comando conserva DocumentVersionId, CurrentPreparationId, CurrentEvidenceVersionId e SelectionRevision/BundleRevision esatti sotto lock comune; promozione/upload/Ready successivi non cambiano snapshot, checkpoint o conferme. GET di uno storico risolve il bundle storico anche se superseded; hasSourceChanges confronta snapshot con risoluzione corrente, senza revocare conferma o aggiornare bozza.

## Sources e gruppi di citazione (F4/F5)

Contratto chiuso in AI §2. Persistenza nello stesso EvidenceItem.Content versionato; SourceId stabile identifica artefatto/range, CitationGroupId+part ordina le fonti dell'evidenza. Eventuale tabella di proiezione per FK/source mapping deve imporre unique(groupId,part), owner progetto/versione coerente e tutti i range verificati. MessageId/sequence XOR campi documentali; nessun messaggio artificiale. Pagina/bbox/sezione sono proiezioni del manifest originale; le copie di header risolvono al blocco originale, non al prompt.

AnalysisContent DocumentPassage atomico mappa SourceId e range, e un item può avere molte evidenceRefs per una citazione logica. Content evidence-1.0 e AnalysisContent 1.0 restano byte-logicamente immutati nella rappresentazione JSONB, decoder dedicati e test compatibilità; migrazione aggiunge solo metadata/FK necessari senza sostituire Content. Relazioni cross-documento/PM del run non cambiano l'autorità degli estratti storici.
