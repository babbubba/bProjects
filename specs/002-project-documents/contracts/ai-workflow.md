# Contratto AI e oltre contesto — 002

Consolidamento F1–F7 secondo D1/D2 approvate dal committente. MAF .NET 1.26.0 fissato; nessuna modifica retroattiva a contratti/risultati della 001. Questo documento descrive un disegno da verificare, non prove runtime superate. Policy temporale normativa in [plan §Regimi](../plan.md#regimi-deadline-e-profili-operativi-f1f7).

> **Aggiornamento 2026-11-03 (T125)**: per l'**analisi documentale** lo scambio con il modello descritto in §1 (`evidenceKeys`) e nel «percorso singola chiamata» di §4.5 (`{results,analysis,evidenceMap}` riprodotto dal modello) è la definizione **v2, storica e frozen** (valida per i run già richiesti con `project-analysis-documents-v2`). Le nuove richieste usano la v3 con alias brevi e composizione server-side di [§7](#7-contratto-di-analisi-documentale-v3-con-alias-t125-2026-11-03); il contratto canonico persistito (AnalysisContent 1.1.0, reconcile-1, patch/completezza) resta quello di §§1–6.

## 1. AnalysisContent 1.1.0 e compatibilità

Root, sezioni, array e item mantengono la struttura 1.0.0, salvo i delta seguenti. Tutti i campi dichiarati sono obbligatori; proprietà extra, duplicate, enum numerici e null non ammessi sono rifiutati. Decoder/validator separati selezionati dalla versione esatta, non un decoder permissivo.

- schemaVersion const `1.1.0`; sourceCutoffSequence intero ≥0, zero soltanto senza messaggi PM e con snapshot documentale pronto non vuoto.
- classification: Declared, Documented, Inferred, Assumed. Declared richiede PM user/PmRevision; Documented richiede DocumentPassage verificata. Ipotesi e deduzioni restano Assumed/Inferred anche se documentate. Un item misto conserva tutte le fonti e la classificazione dell'enunciato, senza elevare l'autorità della fonte.
- Source 1.1: `id: UUID`, `kind: Message|PmRevision|DocumentPassage`, `messageId: UUID|null`, `messageRole: user|assistant|null`, `revisionId: UUID|null`, `documentId, documentVersionId, preparationId, artifactId, blockId: UUID|null`, `start,end: integer|null`, `quote: string|null`.
- Message: campi e regole 001, campi documentali tutti null. PmRevision: campi/regole 001, campi documentali tutti null. DocumentPassage: messageId/messageRole/revisionId null; campi documentali tutti non null; range [start,end) UTF-16 con start≥0, end>start entro CanonicalText del blocco immutabile; quote esatta verificata. Il server risolve pagina, sezione, bbox e righe, mai il modello.
- `evidenceRefs` resta array di source IDs, non EvidenceItem IDs. Una citazione multi-blocco usa più source IDs ordinati (vedi §3), senza concatenare passaggi come se appartenessero a un singolo blocco. L'AI usa solo source IDs assegnati dal server per range ammessi; gli ID nuovi degli elementi sono validati come nella 001.
- `evidenceKeys` rimane proprietà interna dell'output AI, validata/rimossa prima di persistere AnalysisContent; la mappa collega itemId agli EvidenceItem selezionati, con alias univoci nella selezione congelata.
- Risoluzione conflitti solo PM, scelta fra alternative esistenti e motivazione attribuita; conferma complessiva non certifica ipotesi o approvazione cliente.

Record AnalysisContent 1.0.0, evidence-1.0.0, coverage-v1 e relative citazioni restano decodificati con i rispettivi decoder, senza riscrittura del JSON o ID. Un edit di revisione 1.0 resta 1.0; per aggiungere documenti si richiede nuova Analizza. Nuove analisi con documenti sono 1.1; progetti senza documenti mantengono ramo e criteri 001. Il frontend nuovo supporta entrambi; non si promette lettura di 1.1 dai vecchi binari. Checkpoint storici mantengono definizione/config proprie. Nessun messaggio PM fittizio.

## 2. evidence-1.1.0: contratto chiuso

### 2.1 Identità e formato dell'output AI

Identità del progetto/documento/versione/preparazione/artefatto/unità, schema/prompt, configurazione e source manifest sono input server congelati, non proprietà che il modello possa ridefinire. Output per unità:

`{schemaVersion: "evidence-1.1.0", evidences: EvidenceCandidate[], noEvidenceReason: string|null}`.

EvidenceCandidate ha esattamente: `key: string` pattern e[1-9][0-9]* univoco nell'unità; `sources: CandidateSource[]` non vuoto; `kind`, `kindRationale: string|null`, `statement: string` non whitespace; `subject,action,object,scope,purpose,instrument,channel,recipient: string|null`; `qualifiers: [{type,text}]`; `polarity`; `certainty`; `relations: [{type,target,basis,rationale}]`. Tutti obbligatori. Campi opzionali assenti semanticamente sono null, non riempitivi generici.

Enum chiusi, riusati dalla 001:

- kind: Goal, Context, Requirement, Action, Constraint, Exclusion, Dependency, Assumption, Gap, Alternative, Decision, Other (Other richiede kindRationale non vuoto).
- polarity: Affirmed, Negated, Excluded, Prohibited, Undefined.
- certainty: Declared, Inferred, Hypothesized, ToClarify. Declared su documento significa asserito dalla fonte, non dichiarato né confermato dal PM: origin è separata.
- qualifier.type: Quantity, Deadline, Duration, Frequency, Minimum, Maximum, Condition, Exception, Obligation, Optionality, Exclusivity, Timing, Other; text non vuoto.
- relation.type: Qualifies, Confirms, Contradicts, Corrects, Supersedes, DependsOn, AlternativeTo, SameRule; basis Declared|Inferred; rationale string|null. Declared significa espresso letteralmente nella sorgente; non implica una decisione PM se la sorgente è documentale.

CandidateSource è un'unione discriminata chiusa:

- Message: `{kind:"Message", messageId:UUID, quote:string, occurrence:integer≥1, role:Primary|Context|Referent, citationGroupKey:string, part:integer≥0}`. Nessun campo documentale ammesso. Valido solo per messaggi forniti e cutoff ammesso; assistant soltanto Referent e mai Primary.
- DocumentPassage: `{kind:"DocumentPassage", rangeKey:string, quote:string, occurrence:integer≥1, role:Primary|Context, citationGroupKey:string, part:integer≥0}`. rangeKey deve appartenere al manifest server dell'unità, che risolve versione/artefatto/blocco e intervallo ammesso. Il modello non fornisce UUID documentali, locator, bbox o offset arbitrari. Quote deve essere sottostringa esatta del range, con occorrenza valida. Non normalizzare via AI né accettare replacement di caratteri; il backend calcola offset UTF-16 sull'artefatto immutabile.

Ogni candidato ha almeno una Primary posseduta dall'unità; un header replicato come contesto non genera da solo una nuova evidenza. source parts nello stesso gruppo hanno part contigui da zero, senza duplicati. target delle relazioni è una key della medesima unità o un alias Pn di un'evidenza fornita dal server; niente target inventati o autoreferenze. Per un target non ancora disponibile si conserva l'enunciato/locator nell'evidenza e si demanda la correlazione al passaggio globale, senza perdere il vincolo.

Zero evidenze implica noEvidenceReason non vuoto; con almeno una evidenza è null. Niente output mancante o troncato dichiarato vuoto. I limiti di stringhe/array del validatore 001 non vanno copiati come tetti silenziosi: il planner deve dividere l'unità prima della chiamata; se l'output eccede il budget operativo è incompleto/fallito, non una lista tagliata.

### 2.2 Rappresentazione persistita e viste

EvidenceExtractionVersion 1.1 registra id, projectId, origin `{kind:"Document",documentId,documentVersionId,preparationId}`, version≥1, jobId (aggregatore), schemaVersion, promptVersion, modelId (modello Extraction congelato per la preparazione), parameters, manifestHash, createdAt, evidenceCount, noEvidenceReason. Non aggiungere questi campi ai JSON 1.0 storici.

EvidenceItem mantiene id/versionId/projectId/ordinal/key/kind/polarity/certainty/statement. Content 1.1 è esattamente `{schemaVersion:"evidence-1.1.0",kindRationale,structure:{subject,action,object,scope,purpose,instrument,channel,recipient},qualifiers:[{type,text}],sources:PersistedSource[]}`; tutte le proprietà obbligatorie, stringhe structure nullable. Identità EvidenceItem assegnata dal server al primo output validato, stabile nel replay. Key globale di versione = unitOrdinal + key locale, non collisione fra e1 di unità diverse.

PersistedSource ha campi obbligatori `sourceId:UUID, kind, role, citationGroupId:UUID, part:integer≥0, messageId:UUID|null, sequence:integer|null, documentId,documentVersionId,preparationId,artifactId,blockId:UUID|null, start:integer,end:integer,quote:string`. XOR: Message ha messageId e sequence non null, documentali null; DocumentPassage ha tutti i documentali non null, messageId/sequence null. range validato sul messaggio originale o CanonicalText del blocco. sourceId identifica range+origine immutabile, riutilizzabile in più evidenze; citationGroupId identifica il gruppo nell'evidenza e ordina le parti, non cambia l'originale. UUID generati e persistiti dal server, nessuna dipendenza da GUID inventati dal modello.

Locator nella vista risolto dal server: `{page:integer|null, headingPath:string[], bbox:{coordinates:number[],coordinateSystem:string}|null, sourceLines:{start:integer,end:integer}|null, doclingRef:string|null}`. Contesto non disponibile → null esplicito, mai pagina presunta. Le bbox originali possono essere più di una: più parti/sources preservano ciascuna posizione. Origin è distinta dalla certezza.

Relazione persistita: id/projectId/versionId/fromEvidenceId/toEvidenceId/type/basis/rationale; vincoli progetto e fonti selezionate. Relazioni interpretate fra estrazioni/versioni documentali e PM durante Analizza vanno in AnalysisEvidenceRelations append-only del run, non modificano gli EvidenceItem originali. SameRule non cancella provenienze multiple.

Viste HTTP nuove: envelope `evidenceViewVersion:"1.1.0"` con evidence/versions, origin e sources sopra. Per righe legacy, versione 1.0 e serializzazione legacy invariata; nel listing misto ogni item è `{evidenceViewVersion, evidence}` e il client discrimina. Il listing legacy resta disponibile su richiesta esplicita di formato 1.0 solo per origini Message (vedi HTTP). Non deserializzare vecchio Content col decoder 1.1; origin per la UI derivata dai metadati DB di migrazione, non riscritta nel payload storico.

### 2.3 Validazione e pubblicazione

Validare JSON chiuso/enum, unicità key/source parts, quote/range, hash artefatto, progetto/cutoff/snapshot, ruoli, riferimenti ammessi e presenza Primary. Schema/source invalidi sono permanenti; nessuna riparazione AI nascosta. Qualificatori, condizioni, polarità e relazioni preservati nei dati; la loro fedeltà semantica richiede verifiche distinte, non è dedotta dal parsing.

Ogni DocumentJob ExtractUnit persiste soltanto WorkOutput e mappe di copertura sotto il proprio fence. Non può inserire la versione finale, promuoverla o rendere Ready la preparazione. L'unico proprietario del commit aggregato è EvidenceExtractionJob del ramo Document, collegato univocamente a PreparationId, eleggibile solo dopo tutte le unità valide e il remapping deterministico locale di key/overlap; la riconciliazione globale semantica appartiene al run Analizza. Nessun DocumentJob Finalize parallelo. Transazione e deadline come [data-model §Commit aggregato](../data-model.md#commit-aggregato-evidence-f3).

## 3. Passaggi multi-blocco e inventario

Un passaggio logico non è un blocco sintetico. Un'evidenza può avere molte sources, raggruppate da citationGroupId/part. AnalysisContent 1.1 mantiene un DocumentPassage per intervallo atomico e evidenceRefs contiene tutti i sourceId del gruppo, nel medesimo ordine; non un solo locator per un testo concatenato. Il renderer mostra separatori/ellissi e pagina/sezione di ogni parte, senza spacciare la concatenazione per citazione letterale contigua.

Esempio normativo: intestazione tabella b10 pagina 2 «Massimo giornaliero» e riga b25 pagina 3 «Cliente A | 5 ordini» sostengono un unico vincolo. Le sources conservano s10→b10[start,end), part=0, role=Context e s25→b25[start,end), part=1, role=Primary nello stesso gruppo; l'item dell'analisi cita [s10,s25]. Nessuna quote «Massimo giornaliero Cliente A 5 ordini» attribuita a b25. Quando b10 è ripetuto in altri chunk, il rangeKey risolve sempre allo stesso b10 dell'artefatto, non alla copia nel prompt. Frasi spezzate su due blocchi/pagine usano due Primary e due passaggi verificabili. Parti in versioni diverse non possono comporre un gruppo dello stesso originale.

Ogni intervallo informativo dell'inventario parser ha una unità proprietaria o una issue. Range di contesto replicati sono separati, mai contati due volte. Split strutturale/paragrafo/frase con mappa esatta; tabelle per righe con header/condizioni contestuali. Unità irriducibile fuori budget → incomplete esplicito, non substring. HybridChunker suggerisce raggruppamento, il tokenizer effettivo e il payload intero decidono l'invio. Zero evidenze motivato distinto da porzioni non elaborate.

## 4. Riconciliazione gerarchica globale e generazione

Definizioni nuove versionate `project-analysis-documents-v2`, `project-document-documents-v2`, `project-chat-documents-v2` e relativi prompt v2; nessun checkpoint di v1 della bozza di piano o grafo 001 viene reinterpretato con questa definizione.

L'estrazione copre tutte le porzioni. La riconciliazione opera sullo snapshot del medesimo Evidence ledger, con due viste: A soggetto/oggetto/ambito/riferimenti nominati; B azione/strumento/canale/qualificatori. Sono lavori esaustivi sul run, senza indice persistente di ricerca, ranking o top-k. Le regole seguenti completano B3; cataloghi e patch sono derivati del run, mai un altro registro Evidence.

### 4.1 Schemi chiusi, identità e versioni

Contratto `reconcile-1`, decoder strict: proprietà elencate obbligatorie, extra/duplicati/null non dichiarati rifiutati. UUID lowercase, hash SHA-256 esadecimali lowercase su JSON canonico RFC 8785 (array nell'ordine normativo), testi originali non normalizzati per hash/citazioni. Set serializzati ordinati per UUID byte order, stringhe in ordine Unicode ordinal; le sequenze conservano ordinal. Ogni envelope ha `{schemaVersion:"reconcile-1",runId,sourceHash,algorithmVersion:"global-reconcile-v1",policyHash,baseHash,payload}`. sourceHash copre snapshot, cutoff, EvidenceVersion e content hash; baseHash identifica l'input immutabile del passaggio. Campi di identità/hash sono server, mai ridefiniti dall'AI. ID derivati sono SHA-256 tipizzati con prefisso e componenti canonici, non UUID inventati dal modello; item UUID finali hanno mapping server persistito univoco per (runId,proposalId).

| Payload | Campi esatti e invarianti |
| --- | --- |
| CatalogEntry | `entryId,evidenceId,evidenceVersionId,contentHash,sourceIds,keys,knownRelationIds,unclassifiedViews`. Una riga per evidenza selezionata; sourceIds non vuoto, risolve quote/gruppi di §2 nel ledger originale. keys = array di `{view:A|B,kind:subject|object|scope|reference|action|instrument|channel|qualifier|alias,value,origin:Literal|Inferred,originOutputHash:null|hash}`. value non vuoto; le chiavi originali e gli alias inferiti rimangono distinti. Nessun enunciato sostituito dal catalogo |
| CatalogNode | `nodeId,view:A|B|Cross,level,ordinal,childNodeIds,memberSetHash,memberCount,keyPageHashes,relationPageHashes,descriptor,descriptorOutputHash`. Foglie hanno childNodeIds vuoto e membership esplicita in pagine; genitori unione esatta disgiunta dei membri dei figli della vista. descriptor string non vuota ≤512 caratteri Unicode, derivata solo per instradare, non citabile. Non elenca falsamente tutti i fatti |
| CatalogPage | `pageId,nodeId,kind:Members|Keys|Relations,ordinal,entries,previousPageHash:null|hash`. entries è union secondo kind: Members `{evidenceId,evidenceVersionId,contentHash}`; Keys `{key,memberIds,memberPageRefs}` dove key ha la forma di CatalogEntry.keys, memberIds:UUID[] e memberPageRefs:hash[] (esattamente uno non vuoto); Relations `{relationId,fromEvidenceId,toEvidenceId,type,basis}`. Pagine append-only, catena/ordinal contigui, conteggi e union hash verificati dal backend |
| CatalogSet | `setId,view:A|B|Cross,memberNodeIds,memberSetHash,memberCount`. Insieme di routing materializzato dal server come unione esatta delle membership dei nodi indicati, ID hash(view,memberNodeIds,memberSetHash). Non ha sommario semantico, nessun membro eliminato per overlap |
| Candidate | `candidateId,kind:Relation|Conflict|Alias,left:{kind:Evidence|Node|Set,id,hash},right:{kind:Evidence|Node|Set,id,hash},proposedType,rationale,basis:Declared|Inferred,originBatchIds,parentCandidateId:null|hash`. proposedType è un relation.type di §2, oppure Alias con kind Alias. Endpoint distinti, esistenti, nello snapshot; originBatchIds non vuoto. Un candidato di nodo/insieme non è una relazione accertata |
| CandidateDisposition | `candidateId,state:Pending|Expanded|Compared|Open|NotProcessed,childCandidateIds,comparisonOutputHashes,reason:null|string`. Expanded richiede figli ed espansione tracciata; Compared richiede confronto di evidenze integrali, anche se esito no_relation; Open richiede confronto integrale e motivo del dubbio da presentare al PM; NotProcessed richiede errore e impedisce completezza. Pending non terminale |
| ReconcileResult | `batchId,inputHash,view,level,visitedEntryIds,visitedNodeIds,candidates,dispositions,itemProposals,patches,descriptor:null|string,noFindingsReason:null|string`. Ogni ID visitato deve essere nell'input; tutti gli input principali hanno visita/disposition, anche se zero proposte. descriptor obbligatorio nei batch che costruiscono nodi, null negli altri; zero findings richiede motivo. itemProposals usa item schema AnalysisContent 1.1 con localKey, evidenceIds non vuoto e sourceIds verificati, mai conferma AI |
| Patch | `patchId,baseHash,target:{kind:Item|Relation,id,expectedHash},operation:AddSupport|AddRelation|ProposeAlternative,value,evidenceIds,sourceIds,originBatchIds`. value è union strict: AddSupport `{evidenceIds,sourceIds}`; AddRelation `{fromEvidenceId,toEvidenceId,type,basis,rationale}` di §2; ProposeAlternative `{text,classification,evidenceIds,sourceIds,reason}` (reason non vuoto). Niente JSON Patch generico, delete, replace, approvazione o risoluzione PM |

Per AddRelation il target Item è l'ancora esistente nella base, non un ID di relazione inesistente; target Relation ammesso solo per AddSupport/ProposeAlternative su una relazione già presente. expectedHash è sempre l'hash del target esistente. I campi evidenceIds/sourceIds nei value e nell'envelope devono coincidere dove entrambi presenti, non sono due autorità. Relation/Conflict ammettono solo relation.type; Conflict richiede Contradicts o AlternativeTo. Gli insiemi di routing sono cataloghi di membership, non nuovi EvidenceItem.

Tipi convenzionali nelle tabelle: contatori/ordinal/level interi ≥0, count≥1 salvo insiemi dichiaratamente vuoti; ID di dominio UUID, ID derivati e campi Hash hash tipizzati; array non null, deduplicati salvo sequenze di parti; text/rationale/descriptor/reason non vuoti quando non null, null solo dove dichiarato. Una selezione Evidence vuota valida usa il percorso zero evidenze motivato senza nodi/radici artificiali. entryId=hash(sourceHash,evidenceId,contentHash); pageId=hash(nodeId,kind,ordinal,entries,previousPageHash), hash di pagina calcolato senza includere pageId. Il nodeId usa i parent/input hash e membership, non hash delle proprie pagine (evita dipendenza circolare).

CandidateId = hash(kind, endpoint ordinati per relazioni simmetriche Contradicts/AlternativeTo/SameRule e Alias, endpoint diretti negli altri casi, proposedType, sourceHash). Rationale/origini non definiscono identità: duplicati uniscono le origini, conservando entrambe le motivazioni come record di proposta. PatchId = hash(baseHash,target,operation,value,evidenceIds,sourceIds); nodeId = hash(view,level,ordinal,child/member hash,inputHash). Un output non identico sotto la stessa chiave di batch è conflitto d'integrità, mai last-write-wins. Original EvidenceId/sourceId e contenuti qualificati restano immutati.

### 4.2 Partizione, directory e livelli oltre contesto

1. Costruire CatalogEntry da **tutte** le evidenze congelate, inclusi PM; normalizzazione delle sole chiavi = Unicode NFC, invariant lowercase, whitespace collassato e trim, senza stemming o equivalenze semantiche. Chiavi null/vuote non inventate: riga nel bucket `unclassified` di quella vista. Primaria di ciascuna vista = minima coppia (kind,value) ammessa, altrimenti unclassified; ordine foglie = (primaria,EvidenceId). Ogni evidenza principale compare una volta per vista. Le altre chiavi sono conservate nella directory, non solo la primaria.
2. `Pack` comune a foglie, pagine e confronti: prendere il massimo prefisso contiguo che soddisfa il preflight completo della fase e le riserve/schema congelati; misurare ogni tentativo di prefisso, non assumere additività dei token. Persistire inputHash, token/provenance, membri/range e ordinal prima di inferire. Nessuna riga spezzata/troncata. Riga o evidenza integrale irriducibile → limite esplicito. Cataloghi troppo grandi sono pagine concatenate, non un prompt unico.
3. Per ogni vista fare merge streaming **esatto** delle Keys ordinate per (kind,value,EvidenceId), senza LLM. Una chiave con membri in foglie diverse genera un candidato di gruppi, non tutte le coppie. Anche ogni relazione già nota genera obbligazione diretta sui suoi due EvidenceId. Alias inferiti si aggiungono alla directory solo con originOutputHash, senza riscrivere chiavi letterali. Per un gruppo con k≥2 foglie ordinate per node ordinal si materializzano due CatalogSet: prime ceil(k/2) foglie e rimanenti; un candidato fra i due insiemi ne impone la riapertura completa (§4.3), senza k² coppie o scansioni cumulative crescenti. Nessuna esclusione per documento, data o distanza. Questi candidati server hanno rationale `shared_literal_key` e originBatchIds dei cataloghi di origine, basis Inferred, proposedType SameRule come ipotesi da verificare, mai relazione applicata senza confronto.
4. Ogni batch foglia riceve le evidenze integrali e produce itemProposals/candidati e descriptor bounded validato, più visita di ogni membro. Costruire livello l+1 raggruppando in ordine node ordinal il massimo prefisso di **almeno due** descriptor di livello l che entra in `Pack`. Un ultimo nodo singolo si inoltra senza nuova AI né nuovo riassunto (stesso memberSetHash, carry tracciato). Ogni genitore conserva riferimenti esatti a tutte le pagine keys/relations/members dei figli; non deve includerle tutte nel prompt. Il confronto dei descriptor figli propone candidati fra nodi anche privi di chiavi comuni: è il raccordo globale obbligatorio ad ogni confine gerarchico, non solo fra evidenze vicine. Se non entrano due descriptor, `reconciliation_irreducible`; numero nodi deve diminuire strettamente a ogni livello non singleton, altrimenti fallimento. Max 32 livelli congelati; non si accorciano descriptor via taglio per forzare avanzamento.
5. Ripetere fino a una radice per vista. Confrontare descriptor radici A e B nel passaggio Cross e unire candidati/alias/directory di entrambe le viste. Una radice è un descrittore con membership esatta esterna, **non** tutte le evidenze condensate come fonte. Anche un insieme unclassified viene coperto da foglie e livelli. Ogni membro non correlato resta item individuale con fonti, senza fusione obbligatoria.

I descriptor aiutano a proporre correlazioni non letterali fra gruppi lontani, ma non garantiscono recall assoluta: keys/descriptor possono non far emergere un conflitto gold. W3 resta rischio semantico e gate qualitativo. Una lista di descriptor visitata non autorizza a dichiarare confrontate le evidenze senza la riapertura seguente.

### 4.3 Riapertura dei candidati e passaggio finale

Ordine stabile: obbligazioni di relazioni note, candidati A, B, Cross, poi candidateId. Ogni candidato di gruppi (Node o Set) genera un manifest di espansione con l'unione esatta e deduplicata dei member EvidenceId, ordinati; riaprire **tutti** i membri integrali con enunciato/qualificatori/polarità/sources/relazioni note, suddivisi da Pack. Ogni range del manifest ha WorkOutput validato; niente confronto semantico basato soltanto su descriptor. Le visite di gruppi propongono endpoint Evidence o alias espliciti e registrano contesto non ancora risolto. Un collegamento già noto o candidato Evidence richiede un batch con entrambi gli endpoint integrali e fonti: se neanche la coppia entra, NotProcessed e coverage_incomplete, senza spacciarla per Open.

Un candidato Node/Set diventa Expanded solo quando tutte le sue espansioni sono coperte e ogni collegamento pendente è trasferito a candidati Evidence non vuoti. Se la scansione di tutti gli originali non propone alcun collegamento e non lascia enunciati/riferimenti pendenti, può terminare Compared con reason `no_candidate_after_full_scan` e hash di **tutti** gli output di espansione: attesta scansione integrale del gruppo, non confronto di ogni coppia o assenza certa di conflitti. Se invece segnala un possibile collegamento ma non riesce a individuarne gli endpoint, resta NotProcessed `candidate_unresolved`. Nessuna chiusura per sola visita di metadati. I figli Evidence finiscono Compared/no_relation oppure Open (alternative integrali da sottoporre al PM), mai risolti automaticamente. Un candidato Alias può validare una chiave di routing inferita solo dopo confronto integrale; non cambia identità o verità delle evidenze.

Dopo A/B/Cross c'è **un solo** round finale: merge streaming delle chiavi/alias emersi, espansione di tutti i candidati ancora pendenti e confronti diretti dei relativi endpoint. Duplicati sono cache hit logici, non nuova AI. Ogni coppia Evidence candidata distinta è confrontata al massimo una volta per inputHash/prompt; le visite di gruppi non impongono tutte le coppie dei membri. Se nel round finale emergono nuovi gruppi o collegamenti che richiedono ulteriore lavoro non già coperto, conservarli NotProcessed `reconciliation_round_limit` e fallire la completezza, senza iterare. Limiti globali 512 chiamate (retry inclusi), 100000 candidati, byte derivati e deadline prevalgono; limite non autorizza pruning.

### 4.4 Composizione delle patch

La base è l'insieme immutabile di itemProposals validati e relazioni del passaggio (baseHash), con tutti i fatti atomici e le loro provenienze; prima delle patch il backend controlla la mappa di ogni EvidenceId. Le patch dello stesso passaggio sono validate **tutte contro quella stessa base**, poi composte come insieme ordinato per patchId; l'ordine non dà precedenza semantica. Nessun worker scrive direttamente un item corrente.

| Caso | Esito normativo |
| --- | --- |
| Stesso patchId e payload canonico | Un effetto, origini unite; replay riusa PatchDisposition |
| Stesso patchId e payload diverso | integrity_conflict permanente, nessuna pubblicazione |
| baseHash o expectedHash superato | Stale, nessuna applicazione/rebase implicita; riuso solo su base esatta. Se necessario al risultato, coverage_incomplete |
| Schema, target, progetto, EvidenceId/sourceId/quote non validi o fuori snapshot | Invalid, errore permanente e copertura incompleta; nessuna source sintetica |
| AddSupport sullo stesso target | Unione ordinata di fonti/evidenze valide; non modifica testo, polarità, qualificatori o classificazione |
| Relazioni equivalenti | Unione provenienze/motivazioni, stessa relazione logica; direzionalità preservata |
| Proposte incompatibili sullo stesso target, oppure SameRule e Contradicts sugli stessi endpoint, o Supersedes/Corrects diretti opposti | Nessuna vince: gruppo di incompatibilità con tutte le patch e record di conflitto con tutte le proposte e fonti; base intatta, classificazioni originali conservate, nuove deduzioni/ipotesi Inferred/Assumed, decisione richiesta al PM |

L'incompatibilità sintattica verificabile è rilevata dal server; incompatibilità semantiche stimate restano segnalazioni, non fatti automaticamente risolti. AddRelation non applica gli effetti di Supersedes/Corrects alle fonti o agli item: registra l'interpretazione, conserva entrambe le alternative. ProposeAlternative aggiunge proposta con ID stabile e motivazione, non sostituisce la base né promuove Documented/Inferred a conferma PM. Una patch incompatibile può avere disposition terminale `PreservedAsConflict` solo se tutte le alternative/qualificatori/fonti sono rappresentate nel conflitto della revisione; altrimenti NotProcessed, nessun risultato completo. Nessun delete o risoluzione automatica, neppure per data.

### 4.5 Avanzamento e completamento verificabili

Ogni confine pubblica un WorkOutput con hash e copertura prima di avanzare (§5). Condizioni congiunte per Complete: inventario/estrazioni completi; tutte le evidenze visitate in A e B; pagine e livelli senza buchi e radici/Cross riusciti; ogni relazione nota e candidato con disposition terminale (Expanded con figli tutti terminali, Compared o Open fondato su originali); nessun Pending/NotProcessed/Stale/Invalid necessario; patch applicate, duplicate riusate o integralmente PreservedAsConflict; ogni EvidenceId mappato a item e ogni conflitto/qualificatore conservato. Coverage report salva insiemi attesi/visitati/mancanti e hash, non un booleano del modello.

Ogni passo deve completare almeno un batch/obbligazione nuovo oppure ridurre il numero di nodi; replay non conta come avanzamento. Nessun batch con inputHash già completato viene rimesso in coda logica. Livelli/round/candidati/chiamate/deadline limitati come sopra; stallo → coverage_incomplete con fase, ID e motivo. Solo dopo queste condizioni assemblare gli array in ordine (section order schema, proposal ordinal, item ID), validare AnalysisContent e committare revisione+Completed atomicamente. Un conflitto aperto correttamente rappresentato è un risultato completo **con punto aperto**, non una verità risolta. Complete strutturale non certifica correttezza semantica: un conflitto gold perso è FAIL anche con visita 100%.

Generazione dai soli item integrali confermati: se tutto entra nel budget, una chiamata; altrimenti unità editoriali misurate con item principali, qualificatori, conflitti e fonti necessari. Output `{sectionKey,blocks:[{itemIds,markdown,sourceIds}]}` chiuso, itemIds del pacchetto, fonti ammesse, tutti gli item principali rappresentati. Assembly senza LLM finale che comprima lo snapshot; un solo Draft Proposed/NotApproved e Completed committati insieme. Nessuna compattazione dei dati confermati. Anche qui copertura ID non certifica il significato.

Raccordo minimo W1: riusare marcatori atomici `[fonte:<UUID>]` della 001, uno per sourceId, nell'ordine delle parti richieste; sourceIds di ogni blocco deve coincidere con i marcatori presenti. Il renderer ricostruisce i gruppi da (itemId,EvidenceId,citationGroupId,part) nella mappa persistita del run, non deduce un unico gruppo dalla sequenza globale dei sourceId. Due gruppi possono condividere lo stesso header/sourceId. Edit PM non modifica tale mappa storica: fonti rimosse non sostengono il testo nuovo, riferimenti rimasti aprono sempre i passaggi atomici; un nuovo enunciato PM segue PmRevision. Fixture di resa/edit rimane da verificare, W1 non dichiarato superato.

Percorso singola chiamata (formato di scambio v2 storico/frozen; per le nuove richieste il modello restituisce il DTO con alias di §7 e il server compone lo stesso `{results,analysis,evidenceMap}` canonico): solo se input integrale dell'intero snapshot e output minimo entrano secondo Plan, il planner può collocare visite A/B/Cross e confronti in **un batch unico** di schema `{results:ReconcileResult[],analysis:AnalysisContent1.1,evidenceMap}` strict, con results esattamente A/B/Cross in quest'ordine e evidenceMap item→EvidenceId. Nessun catalogo gerarchico necessario; tutti gli originali sono nell'input. Validare le stesse obbligazioni/patch/mappa di §4.5; nessuna fiducia in una dichiarazione `complete` AI. Se l'output valido richiede altro lavoro, promozione prima del passo successivo; se troncato/invalido, fallimento. Il numero previsto di chiamate è uno, non una garanzia di conclusione o durata. Per i risultati non gerarchici descriptor=null e level=0.

## 5. Topologia fissa, dati dinamici e checkpoint (F2)

Sostituisce l'ipotesi precedente di congelare tutti i pacchetti prima che esistano gli output. WorkManifest iniziale congela input/fonti, config/policy, impronta runtime/tokenizer, definizione del grafo e regole versionate di partizione/correlazione, non la futura lunghezza degli output AI.

### 5.1 Grafo e guard prima dell'inferenza

Topologia fissa: Load → PrepareBatch → LookupOutput; hit → Advance; miss → InvokeBatch → ValidatePersistBatch → Advance; Advance → PrepareBatch oppure Assemble → Commit. Gli archi sono nativi MAF con predicati esclusivi sul messaggio (`Hit|Miss`, `More|Done`); la verifica fallita termina per errore, mai percorre entrambi i rami. Executor ID stabili per definizione, nessun nodo per batch. PrepareBatch trasforma i dati secondo §4/§6 e pubblica DerivedBatch, non legge un NextExecutor dal DB. Il feedback e l'ordine dei nodi rimangono nel workflow nativo.

Il ramo multipasso nuovo usa un executor MAF `Executor<TInput>` InvokeBatch che chiama **AIAgent con una nuova AgentSession per invio**, anziché riutilizzare ciclicamente un singolo host bound con sessione implicita. I grafi bound della 001 non cambiano. LookupOutput controlla (owner,batchId,inputHash), payload/hash/schema/config/fonti/definition compatibili e stato Committed. Hit emette solo il riferimento validato verso Advance, senza creare sessione. InvokeBatch ripete **lo stesso lookup all'ingresso**, prima di creare la sessione, perché il checkpoint ripreso potrebbe contenere il messaggio Miss precedente al commit. Nessun checkpoint MAF viene modificato manualmente per saltare un nodo.

### 5.2 Identità congelata e lifecycle AgentSession

Processing identity = (owner kind/id, WorkManifest hash, graph definition, stage/view/pass/level, ordinal, parent output hashes ordinati, inputHash). BatchId hash di queste componenti salvo inputHash registrato separatamente; stesso input logico nel replay ha stessa identità. InputHash comprende payload **completo** (prompt/schema/tool/framing/opzioni, riferimenti originali e relativi hash), non solo EvidenceId. Runtime fingerprint comprende modello, tokenizer, template e finestra/budget congelati. Attempt/fence non fanno parte dell'identità logica, ma sono attributi del tentativo di esecuzione. Sessione workflow MAF è stabile sul root; non coincide con AgentSession del batch.

Su miss e lease corrente: (1) controllare root/figlio/deadline e runtime compatibile; (2) creare sessione nativa vuota con CreateSessionAsync; (3) comporre solo istruzioni della fase, input batch e contesto esplicito congelato; provider di storico conversazionale/compattazione/sintesi disabilitati per questi agenti batch, tool autonomi assenti; (4) preflight sul payload effettivo completo, includendo ogni messaggio/provider ammesso e riserva output; conteggio ancora unavailable → runtime_unverified senza invio, upper bound calibrato ammesso, semplice stima non basta; (5) riservare atomicamente il contatore chiamate del root e registrare invio/attempt/fence prima della rete; (6) RunAsync o RunStreamingAsync con timeout residuo; (7) validare e persistere. Tre batch individualmente entro budget ma cumulativamente oltre finestra usano tre sessioni nuove: nessun turno precedente implicito. I dati richiesti da batch precedenti entrano solo come riferimenti espliciti materializzati e contati.

Sessione del batch termina dopo singola risposta o errore; riferimento rilasciato senza presupporre IDisposable/Reset non verificati. Eventuale SerializeSessionAsync è diagnostica derivata compatibile, **non** checkpoint di una chiamata LLM in volo. Non riprendere metà generazione/token dal server; nessuna dipendenza dal provider per durable sessions. Il resume usa output validi prima di decidere invii, non deserializza una sessione cumulativa. Streaming del batch non validato non è risultato definitivo.

### 5.3 Commit di dominio, checkpoint e recovery

Prima di Advance: transazione corta fenced persiste WorkOutput Committed con payload/hash/schema, evidence/source mapping e coverage/disposition, ID server assegnati, oltre ai manifesti figli già determinabili o a una ricevuta di espansione deterministica. Unique(owner,batchId,inputHash); chiave di batch identica con input diverso è incompatibilità. Eventuale storage privato voluminoso è durevole e hash-validato prima del riferimento DB, con riconciliazione come per gli artefatti, mai Completed su blob mancante. I figli dipendenti da più output sono pubblicati da PrepareBatch solo dopo tutti i genitori Committed: unique input/parent hash e transazione fenced. Nessun avanzamento su risposta in memoria o parziale.

Poi QueueStateUpdateAsync registra soltanto riferimenti/cursori/hash nello stato MAF; checkpoint nativo al confine del superstep. Stato condiviso visibile agli altri executor dal superstep successivo: trasmettere i riferimenti necessari anche nel messaggio, senza assumere visibilità immediata. Store checkpoint esteso ai nuovi owner verifica lease/fence/root/deadline. Le transazioni DB di dominio e checkpoint MAF sono distinte, non una finta transazione distribuita.

| Interruzione | Recupero obbligatorio sotto nuovo lease/fence |
| --- | --- |
| Prima di output Committed, anche LLM già terminato | Ignorare risposta/sessione incompleta; nuova sessione e possibile reinferenza, contatore invio già consumato conservato; nessun exactly-once chiamate |
| WorkOutput Committed prima del checkpoint | Resume nativo anche da checkpoint precedente; LookupOutput **o guard interno InvokeBatch** trova hit e va ad Advance senza LLM; stessa payload/ID/hash/figli |
| Checkpoint dopo commit, messaggio/Advance ripetuto | Rileggere WorkOutput e figli, union idempotente dei covered IDs; nessuna nuova chiamata o duplicazione |
| Lease perso prima/dopo risposta | Cancellazione best-effort; stale worker non scrive output, figli, checkpoint, stato finale o eventi definitivi. Nuovo owner riusa solo ciò che fu validamente Committed prima della perdita |
| Output troncato, JSON/riferimenti invalidi | Nessun WorkOutput Committed; errore permanente, diagnostica separata, non cache hit/noEvidence. Trasporto transitorio senza output segue massimo tentativi/backoff/deadline del root |
| Hash/config/schema/runtime incompatibili o payload mancante | Fail checkpoint_incompatible/work_output_invalid; non reinferire per sovrascrivere il risultato esistente |
| Dominio finale Completed, checkpoint finale fallito | Completed prevale, leggere stessa revisione/bozza/risposta; nessuna ripresa/inferenza |
| Retry PM dopo Failed/Incomplete | Nuovo root con RetryOfId e nuovo t0, output vecchio immutato. Verificare compatibilità semantica esatta (fonti/cutoff/revisione, prompt/schema/algoritmo, parametri/runtime/budget) prima di importare output con DerivedFromOutputHash; nuova ricevuta nel nuovo owner rimappa identità solo di run, non fonti. Se include run-specific payload non rimappabile deterministicamente, non riutilizzabile |

Il vecchio checkpoint può essere dietro al dominio; non può essere autorevole su un output incompatibile. Un checkpoint che punta a un output assente/corrotto non autorizza avanzamento o nuova generazione nascosta. In assenza di qualunque checkpoint si riavvia **la stessa topologia nativa dall'ingresso** con manifest identico; i guard recuperano output committati, non un ciclo applicativo di dispatch nodi. Attempts/CallCount/deadline non si azzerano. Atomicità, unique e fence garantiscono unicità degli effetti; crash tra riserva invio e rete può consumare una chiamata non effettuata, costo prudenziale dichiarato.

### 5.4 Primitive 1.26.0 e raccordo minimo

Riscontro statico del 2026-11-01 sui pacchetti NuGet locali **net10.0 1.26.0**, pin in csproj/packages.lock.json: XML Microsoft.Agents.AI.Workflows documenta Executor<TInput>, WorkflowBuilder.AddEdge<T>(ExecutorBinding,ExecutorBinding,Func<T,bool>), QueueStateUpdateAsync<T>, CheckpointManager.CreateJson e InProcessExecution.ResumeStreamingAsync(Workflow,CheckpointInfo,CheckpointManager,CancellationToken). Core/Abstractions espongono CreateSessionAsync, RunAsync/RunStreamingAsync e Serialize/DeserializeSessionAsync; MafApiCompatibilityTests contiene sessione+serialize e workflow+checkpoint/resume con SDK reale e modello finto. Codice letto, test non rieseguiti. Fonti e limiti in Research R10; nessuna API ResetSession o resume intra-inferenza adottata.

MAF non rende atomico il commit WorkOutput PostgreSQL con la chiamata LLM né conosce la validità delle evidenze applicative. Il raccordo minimo è LookupOutput/guard idempotente e InvokeBatch con CreateSessionAsync dentro un executor MAF, più store fenced esistente esteso. Nessun interprete di grafo, scheduler di executor, cursor che sostituisca il checkpoint, retry autonomo o registro di agenti parallelo. I predicati degli archi sono codice della definizione, le tabelle sono dati/input/output. Nuovo ciclo, esclusività routing e restore processo nuovo devono ancora essere compilati/provati con SDK 1.26.0, DB reale e fault injection (§Quickstart); presenza statica delle firme non dimostra il comportamento integrato. Gate fallito → correzione della composizione nativa, nessuna invenzione di primitive o engine alternativo.

Per le estrazioni documentali ogni unità è un job AI bounded, con risultato immutabile e rilascio dello slot; il job aggregatore non occupa uno slot per tutta la preparazione. I workflow formali multipasso mantengono un solo owner; il relativo limite e FIFO 001 restano, nessun parallelismo DGX aggiunto.

## 6. Chat, budget, regimi e copertura

Chat documentale visita tutte le unità ammesse per una domanda sui documenti; ogni risultato dà supporti citati o no_support motivato. Riduzione gerarchica con lo stesso feedback di §5 conserva l'unione delle unità visitate, conflitti, source IDs e note pertinenti; gli originali rimangono accessibili, nessuna riscrittura formale Evidence/Analysis. Non è retrieval, non scarta top-k. Domanda non documentale può rispondere sul solo dialogo con no_document_use esplicito.

Fonte non pronta inclusa: stato congelato, indisponibilità dichiarata, nessuna attesa di readiness al retry né risposta inventata. Cambio fonti invalida sessioni/sintesi; assistant di snapshot diversi esclusi dal contesto operativo ma leggibili nello storico. Originali PM preservati; rinvii a fonti escluse marcati non applicabili come prove documentali. Streaming solo del testo finale candidato, provvisorio fino a validazione/commit; un'inferenza finale fuori output non diventa risposta completa fittizia.

Policy D1 in Plan §Regimi, correzione finale B1: classificazione iniziale tecnica (single_call_fit, multipass_or_capacity o workload_unknown motivato), indipendente da tempo trascorso/carico dello slot. Promozione Ordinary→Extended solo prima della scadenza e con ricevuta additional_work verificata: almeno un WorkOutput Committed valido, obbligazioni di copertura residue e un batch AI necessario senza output compatibile già Committed. Nessuna promozione da timer, coda, inferenza lenta o retry della stessa chiamata. PrepareBatch produce la ricevuta e i batch dal piano congelato; la transazione fenced li collega alla transizione atomica del root secondo Plan. Non cambia topologia, identità, cache, ordine commit→Advance o regole di copertura di §4–5. Se gli output sono già riutilizzabili si prosegue senza nuova AI o promozione per reinferirli. Root terminale/scaduto non promuove; output invalido o limite irriducibile falliscono. Timeout locali sempre subordinati al padre; retry automatico/reclaim/resume non azzerano t0, contatori o hard cap iniziale 3600 s. Il PM vede stato e motivo senza scegliere o confermare strategie. Regime ed esito prestazionale sono distinti: N completo a 150 s con qualità conforme può essere PASS anche dopo promozione, N oltre 180 s resta FAIL temporale. Nessuna riduzione di schema/qualità/fonti per rispettare il tempo; fencing impedisce commit remoto tardivo.

Payload intero con istruzioni/schema/framing entro finestra reale per modello/fase, output totale reasoning incluso e margine. Stime calibrate e unavailable espliciti; niente char/4 o finestra teorica. Runtime non verificabile blocca il nuovo ramo documentale. MaxAiCalls contabilizzato persistentemente per root, retry inclusi, prima dell'invio; raggiunto il limite termina con errore, non con parti ignorate.

Errori 001 riusati ove pertinenti; aggiuntivi: sources_not_ready, source_snapshot_invalid, document_incomplete, document_limit_exceeded, parser_failed, runtime_unverified, tokenizer_mismatch, coverage_incomplete. Retry automatico solo transitorio, schema/citazione invalida permanente; parser timeout transitorio solo se resta budget padre e attempt. Coverage-2 distingue errori deterministici (range/locator/hash/ID mancanti, fuori snapshot, item senza frammento, relazioni note non trattate), rischi semantici stimati (numeri/polarità/qualificatori/condizioni alterati) e valutazioni PM. Un rischio non è una diagnosi semantica certa. Hash comprende snapshot, parse, estrazioni, batch derivati, revisione e draft; report non cambia gli originali.

## 7. Contratto di analisi documentale v3 con alias (T125, 2026-11-03)

Disegno da verificare, non prova runtime superata. **Sostituisce, per le nuove richieste, il solo formato di scambio con il modello del «percorso singola chiamata» di §4.5** (`{results,analysis,evidenceMap}` con id/hash/Source riprodotti dal modello): quel formato è la definizione **v2** (`project-analysis-documents-v2`, prompt `document-analysis-v2`, schema `document-analysis-1`), ora **storica e frozen**: resta eseguibile e immutato per i run già richiesti con v2 e non è reinterpretato. Restano invariati AnalysisContent 1.1.0 (§1), reconcile-1/`global-reconcile-v1` (§4.1), invarianti di patch/completezza (§4.4–4.5), grafo fisso, checkpoint e fencing (§5), regimi (§6), generazione e chat v2. Il contratto **canonico** (`SingleCallOutput`, AnalysisContent 1.1.0, mappa item→evidenze, relazioni di run) è quello di prima: cambia chi lo produce.

### 7.1 Motivo

La campagna reale su Qwen3.6-35B ([I4-real.md](../validation/I4-real.md) e le fasi [A](../validation/I4-real-131k-faseA.md), [B](../validation/I4-real-131k-faseB.md), [C](../validation/I4-real-131k-faseC.md)) ha dato 1 analisi valida su 25 a W=32768 e 1 su 3 per cella a W=131072 (N1 PASS, N2/N3 FAIL in A e B; 0/3 con Qwen3.8-Flash-Next-Smart). I fallimenti dominanti sono strutturali, non di capacità: id di fonte al posto di id di evidenza, hash di voce al posto di UUID, etichette (`req-1`) al posto degli UUID di item, UUID troncato o con una cifra errata, campi nulli o omessi, loop di ripetizione di hash. Il tipo di errore cambia a ogni run e a ogni configurazione e non si riduce con W, reasoning o prompt: il contratto v2 chiede 60+ identificativi copiati alla lettera e UUID inventati. Decisione vincolante: il modello fa interpretazione semantica; il server fa identità, hash, provenienza, mapping e integrità referenziale. Il server non prende decisioni semantiche: compone solo ciò che il modello ha dichiarato in modo esplicito.

### 7.2 Confine modello / server

| Struttura canonica | Campi semantici (dal modello, con alias) | Derivati dal server (mai nell'output AI) |
| --- | --- | --- |
| Item di analysis (goals, scope, requirements, flows, constraints, dependencies, assumptions, openQuestions) | sezione di appartenenza, `text`, `classification`, evidenze di supporto (alias `E`), chiave locale | `id` canonico, `evidenceRefs` (sourceId di tutte le fonti delle evidenze citate, nell'ordine gruppo/part), voce di `evidenceMap`, validazione `Declared`/`Documented` sulle fonti |
| Conflitti e alternative | `description`, almeno 2 alternative (item propri con evidenze), chiave locale | `id` conflitto e alternative, `status:"Open"`, `resolution:null` (solo PM, §1) |
| `analysis.sources` | nessuno | solo le fonti **originali** (record persistiti) delle evidenze citate: quote, range, blocco, documento, versione, preparazione, artefatto; `messageRole` dal dialogo dello snapshot |
| Metadati di `analysis` | nessuno | `schemaVersion`, `analysisId`, `projectId`, `sourceCutoffSequence`, `validationState:"Unvalidated"` |
| `results[A/B/Cross]` | visita per vista (alias `E` esaminati), `noFindingsReason` per vista | `batchId`, `inputHash`, `level:0`, `descriptor:null`, `visitedNodeIds:[]`, `visitedEntryIds` (da alias dichiarati), `originBatchIds` |
| Candidate / Disposition | coppia `E`/`E` (o relazione nota `R`), tipo, `basis`, `rationale`, esito `Compared`/`Open`, `reason` | `candidateId`, endpoint con `contentHash`, `kind` (Relation; Conflict solo con Contradicts/AlternativeTo), `parentCandidateId:null`, `childCandidateIds:[]`, `comparisonOutputHashes` |
| ItemProposal | (gli item di analysis) | derivati 1:1 dagli item (`localKey` = chiave locale, `evidenceIds`, `sourceIds`), collocati nel result Cross |
| Patch | operazione (`AddSupport`, `AddRelation`, `ProposeAlternative`), bersaglio (item per chiave o relazione nota per alias), evidenze, valore semantico (tipo, basis, rationale, testo, classificazione, reason) | `patchId`, `baseHash`, `expectedHash`, `sourceIds`, `originBatchIds` |
| Metadati tecnici, id run/batch/progetto, UUID, SHA-256, elenco completo `visitedEntryIds` | nessuno | tutto |

Un'evidenza è «a supporto» di un'affermazione solo se il modello la cita esplicitamente: il server non assegna mai evidenze per somiglianza lessicale, ordine o prossimità. Una evidenza non citata da alcun item è copertura incompleta (§4.5), non un'assegnazione automatica.

### 7.3 Catalogo alias (immutabile, server-side)

Il catalogo è funzione pura (algoritmo `alias-catalog-1`) dello snapshot congelato al POST (T057) e della versione del contratto; si costruisce al claim da `DocumentAnalysisSources.LoadAsync` e non ha identità fuori dal run.
- **`E<n>`** evidenze, `n` da 1 secondo l'ordine di lettura già usato dal server (messaggi PM per sequenza e ordinale, poi documenti nell'ordine dello snapshot e per ordinale; spareggio EvidenceId). **`S<n>`** fonti, una per **SourceId distinto** (due evidenze con lo stesso passaggio condividono lo stesso `S`), numerate al primo incontro scorrendo le evidenze e i loro `(citationGroupId, part)`. **`R<n>`** relazioni note dello snapshot, per `(from, to, type)`. Pattern esatto `^[ESR][1-9][0-9]*$`, case-sensitive, nessuna normalizzazione.
- Chiavi locali scelte dal modello: `i<n>` item e alternative (univoche fra tutti gli item), `c<n>` conflitti, `f<n>` finding, `p<n>` patch; pattern `^[icfp][1-9][0-9]*$`, univoche per tipo.
- Ogni voce registra: alias; `EvidenceId`, `EvidenceVersionId`, `ContentHash`, `entryId`; origine Message|Document con `DocumentId/VersionId/PreparationId`; per ogni fonte `SourceId`, `ArtifactId/BlockId`, range UTF-16, citazione letterale, `CitationGroupId`, `part`, `role`, provenienza (Message|DocumentPassage), `MessageId/sequence`; relazioni note (id, from, to, type, basis); id del run, `sourceHash`, versioni di contratto. `catalogHash` = SHA-256 del JSON canonico RFC 8785 del catalogo, congelato nel WorkManifest e nel payload del WorkOutput. Nessuna nuova tabella né migrazione: il mapping completo è nel payload JSONB del WorkOutput.
- Il prompt mostra per ogni evidenza `[E3] tipo, polarità, certezza, origine`, enunciato, fonti `[S5] ruolo "citazione letterale"` e relazioni note `[R1] E3 DependsOn E7`; **mai** UUID, hash, `entryId`, `batchId`, `inputHash`, id di progetto/analisi/run, offset, blocchi o metadati tecnici; nemmeno cutoff e conteggi. Gli `S` sono etichette di lettura: l'output AI non li cita (YAGNI).

### 7.4 DTO di output AI (`document-analysis-alias-1`)

Distinto dal canonico persistito. Strict: tutte le proprietà obbligatorie, extra/duplicate/enum numerici/numeri fra virgolette rifiutati, `null` solo dove dichiarato. Schema indicativo:

```
AiAnalysisOutput = {
  contract: "alias-1",
  visited:  { A: E[], B: E[] },        // alias esaminati per vista (A: soggetto/oggetto/ambito; B: azione/strumento/canale/qualificatori)
  noFindings: { A: string|null, B: string|null, Cross: string|null },
  items: { goals, scopeOverview, scopeIncluded, scopeExcluded, requirements, flows,
           constraints, dependencies, assumptions, openQuestions: AiItem[] },
  conflicts: [ { key: "c1", description: string, alternatives: AiItem[] /* almeno 2 */ } ],
  findings:  [ { key: "f1", view: "A"|"B"|"Cross", left: E, right: E, type: RelationType,
                 basis: "Declared"|"Inferred", rationale: string, outcome: "Compared"|"Open", reason: string|null } ],
  knownRelationChecks: [ { relation: R, verdict: "Confirmed"|"NoRelation"|"OpenForPM", rationale: string } ],
  patches: [ { key: "p1", op: "AddSupport"|"AddRelation"|"ProposeAlternative",
               target: { item: i|null, relation: R|null }, evidence: E[], value: PatchValueAi } ]
}
AiItem = { key: "i1", text: string, classification: "Declared"|"Documented"|"Inferred"|"Assumed", evidence: E[] }
PatchValueAi = AddSupport {} | AddRelation { from: E, to: E, type, basis, rationale: string|null }
             | ProposeAlternative { text, classification, reason }
```

`RelationType` e `basis` sono gli enum di §2.1. Hanno significato semantico, e quindi restano al modello: item (sezione, classificazione, evidenze), conflitti e alternative, finding (relazioni, conflitti, alias candidati fra evidenze) con esito `Compared`/`Open`, verdetti sulle relazioni note, patch, visita per vista e motivo di assenza di finding. **Non** si lasciano vuote le strutture di riconciliazione: la v2 imponeva `candidates/dispositions/itemProposals/patches` vuoti (prompt riga 13), il che rende impossibile completare uno snapshot con relazioni note (`known_relation_unresolved` in `ReconciliationCompletion`; dedotto dal codice, non provato con il modello reale). Sono derivabili dal server solo i campi della colonna destra di §7.2.

### 7.5 Id canonici stabili

`uuid = UUIDv8(SHA-256(JCS(["canon-ids-1", kind, rootId, sourceHash, localKey]))[0..16])`, con `kind ∈ {item, conflict}` (le alternative sono item), lowercase con trattini; stessa tecnica del precedente `pm-source` di `DocumentAnalysisSources.PmSources` con bit di versione e variante. Nessun `Guid.NewGuid()` e nessun id dal modello. `rootId` = ProcessingRun dell'analisi; `sourceHash` = quello dello snapshot. L'id non dipende dal testo: nello stesso run la chiave locale identifica l'item, e un output diverso sotto la stessa chiave di batch è già `integrity_conflict` (§4.1). EvidenceId e SourceId restano gli id persistiti nel ledger (nessuna derivazione). Collisione fra due id canonici, o con un id già presente nella revisione: `integrity_conflict`.
- **Replay e recovery**: il WorkOutput Committed contiene il DTO AI canonico (`aiOutputHash`), il catalogo (`catalogHash`) e il canonico composto (`canonicalHash`); ogni lettura o commit ricompone dal DTO e dal catalogo e verifica `canonicalHash`, senza reinferire. Stessi input, stessi id.
- **Retry PM (nuovo root, RetryOfId)**: il DTO non contiene dati di run, quindi un output compatibile (§5.3) è importabile con `DerivedFromOutputHash` e ricomposto sul nuovo `rootId`: cambiano solo gli id di run, mai fonti né evidenze.
- `AnalysisRevision.Id` resta generato al commit atomico come oggi; non fa parte del contratto con il modello.

### 7.6 Risoluzione e rifiuto

Decodifica strict del DTO, poi risoluzione di ogni alias contro il catalogo congelato. Rifiuto permanente (nessun WorkOutput, nessuna riparazione AI, nessuna normalizzazione o ricerca fuzzy):
- alias sconosciuto, fuori snapshot, di un namespace sbagliato (es. `S` dove serve `E`), o con formato non canonico (`e3`, `E03`, `E 3`);
- duplicati vietati: chiave locale ripetuta, alias ripetuto in una lista, finding o patch con identità canonica duplicata (stesso `candidateId`/`patchId`), alias ripetuti in `visited`;
- item o alternativa senza evidenze dove le regole 1.1 le richiedono; `Declared` senza fonte PM e `Documented` senza DocumentPassage (regole esistenti di AnalysisContent 1.1, applicate al canonico composto); conflitto con meno di due alternative; patch con chiavi locali inesistenti; endpoint di un finding coincidenti.

Una evidenza non citata, una visita mancante, una relazione nota senza verdetto o patch Stale/incompatibili non rappresentate come conflitto non invalidano l'output: producono `ValidIncomplete` (WorkOutput Committed, mai Completed) come in §4.5.

### 7.7 Composizione e validazione server

Il compositore è deterministico e non decide semanticamente: (1) decodifica e valida il DTO; (2) risolve gli alias; (3) genera gli id di §7.5; (4) ricostruisce `analysis.sources` solo dalle fonti originali del catalogo (nessun Source dal modello) e gli `evidenceRefs`; (5) costruisce `evidenceMap` dalle associazioni esplicite item→evidenze; (6) conserva citazioni, range, gruppi multi-blocco e provenienza come nel ledger; (7) compone `SingleCallOutput` e lo valida **con i validatori esistenti** (`ReconciliationValidator.ValidateSingleCall`, `DocumentAnalysisContentRules`, `DocumentAnalysisValidator`, completezza T121) sul canonico, senza allentarli.

Mapping A/B/Cross, rispettando gli invarianti di T120/T121:
- `results` esattamente `A,B,Cross`; `visitedEntryIds` di A e B dagli alias dichiarati in `visited` (alias mancanti: gap `entry_not_visited`, mai riempiti dal server); Cross = intersezione di A e B.
- Finding → `ReconcileCandidate` più `CandidateDisposition` nel result della vista dichiarata: `Compared` oppure `Open` (con `reason` obbligatorio); `comparisonOutputHashes = [aiOutputHash]`, hash del DTO e non dell'OutputHash del WorkOutput che lo contiene (dipendenza circolare).
- `knownRelationChecks`: `Confirmed`/`NoRelation` → candidato sulla coppia della relazione nota con disposition `Compared` (reason `no_relation` per `NoRelation`); `OpenForPM` → `Open`; senza verdetto resta `known_relation_unresolved`. Il `basis` è quello della relazione persistita (da esporre in `SnapshotRelation` se oggi non è portato).
- Item → `ItemProposal` nel result Cross. **Prima** si calcola la base (`DocumentAnalysisBase.Compute`) e **poi** le patch (`baseHash`, `expectedHash`, `patchId`, `sourceIds`), tutte validate contro la stessa base; le `AddRelation` accettate alimentano le relazioni di run append-only come oggi. Le patch incompatibili restano `PreservedAsConflict` solo se rappresentate in `analysis.conflicts` (regola esistente).
- Vietati: Completed incompleto, completezza dichiarata dal modello, risoluzione di conflitti, scelta per data o autorevolezza.

### 7.8 Versioni e compatibilità storica

| Elemento | v2 (storico, frozen) | v3 |
| --- | --- | --- |
| Definition workflow | `project-analysis-documents-v2` | `project-analysis-documents-v3` |
| Prompt | `document-analysis-v2` (testo allo stato del commit 9c7ac3b, non più modificabile) | `document-analysis-v3` (`Prompts/document-analysis-v3.md`) |
| Schema output AI / WorkOutput | `document-analysis-1` | `document-analysis-alias-1` |
| Algoritmo / validazione | `analysis-single-v1` / `document-analysis-validation-v1` | `analysis-single-v2` / `document-analysis-validation-v2` |
| Catalogo alias / id canonici | n/a | `alias-catalog-1` / `canon-ids-1` |
| AnalysisContent, reconcile, generazione, chat | 1.1.0 / `reconcile-1` / `project-document-documents-v2` / `project-chat-documents-v2` | invariati |

Freeze (T057): `SourceSnapshotStore` (`AnalysisDefinition`, `AnalysisPromptVersion`, `AnalysisPromptVersions()`) scrive v3 per le **nuove** richieste di Analizza con fonti documentali; `PromptVersions` congela anche schema AI, catalogo e id canonici, e il manifest ne include gli hash (configHash). Routing (T123): `AnalysisWorkflow.ExecuteAsync` instrada le definizioni {v2, v3} a `DocumentAnalysisWorkflow`, che sceglie l'implementazione dalla `WorkflowDefinitionVersion` congelata sul run; definizione o versione ignota → `workflow_incompatible`, mai downgrade o reinterpretazione. Il preflight di classificazione T124 (`WorkflowRegimeProbe`/`BuildRegimePayload`) misura lo **schema del DTO v3** per i root v3, non il wrapper v2. Un run v2 già richiesto, con WorkOutput o checkpoint, prosegue con il codice v2 e le proprie versioni; un run v3 non usa mai il decoder v2. Run 001, chat, generazione e revisioni 1.0/1.1 esistenti restano invariati. Il DTO v3 non entra in alcun record: la revisione persistita è AnalysisContent 1.1.0 composto.

### 7.9 Matrice errori e codici

| Condizione | Esito | Codice |
| --- | --- | --- |
| Output troncato (`finish=length`), JSON non decodificabile, extra/null/enum/pattern errati | permanente, nessun WorkOutput | `work_output_invalid` |
| Alias sconosciuto/fuori snapshot/namespace sbagliato/formato non canonico, duplicati vietati, regole 1.1 sul canonico | permanente | `work_output_invalid` (violazione con alias e percorso) |
| Evidenza non citata, visita mancante, relazione nota senza verdetto, patch Stale/Invalid necessaria | WorkOutput Committed riutilizzabile, nessuna revisione | `coverage_incomplete` |
| Id canonico in collisione, stesso `patchId` con payload diverso, `canonicalHash` ricomposto diverso | permanente | `integrity_conflict` |
| `catalogHash` ricostruito diverso dal congelato, WorkOutput o checkpoint non compatibile | permanente, nessuna reinferenza | `checkpoint_incompatible` |
| Fonte originale non verificabile alla costruzione del catalogo (hash artefatto, range, quote) | permanente | `source_snapshot_invalid` |
| Definizione, prompt, schema o versione ignoti o non supportati | permanente | `workflow_incompatible` |
| Runtime, finestra o tokenizer non verificabili; payload oltre budget | come oggi | `runtime_unverified` / `document_limit_exceeded` |

### 7.10 Prova e punti aperti

Test, perimetro e gate sono in tasks T125. Prima verifica reale: N1–N3 su Qwen3.6-35B-Smart, W=131072, reasoning OFF, output 16384, registrando **separatamente** validità del DTO, ricostruzione, validazione canonica, copertura, citazioni, semantica, conflitti, token, durata e `finish`. Il contratto non garantisce qualità semantica: un conflitto gold perso o una citazione semanticamente errata restano FAIL anche con struttura valida. Decisioni aperte, non risolte qui: (a) `visited` è un'attestazione del modello su alias (breve, basso rischio di copia) e non è derivata dalla mappatura item→evidenze, per non fabbricare un'attestazione; (b) chat e generazione citano ancora `[fonte:<UUID>]` (1 UUID corrotto su 23 turni di chat, intercettato dal validatore): estendere gli alias a quei grafi è fuori da T125; (c) l'effetto di `presence-penalty` del profilo Smart sulla copia di identificativi non è isolato; (d) token alias nei testi degli item sono rischio semantico (coverage-2), non errore.


### 7.11 Bozza con alias (T127, 2026-11-03)

Estende alla fase Document (§4.5, generazione dai soli item integrali confermati) lo stesso confine di §7.2: il modello decide la forma del testo, il server possiede gli id canonici. Motivo: nella terza esecuzione N3 reale il modello ha corrotto un UUID di item citato nei blocchi (`work_output_invalid`, id inesistente + item reale non rappresentato).

- **Messaggio**: item e alternative dei conflitti con alias `I<n>` (ordine di `DocumentDraftPackage`), fonti con alias `F<n>` solo se sostengono almeno un item. Nessun UUID, hash o id di analisi/progetto/revisione; l'id del conflitto e le fonti della sola risoluzione non hanno alias (non citabili). Il modello non riproduce nulla di ciò che il server possiede.
- **DTO `document-draft-alias-1`**: `{sectionKey, blocks:[{items:[I<n>], markdown, sources:[F<n>]}]}`, codec strict e schema con pattern esatto; nel Markdown i marcatori sono `[fonte:F<n>]`.
- **Risoluzione**: alias sconosciuto, duplicato nel blocco, non citabile (id di conflitto, fonte della sola risoluzione, UUID al posto dell'alias) o marcatore non alias → `work_output_invalid`. Il server ricostruisce `itemIds`/`sourceIds` canonici e riscrive i marcatori in `[fonte:<UUID>]`; poi `DocumentDraftValidator.ValidateBlocks` (invariato: solo item integrali citabili, fonte ammessa solo se sostiene un item del blocco, copertura, item non rappresentati dichiarati) valida il risultato canonico. Il server non assegna mai item ai blocchi.
- **WorkOutput** `document-draft-alias-1`: DTO, `catalogHash` (catalogo `draft-alias-catalog-1`, funzione pura della revisione confermata congelata), `aiOutputHash`, `canonicalHash` e Markdown; a ogni commit la ricomposizione dal DTO committato verifica gli hash (`checkpoint_incompatible` / `work_output_invalid` / `integrity_conflict`), mai una reinferenza.
- **Versioni**: definizione `project-document-documents-v3`, prompt `document-draft-v3`, schema `document-draft-alias-1`, catalogo `draft-alias-catalog-1`, validazione `document-draft-validation-v1` (invariata); freeze in `SourceSnapshotStore` per le sole NUOVE richieste di Genera. I run già congelati restano `project-document-documents-v2` con prompt, decoder e WorkOutput v2; definizione o versione ignota → `workflow_incompatible`. Routing (`DocumentWorkflow`) e preflight T124 estesi a {v2, v3}. Nessuna migrazione, nessuna ADR, nessuna modifica a AnalysisContent 1.1.0.

### 7.12 Nota di prompt: classificazione semantica delle assunzioni (T125, 2026-11-03)

Il prompt di analisi v3 (`document-analysis-v3`, nuova versione di testo, v2 storico intatto) esplicita le quattro classificazioni: `Documented` = fatto o requisito sostenuto dai documenti; `Declared` = dichiarato dal PM; `Assumed` = ipotesi non confermata introdotta come assunzione; `Inferred` = deduzione da validare. Un fatto documentato non va spostato in `assumptions` solo perché contiene un'incertezza o una condizione. Origine: N2 reale, voce documentata classificata `Documented` in `assumptions` e scartata dal validatore. Il validatore resta rigoroso (§1: ipotesi e deduzioni restano Assumed/Inferred anche se documentate); nessuna conversione automatica Documented→Assumed. Verifica: test di contratto del prompt in tasks T125.

## 8. Chat con alias (T128, 2026-11-03)

Estende a Chat documentale lo stesso confine di §7.2: il modello sceglie quali fonti citare, il server possiede gli identificativi canonici. Risolve il punto aperto (b) di §7.10 (un UUID di fonte malformato nella chat, N3 reale, quinta esecuzione). Stato: contratto approvato dall'autore, non implementato.

- **Catalogo `chat-alias-catalog-1`**: funzione pura delle fonti ammesse dello snapshot congelato del turno (stesse fonti oggi ammesse da `DocumentChatContract.Validate`), alias `F<n>` da 1 secondo l'ordine di lettura del messaggio. Un alias per **`SourceId` distinto**, non per Evidence: una fonte condivisa da più evidenze ha un solo alias; le parti di una citazione multi-blocco (`citationGroupId`, `part`) restano fonti distinte con il proprio alias, con gruppi e ordine invariati. Il catalogo registra alias→`SourceId` (e provenienza/range/citazione letterale invariati) e `catalogHash`; immutabile, senza identità fuori dal turno.
- **Messaggio e output**: le fonti appaiono con `F<n>` e citazione letterale, senza UUID né hash; la risposta resta `{mode,answer}` e cita con `[fonte:F<n>]`.
- **Risoluzione**: formato esatto (`[fonte:F<n>]`, case-sensitive, atomico); alias sconosciuto, fuori snapshot, duplicato non consentito o marcatore malformato → `work_output_invalid`. Mai correzione fuzzy né sostituzione per somiglianza, mai aggiunta di fonti non citate, nessuna inferenza per convertire gli alias. Il server traduce in `[fonte:<UUID>]`, esegue `DocumentChatContract.Validate` sul testo canonico e persiste/restituisce **solo** il formato canonico pubblico; il mapping non esce dal server.
- **Invarianti**: citazioni verbatim, range, provenienza e gruppi multi-blocco invariati; snapshot e mapping immutabili; replay, retry e recovery idempotenti con lo stesso catalogo (hash verificato: `checkpoint_incompatible` se diverso); chat senza documenti invariata (`no_document_use` senza marcatori).
- **Versioni e compatibilità**: definizione `project-chat-documents-v3`, prompt `document-chat-v3`, schema e catalogo nuovi; freeze in `SourceSnapshotStore` per le sole nuove richieste; turni e checkpoint già congelati restano `project-chat-documents-v2` (UUID) con il loro decoder, senza conversione; routing T123 {v2, v3}, definizione ignota → `workflow_incompatible`; preflight T124 sullo schema v3. Nessuna migrazione, nessuna ADR, nessuna modifica a AnalysisContent 1.1.0. Prova e gate in tasks T128.

## 9. Retry guidato della bozza (T129, 2026-11-03)

Eccezione circoscritta, per la sola generazione documentale (§7.11), al principio «schema/citazione invalida permanente» di §6. Stato: contratto approvato dall'autore, non implementato.

- **Condizione**: la validazione canonica (`DocumentDraftValidator`, invariato) rileva item obbligatori **non rappresentati** e nessun'altra violazione. Il primo output resta invalido e non committato.
- **Secondo invio**: snapshot confermato, versioni e fonti canoniche, alias ammessi (§7.11), elenco preciso degli item mancanti, istruzione di produrre la bozza **completa**. È una rigenerazione: nessun merge o patch del primo output, il server non scrive testo di item e non assegna item ai blocchi.
- **Limiti**: al massimo 1 retry per operazione di generazione; consuma il budget `MaxAiCalls` come ogni chiamata; deadline originale invariata; `CallCount`, attempt e contatori di regime non azzerati; nessuna promozione di regime per sola invalidità; nessun retry se deadline o budget residuo non bastano; nessun doppio commit.
- **Esclusi dal retry** (permanenti, `work_output_invalid`): errori strutturali generici, fonti inventate, provenienza violata, output malevoli.
- **Esito**: il risultato finale deve superare tutti i validatori e la coverage originale, ed è `Proposed`/`NotApproved`; secondo fallimento ⇒ run `Failed`. Il retry è integrato in MAF/WorkOutput/checkpoint/fencing, senza scheduler parallelo; lo stato «retry consumato» è persistito, quindi un crash fra primo fallimento e secondo invio (o un restart/worker concorrente) non autorizza tentativi ulteriori e il replay del secondo risultato già committato non reinferisce. Prova e gate in tasks T129.

## 10. Retry guidato dell'analisi (T130, 2026-11-04)

Eccezione circoscritta, per la sola analisi documentale v3 (§7), al principio «output invalido permanente» di §6, analoga a §9. Stato: contratto approvato dall'autore. Origine: prove reali su DGX, dove il modello scarta in modo non deterministico (a) chiavi locali duplicate (`work_output_invalid`, «chiave locale iN duplicata…») e (b) evidenze non mappate su alcun item (`coverage_incomplete` con `evidence_not_mapped`), nonostante il prompt `document-analysis-v3` li vieti.

- **Condizione**: la composizione/validazione canonica (validatori invariati) rileva come **unica** violazione (a) chiavi locali duplicate oppure (b) evidenze non mappate (`evidence_not_mapped` come unico gap). Il primo output resta invalido e non committato.
- **Secondo invio**: lo stesso messaggio del primo (dialogo, evidenze, fonti e relazioni con alias) più l'elenco preciso delle chiavi duplicate o delle evidenze (alias `E<n>`) non mappate e l'istruzione di produrre l'analisi **completa**. È una rigenerazione: nessun merge o patch del primo output, il server non scrive semantica.
- **Limiti**: al massimo 1 retry per operazione di analisi; consuma `MaxAiCalls`; deadline originale invariata; `CallCount`, attempt e contatori di regime non azzerati; nessuna promozione di regime; nessun retry se deadline o budget residuo non bastano; nessun doppio commit.
- **Esclusi dal retry**: ogni altro errore strutturale, fonti/citazioni inventate, provenienza violata, output malevoli, altri gap di copertura.
- **Esito**: il risultato finale deve superare tutti i validatori invariati; secondo fallimento ⇒ run `Failed` con il codice del secondo errore (nessun terzo invio). Lo stato «retry consumato» è persistito (batch-marcatore fenced come in §9): un crash fra primo fallimento e secondo invio, un restart o un worker concorrente non autorizzano tentativi ulteriori e, dopo la rinuncia, vale il motivo originale. Il piano v2 congelato non ha retry. Prova e gate in tasks T130.
