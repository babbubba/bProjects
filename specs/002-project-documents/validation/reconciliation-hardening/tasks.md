# Riconciliazione gerarchica della 002 — task operativi

Diagnosi: [assessment.md](assessment.md). Piano: [plan.md](plan.md). Gli ID `RH-xxx` sono locali a questa cartella.

**Stato:** allegato tecnico di un mandato. Il `CLAUDE.md` del progetto fa di `specs/002-project-documents/tasks.md` l'unica baseline operativa. **D11 è approvata soltanto per il mandato M0 (2026-11-13)**: RH-001…RH-006 sono riportati nel `tasks.md` ufficiale come **T154–T159** (RH-001→T154, RH-006→T155, RH-002→T156, RH-003→T157, RH-004→T158, RH-005→T159; sezione «Delta Reconciliation Hardening M0»). M1–M6 **non sono autorizzati** e non sono riportati nel registro ufficiale. Questo file non va usato come pianificazione parallela. Spuntare una casella solo a task completato e verificato (lo stato qui e nel registro ufficiale va tenuto allineato).

## Regole valide per tutti i task

- **Prerequisito:** mandato con D1, D2, D2b, D3, D4, D6, D9 e D11 approvate ([plan.md](plan.md) §11). M5 richiede l'autorizzazione D8; M6 richiede D1 e l'esito positivo di M5.
- **Ordine consigliato:** eseguire M0 **prima** di confermare D2 e D2b. Le sue evidenze (RH-004, RH-005, RH-006) possono cambiarle.
- **Branch:** di lavoro derivato da `feature/003-retrieval-evidence-search@c76c6c4` (D9). Commit piccoli, Conventional Commits, con riferimento «002 reconciliation-hardening RH-xxx», solo se il mandato li autorizza. Niente merge su `main`, niente push, niente `Co-Authored-By`. Preservare i file non tracciati (`benchmarks/`, `docs/roadmap/*-HANDOFF.md`, `docs/acceptance-trace/`).
- **Mai modificare:** `document-reconcile-v3/v4/v5.md`; profili `-1/-2/-3`; golden di `configHash` v8/v9/v10 e degli schemi; il ramo v10 di `ContractOf`; manifesti T040 v1–v6; `specs/003-retrieval-evidence-search/**` (salvo D7); il port (`DocumentBatchPort.cs`); test storici, se non come previsto da un task.
- **Nessuna inferenza reale** fino a M5. `BPROJECTS_TEST_DGX_ALLOW` non va impostata.
- **Comandi** (da `backend/`):
  - `dotnet build bProjects.sln --no-restore`
  - `dotnet test --solution bProjects.sln --filter-class "*<Classe>*"`
  - suite completa `dotnet test --solution bProjects.sln`, con `BPROJECTS_TEST_POSTGRES` impostata e parser reale dove richiesto.
- **STOP generale:** se un test storico o golden v2–v10 diventa rosso, se serve cambiare una regola non prevista dal task o se compare una nuova incoerenza fra schema, compositore e validatore: fermarsi, documentarla nella sezione «Evidenze» in coda ad `assessment.md` e chiedere una decisione. Non allentare regole né modificare attese per far passare un test.

---

## M0 — Baseline offline e strumenti di verifica (nessuna modifica del codice di produzione)

### [x] RH-001 — Fixture di replay dagli archivi dei Run 2–5
- **Scopo:** rendere versionati, e indipendenti da `%TEMP%`, gli output reali della gerarchia.
- **File:**
  - nuovo `backend/tests/bProjects.IntegrationTests/Reconciliation/Replay/Extract-ReplayFixtures.ps1`, strumento ripetibile versionato;
  - fixture `Reconciliation/Replay/run{R}-{NNN}.json` e `Reconciliation/Replay/PROVENANCE.json`;
  - nel `.csproj` dei test, `<None Include="Reconciliation/**" CopyToOutputDirectory="PreserveNewest" />`.
- **Modifiche precise:**
  - sessioni d'archivio del §1 dell'assessment; chiamate Run 2: 022–026, Run 3: 022–026, Run 4: 022–036, Run 5: 022–030 (34 file);
  - per ciascuna un JSON con `run`, `call`, `session`, `requestSha256`, `assistantSha256` (dei file d'archivio), e con i seguenti campi estratti **solo dal corpo** della richiesta (mai `headers`):
    - `stage` (da «Tipo di batch»: foglia→leaf, riduzione→reduce, cross, espansione→expand, confronto→compare), `view`, `system`;
    - `focus` `{type,left,right,rationale}` oppure null, `groups` (alias → `N1`/`N2`/`N1,N2`) oppure null;
    - `entries` `[{alias,origin,kind,polarity,statement,sources:[{alias,role,quote}]}]` dalle intestazioni `<<<VOCE …>>>` e `<<<FONTE …>>>`, `nodes` `[{alias,view,descriptor}]`, `items` `[{handle,evidence:[alias],text}]`, `relations` `[{alias,from,type,to,basis}]`;
    - `assistant`: testo grezzo, invariato;
  - `PROVENANCE.json` elenca file, hash e la dichiarazione «corpus sintetico T040 (seed 20261106), nessun dato cliente».
- **Test:** `ReconcileReplayFixturesTests.Fixtures_are_complete_and_untampered`:
  - 34 fixture presenti;
  - `assistantSha256` = SHA-256 del campo `assistant`;
  - nessuna occorrenza di `Bearer`, `Authorization`, `api_key`, dell'IP del DGX o di `/v1/`;
  - `stage` nell'insieme chiuso.
- **Completamento:** test verde; `git diff --stat backend/src` vuoto.
- **Dipendenze:** nessuna. **Effort:** 0,75 gg.
- **Decisioni risolte:** fixture del corpus sintetico T040 versionabili (precedente: `ReconcileSchemaProfileV9Tests.Run3Call026`).
- **Impatto:** nessuno.
- **STOP:** sessione o file mancante, o hash diverso: fermarsi, senza ricostruire a mano.

### [x] RH-006 — Costruttore di fixture → richiesta, catalogo e snapshot
- **Scopo:** comporre una fixture reale con il compositore di produzione senza database né tokenizer, condizione per RH-002, RH-017 e RH-022.
- **File:** nuovo `backend/tests/bProjects.IntegrationTests/Reconciliation/Replay/ReplayFixtureBuilder.cs`. Riusare i costruttori dei test di `ReconcileAliasComposerTests` e `ReconcileRig` per `AnalysisAliasCatalog`, `ReconcileAliasCatalog`, `ReconciliationSnapshot` e `ReconcileBatchRequest`.
- **Modifiche precise:**
  - `EvidenceId` = GUID deterministico da (`run`, alias);
  - `ContentHash` = SHA-256 dell'enunciato;
  - `SourceId` deterministico da (`run`, alias della fonte);
  - origine, tipo, polarità ed enunciato dalla fixture;
  - per espansione e confronto: candidato in esame dal campo `focus` (estremi E/N risolti sugli alias del batch) e `groups` per la v10;
  - per espansione e confronto, la base del passaggio (`ReconciliationBase`) costruita dagli `items` della fixture.
- **Test:** `ReplayFixtureBuilderTests`:
  - le 34 fixture producono una richiesta il cui messaggio alias (`ReconcileAliasMessages.Build`) contiene le stesse righe `<<<VOCE [E…]` della richiesta originale, nello stesso ordine;
  - per la 025 del Run 3 il messaggio ricostruito è uguale a quello archiviato riga per riga, salvo intestazioni e item già proposti.
- **Completamento:** test verde.
- **Dipendenze:** RH-001. **Effort:** 1 gg.
- **Decisioni risolte:** il replay è **per batch**. Non si ricostruiscono gerarchie intere: i confini delle foglie dipendono dal tokenizer reale.
- **STOP:** se il catalogo non è ricostruibile senza GUID reali, limitarsi alle fixture ricostruibili e documentarle.

### [x] RH-002 — Matrice di contratto eseguibile (caratterizzazione v10 e v6)
- **Scopo:** fissare in test il comportamento attuale della matrice del §4.1 dell'assessment, incoerenze comprese.
- **File:** nuovo `ReconcileContractMatrixTests.cs`, che usa `ReconcileSchemaSubset` (validazione contro lo schema inviato), `ReconcileAliasComposer.Compose` e `ReconciliationHierarchy.InvokeAsync` o `ReconcileRig`.
- **Casi** (v10 con profilo `-3`, e v6 con schema unico; si asserisce l'esito attuale):
  1. Confronto con `focus Open` e motivo, 0 candidati, `noFindings` null: ammesso dallo schema, rifiutato con «noFindingsReason: obbligatorio…» (I-1).
  2. Confronto `Open` con `reason` null: ammesso dallo schema, rifiutato dal compositore (I-2).
  3. Confronto con un candidato `Contradicts` sulla stessa coppia e `focus Compared`: accettato. Il figlio diventa `Compared` per cache, senza chiamata (I-3, motore come in `ReconciliationCandidateTests`).
  4. Confronto con un candidato `DependsOn` invertito e `focus Open`: accettato. Il figlio eredita `Open` e l'assembly produce **due** conflitti per la stessa coppia (I-3, forma 029 del Run 4).
  5. Blocco di espansione v10 con 0 candidati, `focus` null, `noFindings` null: rifiutato (I-1 latente).
  6. Foglia B con 0 candidati e `noFindings` null: rifiutata.
  7. Forma dei confronti del Run 4 (candidato ridondante più `Open`): accettata.
  8. Foglia A che rende una Gap come `requirements`: accettata dal compositore (I-4).
  9. Foglia A con un `openQuestions` senza evidenze e una voce non citata: accettata (I-5).
- **Completamento:** tutti i casi verdi sul codice attuale.
- **Dipendenze:** nessuna. **Effort:** 1 gg.
- **STOP:** un esito diverso dall'assessment va documentato e la diagnosi corretta.

### [x] RH-003 — Generatore di istanze dello schema e test di proprietà (v10)
- **Scopo:** enumerare in modo sistematico le classi «ammesso dalla grammatica, rifiutato dalla validazione».
- **File:**
  - nuovo `ReconcileSchemaInstanceGenerator.cs`: generatore deterministico (seed 20261113) per lo stesso sottoinsieme di `ReconcileSchemaSubset.Keywords` (`type` anche array, `enum`, `pattern` degli alias, `minLength`, `minItems`/`maxItems`, `anyOf`, `properties`/`required`/`additionalProperties`);
  - nuovo `ReconcileSchemaPropertyTests.cs`.
- **Modifiche precise:**
  - alias dall'universo del batch: presenti, fuori batch dello stesso formato, nodi, item, relazioni;
  - stringhe da un pool con `""`, `" "`, una frase normale, una stringa da 600 caratteri;
  - array da 0 a 3 elementi; ≥ 2.000 istanze per variante;
  - ogni istanza è validata con `ReconcileSchemaSubset` (deve esserlo per costruzione), poi composta e validata come in RH-002.
- **Test:** per le sei varianti del profilo `-3`, registra le categorie di rifiuto (per messaggio, ammesso solo in questo test di caratterizzazione) con i conteggi e asserisce l'insieme atteso: I-1, I-2, alias sconosciuto o fuori batch, estremi coincidenti, candidato duplicato, chiave duplicata, descriptor oltre 512 o di soli spazi, testo di soli spazi (anche nelle patch), patch incoerente.
- **Completamento:** test verde e deterministico.
- **Dipendenze:** RH-002. **Effort:** 1,5 gg.
- **STOP:** una categoria non prevista è una nuova incoerenza da documentare.

### [x] RH-004 — Contatore delle richieste di misura e scomposizione per misuratore
- **Scopo:** verificare offline il meccanismo del §7 dell'assessment e riconciliarlo con la prova isolata (577 `/apply-template` e 865 `/tokenize` prima della gerarchia). Fornisce il criterio d'ingresso dell'opzione O3.
- **File:**
  - nuovo helper `CountingLlamaRuntime.cs` (`HttpMessageHandler`):
    - risponde a `/v1/models`, `/props`, `/slots`, `/tokenize` (lunghezza/3,5, arrotondata per eccesso) e `/apply-template` (concatenazione);
    - conta per percorso e classifica il tipo dal messaggio utente reso: `Tipo di batch: foglia|riduzione|cross|espansione|confronto`, oppure `{` per una pagina, altrimenti «altro»;
  - nuovo `ReconcileMeasurementBudgetTests.cs`.
- **Modifiche precise:**
  - `DocumentReconcilePlan` v10 e v6 sul ledger di `RetrievalIsolatedFixture` (283 evidenze), in due configurazioni: con `DirectoryFacts` vuoti (come la prova isolata) e con `DirectoryFacts` deterministici (soggetto, oggetto e azione dal testo della regola);
  - profilo W=32768, O=16384, M=1024;
  - `PrepareAsync(0)`, poi lettura dei contatori.
- **Test:**
  - uguaglianze di meccanismo: `apply-template(leaf)` = Σ<sub>foglie</sub>(*k*+1) − 1 per vista; `apply-template(page)` = Σ<sub>catene</sub>(voci + interruzioni); `tokenize` = 2·leaf + page + 2·rimisure;
  - golden dei conteggi per entrambe le configurazioni.
- **Completamento:**
  - tabella in «Evidenze M0»: conteggi per tipo, quota delle pagine, confronto con la prova isolata;
  - se la quota delle pagine è < 30% delle richieste, O3 resta chiusa.
- **Dipendenze:** nessuna. **Effort:** 0,75 gg.
- **STOP:** se le uguaglianze non valgono, correggere il §7 dell'assessment prima di M3.

### [x] RH-005 — Simulazione offline del costo dell'espansione v10
- **Scopo:** misurare blocchi, chiamate ed esiti del precheck al variare dei figli per blocco. Criterio d'ingresso dell'opzione O5.
- **File:** nuovo `ReconcileExpansionCostSimulationTests.cs` (motore puro con `ReconcileInvoker` finto).
- **Modifiche precise:**
  - ledger RH-004 (con `DirectoryFacts`);
  - modello finto: foglie con descriptor e 0–3 candidati fissi; riduzione e cross con un candidato di gruppo; espansione con *c* figli per blocco, *c* ∈ {0, 1, 2, 3, 5, 16}; confronti `Compared`;
  - `MaxAiCallsPerRun` = 512.
- **Test:** per ogni *c* registra blocchi per candidato, chiamate totali, `NotProcessed` con motivo, esito della completezza; determinismo su due esecuzioni.
- **Completamento:** tabella in «Evidenze M0».
- **Dipendenze:** RH-004. **Effort:** 0,75 gg.
- **STOP:** nessuna modifica dell'algoritmo.

**Checkpoint C0:**
- M0 verde;
- suite completa invariata;
- `git diff --stat backend/src` vuoto;
- evidenze M0 registrate;
- riesame di D2, D2b e delle opzioni O3 e O5 con queste evidenze.

---

## M1 — Definizione v11 e contratto per stadio (D1 per l'identità, D3, D4)

### [ ] RH-010 — Identità della definizione v11 (non default)
- **File e modifiche:**
  - `AI/DocumentAnalysisWorkflow.cs`:
    - `DefinitionVersionV11 = "project-analysis-documents-v11"`, `ReconcileContractKey = "reconcileContract"`;
    - in `ContractOf`, ramo che restituisce 11 solo con `StagedKeysMatch(run, ReconcileAliasContracts.PromptVersionV6)`, nessuna chiave `retrieval*`, `reconcileSchemaProfile` = `reconcile-alias-schema-by-stage-1`, `reconcileExpansion` = `expansion-grid-1` e `reconcileContract` = `reconcile-stage-contract-1`;
    - `ReconcilePromptOf`, `ReconcileSchemaProfileOf`, `ReconcileExpansionOf` e il nuovo `ReconcileContractOf` per la v11;
    - la v11 va aggiunta a: elenco di `:707`, `contract switch` di `:839` e `:874` (`11 => DefinitionVersionV11`), **condizione `contract is 6 or 7 or 8 or 9 or 10` di `:678`** (formato di messaggio 2 della single-call) e **di `:835`** (instradamento verso `RunStagedAsync`);
    - `:901`: la v11 non usa il recupero.
  - `Documents/SourceSnapshotStore.cs`: `AnalysisDefinitionV11` e `AnalysisPromptVersionsV11()` = chiavi della v6 con `reconcile` = `document-reconcile-v6` più le tre chiavi nuove. `DocumentaryAnalysisFreeze` **invariata**.
  - `AI/AnalysisWorkflow.cs:393`, `Evidence/EvidenceCoverage.cs:1066-1068` (`AppliesStructuralRepetition`), `AI/DocumentAnalysisAliasPlan.cs:46`: v11 dove compare la v6.
  - `AI/ReconcileAliasContracts.cs`: costanti `PromptVersionV6`, `SchemaProfileByStage`, `StageContract`.
  - `ReconcileAliasMode`: parametro facoltativo `string? StageContract = null`.
- **Test** (nuovo `ReconcileDefinitionV11Tests`):
  - `ContractOf`: chiavi esatte ⇒ 11; v11 con una chiave `retrieval*`, con prompt v5 o con profilo `-3` ⇒ `workflow_incompatible`;
  - freeze v11 = freeze v6 più esattamente quattro differenze;
  - golden dei freeze v6…v10 invariati;
  - `DocumentaryAnalysisFreeze` invariata;
  - **single-call v11 = v6**: con lo stesso ledger entro budget, corpo HTTP della richiesta di analisi uguale byte per byte a quello della v6, salvo `definition` e chiavi congelate dove compaiono nell'identità;
  - **instradamento oltre budget v11 = v6**: con un ledger fuori dalla single-call, entrambe entrano in `RunStagedAsync`.
- **Completamento:** test nuovi e storici verdi.
- **Dipendenze:** C0. **Effort:** 1,25 gg.
- **Decisioni risolte:** il ramo v10 di `ContractOf` non si tocca (nessun controllo nuovo di assenza di chiavi).
- **STOP:** se serve toccare un golden v6–v10 o il ramo v10.

### [ ] RH-011 — Tabella di stadio e profilo `reconcile-alias-schema-by-stage-1`
- **File e modifiche:**
  - nuovo `AI/ReconcileStageContracts.cs` con `record ReconcileStageContract(string Stage, ReconcileBatchSchema Variant, bool DescriptorRequired, bool ModelItems, char? EndpointPrefix, int? CandidateCap, ReconcileFocusRule Focus, bool Patches, bool ServerItems)`, `enum ReconcileFocusRule { None, NullOrUnresolved, ComparedOrOpen }` e la tabella statica:

    | Stadio | Variante | descriptor | item del modello | estremi | tetto | focus | patch | item del server |
    |---|---|---|---|---|---|---|---|---|
    | leaf A | LeafA | sì | no | E | — | None | no | **sì** |
    | leaf B | LeafB | sì | no | E | — | None | no | no |
    | reduce | Reduce | sì | no | N | — | None | no | no |
    | cross | Cross | no | no | N | — | None | no | no |
    | expand | Expand | no | no | E | 16 | NullOrUnresolved | sì | no |
    | compare | Compare | no | no | — | 0 | ComparedOrOpen | sì | no |

  - `AI/ReconcileAliasContracts.cs`: dizionario separato `BatchSchemasByStage`:
    - LeafA = copia esatta di LeafB del `-2`;
    - Expand = Expand del `-3`;
    - Compare = Compare del `-2` con `candidates.maxItems = 0` e `focus` = `{"anyOf":[{oggetto, outcome {"type":"string","enum":["Compared"]}, reason nullable}, {oggetto, outcome {"type":"string","enum":["Open"]}, reason {"type":"string","minLength":1}}]}`;
    - Reduce, Cross e LeafB = `-2`;
    - `noFindings` invariato (testo nullable);
    - ordine delle proprietà e `required` invariati; **nessun `const`**;
    - `ProfileSchemaFor`, `ProfileVariants` e `ProfileHashes` gestiscono il nuovo profilo.
- **Test** (nuovo `ReconcileSchemaProfileByStageTests`):
  - golden dei sei hash; LeafA e LeafB con lo stesso hash;
  - `ReconcileSchemaSubset.ProviderProblems` vuoto per ogni variante;
  - corrispondenza campo per campo con la tabella;
  - hash dei profili `-1/-2/-3` invariati.
- **Dipendenze:** RH-010. **Effort:** 0,75 gg.
- **Decisioni risolte:** `anyOf` con l'esito prima del motivo (assessment §4.3); nessun `maxLength`.
- **STOP:** se serve un costrutto fuori da `ReconcileSchemaSubset.Keywords`.

### [ ] RH-012 — Regole di validazione V1/V2
- **File e modifiche:**
  - `AI/ReconciliationContracts.cs`: `public enum ReconcileValidationRules { V1, V2 }` e `CheckResult(ReconcileResult r, string sourceHash, ReconcileValidationRules rules = ReconcileValidationRules.V1)`. In V2 **nessuna** regola su `NoFindingsReason` (`:694-702`): il campo è informativo e persistito così com'è. `CheckSingleCall` invariato.
  - `ReconciliationContext`: parametro finale `ReconcileValidationRules Rules = V1`.
  - `ReconciliationHierarchy.InvokeAsync`: parametro `rules`, passato da `BuildAsync` e da `ReconciliationCandidates.RunBatchAsync`.
  - `DocumentReconcilePlan`: con `StageContract`, `Rules = V2` e `reconcile-validation-v2` in `Expectation` e nel `WorkOutputInput`.
- **Test:**
  - unit V1/V2 sui casi 1, 5 e 6 di RH-002 (V1 rifiuta, V2 accetta);
  - V2 accetta `noFindingsReason` di soli spazi;
  - RH-002 verde;
  - attesa del lookup v11 = (`reconcile-1`, `reconcile-validation-v2`).
- **Dipendenze:** RH-010. **Effort:** 0,5 gg.
- **Decisioni risolte:** D3 (la traccia dell'esito è del server: visite, hash, disposizioni).

### [ ] RH-013 — Compositore v11: violazioni tipizzate e confronto senza candidati
- **File e modifiche:**
  - `AI/ReconcileAliasComposer.cs`:
    - `enum ReconcileViolationKind { Undecodable, UnknownAlias, CoincidentEndpoints, Duplicate, DuplicateCandidate, InvalidText, PatchShape, ItemShape, StageShape }`;
    - `record ReconcileViolation(ReconcileViolationKind Kind, string Path, string Message)`;
    - `ReconcileAliasComposition.TypedViolations`; `Violations` resta la lista di stringhe con i messaggi storici, identici;
    - `Compose(..., ReconcileComposeOptions? options = null)`.
  - Tabella completa di etichettatura (righe di `ReconcileAliasComposer.cs` a HEAD `c76c6c4`):

    | Controllo | Righe | Tipo |
    |---|---|---|
    | decodifica fallita | 50-53 | Undecodable |
    | item fuori stadio | 137-141 | StageShape |
    | chiave di item ripetuta | 148-151 | Duplicate |
    | alias ripetuto in una lista | 106-109 | Duplicate |
    | alias sconosciuto o fuori batch (evidenze, estremi, item, relazioni, from/to) | 114-117, 212-215, 221-224, 377-380, 389-392, 414-422 | UnknownAlias |
    | testo dell'item vuoto | 155-158 | InvalidText |
    | classificazione, `assumptions`, evidenze dell'item | 162-182 | ItemShape |
    | estremo con prefisso sbagliato | 204-208 | StageShape |
    | tetto dell'espansione superato | 237-240 | StageShape |
    | chiave di candidato ripetuta | 246-249 | Duplicate |
    | estremi coincidenti (candidati e `AddRelation`) | 253-256, 424-427 | CoincidentEndpoints |
    | rationale vuota | 258-261 | InvalidText |
    | candidato duplicato | 271-275 | DuplicateCandidate |
    | focus del confronto mancante o `Unresolved` | 294-297 | StageShape |
    | `Open` senza motivo | 298-301 | InvalidText |
    | focus dell'espansione diverso da `Unresolved` | 313-316 | StageShape |
    | focus fuori stadio | 326-329 | StageShape |
    | patch fuori stadio o base assente | 339-349 | StageShape |
    | chiave di patch ripetuta | 356-359 | Duplicate |
    | `target` non esclusivo, evidenze mancanti per `AddSupport`/`ProposeAlternative`, `AddRelation` su relazione, evidenze diverse dagli estremi, `value` incoerente con `op`, bersaglio assente dalla base | 366-369, 399-402, 407-410, 432-435, 446-449, 457-459, 477-480 | PatchShape. **Correzione M0 (assessment «Evidenze M0», C-4):** `value` incoerente con `op` non raggiunge il compositore nel percorso alias, perché `AiPatchConverter` lo rifiuta in decodifica (`AnalysisAliasContracts.cs:376-382`): è `Undecodable`; il ramo 457-459 è difesa in profondità non raggiungibile |
    | patch duplicata | 489-492 | Duplicate |

  - Solo con `options.StageContract`:
    - candidati nel confronto ⇒ `StageShape`;
    - `descriptor` di soli spazi o oltre 512 rune ⇒ `InvalidText`;
    - `text` e `reason` di `ProposeAlternative`, e `rationale` non nulla di `AddRelation`, di soli spazi ⇒ `InvalidText` (stesse regole di `CheckPatch`, `ReconciliationContracts.cs:858-870`, come difesa in profondità).
- **Test:**
  - un'istanza minima per ogni riga della tabella con il tipo atteso;
  - in v10 il confronto con candidati resta accettato e i messaggi restano identici (`ReconcileAliasComposerTests` verde);
  - `TypedViolations` non vuota se e solo se `Violations` non è vuota.
- **Dipendenze:** RH-011, RH-012. **Effort:** 1 gg.
- **Decisioni risolte:** D4; nessuna fusione dei duplicati; tipi solo diagnostici (nessun retry, vedi opzione O1).
- **STOP:** se i numeri di riga sono cambiati, rilocalizzare per contenuto; se un controllo non si lascia etichettare senza cambiare il messaggio storico, fermarsi.

### [ ] RH-014 — Prompt unico `document-reconcile-v6`, organizzato per stadio
- **File e modifiche:**
  - nuovo `AI/Prompts/document-reconcile-v6.md`, derivato dal v5 con queste sole differenze:
    1. sezione «Output» divisa in un elenco comune (contract, etichette, formato) e in sottosezioni «Foglie», «Riduzione», «Cross», «Espansione», «Confronto»;
    2. `items`: «sempre `[]`: gli item delle evidenze li compone il server»;
    3. la frase del tetto di 16 e la regola «non selezionare i primi 16» compaiono **solo** nella sottosezione «Espansione»;
    4. «Confronto»: `focus` obbligatorio, `Compared` (motivo facoltativo) oppure `Open` (motivo obbligatorio, non risolvere il dubbio); `candidates` sempre `[]`;
    5. `noFindings`: «facoltativo: una breve spiegazione se il batch non ha esiti»;
    6. «Item già proposti» nel formato `[i3] (E3)`, senza testo.
  - Restano letterali: etichette (v5 righe 11–12), anti-istruzioni (v5 riga 9), regole dei conflitti, delle patch e dell'espansione con membership.
  - `ReconcileAliasContracts.InstructionsFor` supporta `document-reconcile-v6`.
- **Test:**
  - SHA di v3/v4/v5 invariati; SHA del v6 pinnato;
  - il v6 non contiene `items` con chiavi `i<n>` né istruzioni di copertura delle foglie;
  - il testo «16» compare solo nella sottosezione «Espansione»;
  - un test di diff verifica che le differenze dal v5 siano solo quelle elencate.
- **Completamento:** test verdi; testo rivisto da un agente revisore, con esito nel commit.
- **Dipendenze:** RH-011. **Effort:** 0,75 gg.
- **Decisioni risolte:** prompt unico, nessuna istruzione nel messaggio utente (separazione dal materiale non fidato). Istruzioni di sistema per stadio = opzione O2.

### [ ] RH-015 — Piano di riconciliazione: ramo v11
- **File e modifiche:**
  - `AI/DocumentReconcilePlan.cs`:
    - il controllo di `:133` accetta, per la v11, il profilo `by-stage-1` con `expansion-grid-1`;
    - `configHash`: ramo nuovo con `prompt`, `instructions` (SHA del v6), `schema`, `catalog`, `canonIds`, `structured`, `schemaProfile`, `schemaProfileHashes`, `expansion` (come la v10) e `stageContract` (`name`, `validation: "reconcile-validation-v2"`, `items: "leaf-projection-1"`, `openEndpoints: "open-endpoints-as-alternatives-1"`, `compare: "no-candidates"`, `messages: ["items-ref-1"]`), più `model`, `maxOutput`, `thinking`, `inputBudget`;
    - piano JSON con la chiave `reconcileContract`;
    - `ComposeAlias` con il formato `items-ref-1` (implementato in RH-021);
    - opzioni del compositore; `Rules = V2`.
  - `AI/DocumentAnalysisWorkflow.cs`: per la v11 costruire `ReconcileAliasMode` con `StageContract`.
- **Test** (`ReconcileDefinitionV11Tests`, con `RetrievalGapFlowTests` invariati):
  - `configHash` v11 diverso da quello v10; golden v8, v9 e v10 invariati;
  - `reconcileContract` nel piano solo per la v11;
  - il primo batch v11 porta lo schema LeafA del profilo `by-stage-1` e le istruzioni v6 (corpo HTTP registrato da un runtime finto).
- **Dipendenze:** RH-012, RH-013, RH-014. **Effort:** 0,75 gg.
- **STOP:** se un golden storico cambia.

### [ ] RH-016 — Equivalenza schema ⇔ compositore ⇔ validazione per la v11
- **File:** nuovo `ReconcileStageContractV11Tests.cs`, con il generatore di RH-003.
- **Test:**
  - ≥ 2.000 istanze per variante del profilo `by-stage-1`, composte con le opzioni v11 e validate con V2, `References` e gerarchia;
  - ogni istanza ammessa dalla grammatica è accettata, oppure rifiutata solo con le **classi residue dichiarate** `UnknownAlias`, `CoincidentEndpoints`, `Duplicate`, `DuplicateCandidate`, `InvalidText`, `PatchShape`;
  - `StageShape`, `ItemShape` e `Undecodable` non compaiono mai. **Correzione M0 (C-4, C-5):** nel profilo `-3` `Undecodable` compare nel 59–61% delle istanze di Expand e Compare perché lo `anyOf` del `value` non è legato a `op`; la condizione vale solo se il profilo `by-stage-1` lega `value` a `op` (coppie `op` `enum` + `value` in `anyOf`), da verificare contro `ReconcileSchemaSubset.Keywords` prima di RH-011;
  - nessun rifiuto proviene da `CheckResult`, `References` o gerarchia su un'istanza accettata dal compositore;
  - casi espliciti:
    - confronto `Open` con motivo, 0 candidati, `noFindings` null ⇒ accettato;
    - confronto `Open` senza motivo ⇒ non valido per la grammatica;
    - confronto con candidati ⇒ non valido per la grammatica;
    - blocco di espansione vuoto ⇒ accettato;
    - foglia B senza candidati né `noFindings` ⇒ accettata.
- **Completamento:** zero rifiuti inattesi; tabella delle classi residue con le frequenze sul generatore in «Evidenze M1» (dato per l'opzione O1).
- **Dipendenze:** RH-003, RH-015. **Effort:** 1 gg.
- **STOP:** una classe inattesa va documentata, senza allentare la regola.

### [ ] RH-017 — Replay v11 per batch delle fixture reali (stadi diversi dalla foglia A)
- **Test** (`ReconcileReplayFixturesTests`, con `ReplayFixtureBuilder`):
  - per ogni fixture di confronto, espansione, foglia B, riduzione e cross:
    - (a) caratterizzazione v10 secondo l'assessment §3: Run 5 030 rifiutata (I-1); Run 4 029–033 accettate; 034/035 accettate come `candidate_unresolved`; 036 non decodificabile; Run 2 026 rifiutata (item); Run 3 026 rifiutata (descriptor);
    - (b) se l'output **così com'è** è valido per lo schema `by-stage-1` della sua variante, la composizione v11 lo accetta; altrimenti «non generabile sotto v11», **senza** trasformare l'output;
  - atteso esplicito: Run 5 030 accettata dalla v11.
- **Completamento:** tabella in «Evidenze M1».
- **Dipendenze:** RH-006, RH-016. **Effort:** 0,5 gg.

**Checkpoint C1:**
- M1 verde;
- suite completa con 0 falliti;
- golden v2–v10 invariati;
- la v11 non è default.

---

## M2 — Item delle foglie composti dal server (D2, D2b)

### [ ] RH-020 — `ReconcileLeafProjection` e tabella `leaf-projection-1`
- **File:** nuovo `AI/ReconcileLeafProjection.cs`:
  - `Version = "leaf-projection-1"`;
  - `static (AiSection Section, DocumentAnalysisClassification Classification) Rule(EvidenceKind kind, EvidenceOriginKind origin, EvidenceCertainty certainty)`: tabella di [plan.md](plan.md) §4, con le condizioni nell'ordine indicato;
  - `static (IReadOnlyList<ItemProposal> Proposals, IReadOnlyDictionary<string, AiSection> Sections) Project(ReconcileBatchRequest request, IReadOnlyDictionary<Guid, DocumentAnalysisEvidence> evidence)`: per ogni voce di `request.Entries`, in ordine (*n* = 1…), `LocalKey = ReconcileAliasComposer.LocalKey(request, "p" + n)`, `Text = evidence[id].Statement` (letterale), `EvidenceIds = [id]`, `SourceIds` = fonti della voce ordinate come in `SortedSourceIds`.
- **Test** (`ReconcileLeafProjectionTests`):
  - tabella esaustiva su tipo × origine × certezza (12 × 2 × 4): il test fallisce se si aggiunge un valore enum senza regola;
  - testo byte per byte uguale all'enunciato;
  - determinismo;
  - `DocumentAnalysisContentRules.Check` senza violazioni sugli item proiettati;
  - nessuna `Alternative` e nessuna certezza `Hypothesized` o `ToClarify` in una sezione strutturale.
- **Dipendenze:** C1. **Effort:** 0,75 gg.
- **Decisioni risolte:** D2 e tabella con la certezza.

### [ ] RH-021 — Integrazione nel compositore, nell'assembly (D2b) e nei messaggi (`items-ref-1`)
- **File e modifiche:**
  - `ReconcileAliasComposer`: con `options.StageContract`, stadio `leaf` e vista A, proposte e sezioni da `ReconcileLeafProjection.Project`; `Ungrounded` vuoti; `items` del modello diversi da `[]` ⇒ `StageShape`. `ReconcileComposeOptions` porta il dizionario delle evidenze. `DocumentReconcilePlan` lo passa in `AbsorbCandidate` e `CommittedAliasTextAsync`.
  - `ReconcileAliasMessages.Build`: parametro `ReconcileMessageFormat format` (`Legacy` | `ItemsRef1`). Con `ItemsRef1` le righe degli item già proposti diventano `[{handle}] ({alias, …})` senza testo; con `Legacy` il messaggio è byte per byte invariato.
  - `DocumentReconcileAssembly.Assemble` (ramo v11, opzione passata dal piano): prima di comporre le sezioni, escludere gli item proiettati che hanno **un'unica** evidenza estremo di almeno un candidato con disposizione `Open`, **e** che non sono bersaglio di patch con disposizione `Applied`, `Duplicate` o `PreservedAsConflict`. L'evidenza resta nella mappa tramite l'alternativa (`:101-107`). Ramo v4–v10 invariato.
- **Test** (PostgreSQL e MAF 1.26.0 reali, modello finto; nuovo `DocumentReconcileAliasWorkflowV11Tests`):
  - profilo W=8192, O=2048, M=1024 e ledger di circa 20 evidenze (con una Gap del PM, una Gap di documento, un'Assumption, una `Hypothesized`, una Decision, un'Alternative e una coppia in conflitto). Il test asserisce che `DocumentAnalysisWorkflow` instradi in gerarchia con stadio Gap;
  - un item `openQuestions` per ogni Gap, con testo uguale all'enunciato; `GapState` non nullo e `MissingGaps` vuoto;
  - la coppia in conflitto compare solo come alternative e `EvidenceCoverage` non produce `item_repeats_conflict_alternative`;
  - ripresa in un processo nuovo a metà gerarchia: stesso `canonicalHash`, nessuna inferenza per i batch Committed;
  - i messaggi di espansione e confronto portano item senza testo;
  - **(b) rischio R-D2b** ([plan.md](plan.md) §8, registrato prima di M1), nell'assembly v11 con `GapStageReferences.From` sull'analisi risultante:
    1. Gap del PM estremo di un conflitto `Open` con un'evidenza non Gap: la domanda del Gap resta in `openQuestions`, `MissingGaps` vuoto, il conflitto resta con entrambe le alternative;
    2. Gap contro Gap: entrambe le domande restano;
    3. Gap estremo di due conflitti `Open`: la domanda resta, una sola volta;
    4. Gap estremo di un conflitto `Open` e bersaglio di una patch `Applied`: la domanda resta;
    5. evidenza non Gap, unica evidenza dell'item, estremo di un conflitto `Open`: l'item è escluso (regola D2b), l'evidenza resta mappata tramite l'alternativa, nessun `evidence_not_mapped`, nessun `item_repeats_conflict_alternative`;
    6. item `openQuestions` di tipo Alternative o certezza `ToClarify` estremo di un conflitto `Open`: comportamento dichiarato e testato (esclusione, con riferimenti `i<n>` rinumerati in modo deterministico: stessi riferimenti a ogni replay);
    7. **negativo di caratterizzazione**: con D2b applicata senza l'eccezione per i Gap, lo stesso caso 1 produce `MissingGaps` = [Gap] e `gap_reference_missing`. Il test documenta il difetto che l'eccezione evita; se l'eccezione fosse respinta, questo diventa il comportamento v11 e va dichiarato in `ai-workflow.md` (RH-043);
    8. D3: per un Gap estremo, l'avviso `item_repeats_conflict_alternative` può comparire ed è non bloccante: il test asserisce che non è bloccante e che l'asserzione «avvisi D3 = 0» di RH-041 vale solo per le evidenze non Gap.
- **Dipendenze:** RH-020. **Effort:** 1,75 gg.
- **STOP:** se il ramo atteso non si raggiunge con il profilo indicato, segnalare senza modificare le policy di instradamento.

### [ ] RH-022 — Replay per batch delle foglie A reali e precondizione dello stadio Gap
- **Test** (`ReconcileReplayFixturesTests`, con `ReplayFixtureBuilder`):
  - (a) v10:
    - la fixture Run 5 024 composta produce proposte in cui nessun item `openQuestions` cita E4; `GapStageReferences.From` su un'analisi minima costruita da quelle proposte restituisce `MissingGaps` = [E4];
    - la fixture Run 4 022 lascia E1 senza item che la citi;
  - (b) v11: le stesse **voci** proiettate danno un item per voce e una domanda per ogni Gap, quindi `MissingGaps` vuoto;
  - (c) rischio R-D2b: dalla fixture Run 5 027 (candidato E255–E4, E4 è un Gap) e Run 4 029 si ricostruiscono, con `ReplayFixtureBuilder`, il batch e il candidato; si compone la disposizione `Open` per quel candidato e si esegue l'assembly v11 con D2b: con l'eccezione per i Gap `MissingGaps` resta vuoto, senza l'eccezione è [E4] (caratterizzazione del difetto).
- **Completamento:** esiti in «Evidenze M2».
- **Dipendenze:** RH-006, RH-021. **Effort:** 0,5 gg.

### [ ] RH-023 — (solo se D2 respinta) Fail-fast nelle foglie A
- **Modifiche:**
  - compositore v11 nella foglia A: ogni voce citata da almeno un item (`ItemShape`) e ogni Gap citata da un item `openQuestions` (`ItemShape`);
  - prompt v6: regola esplicita sulle Gap nella sottosezione «Foglie».
- **Test:** le fixture Run 4 022 e Run 5 024 sono rifiutate con `ItemShape`. Il fallimento arriva prima, ma **non è recuperato**.
- **Dipendenze:** C1. **Effort:** 1 gg. Sostituisce RH-020 e RH-021; RH-022(b) si adatta.

**Checkpoint C2:**
- M2 verde;
- suite completa con 0 falliti;
- nessuna `evidence_not_mapped` né `gap_reference_missing` sui ledger di test della v11.

---

## M3 — Prestazioni a risultato identico (D6)

### [ ] RH-030 — P-1: memo dei token degli extra (tutte le definizioni)
- **File e modifiche:**
  - `AI/ContextBudget.cs`:
    - `public sealed class ExtraTokenMemo` con `ConcurrentDictionary<(string Fingerprint, string ModelId, string Sha256), int>`;
    - `DocumentPreflight.MeasureAsync(DocumentPayload payload, CancellationToken ct, string? expectedFingerprint = null, ExtraTokenMemo? memo = null)`;
    - in `CountAsync` il memo si usa **solo** per `ResponseSchemaJson` e `ToolsJson`, nei due rami che li tokenizzano (`:489-492`, `:503-508`); chiave = `profile.Fingerprint`, `profile.ModelId`, SHA-256 UTF-8 del testo; il messaggio reso non si memoizza mai.
  - `AI/DocumentReconcilePlan.cs`: campo `_extraTokens = new ExtraTokenMemo()`, passato alle chiamate `_preflight.MeasureAsync` di `:575`, `:836`, `:990`, `:1141`, `:1178`.
- **Test** (`DocumentPreflightMemoTests`):
  - 100 misure con 3 schemi distinti: `Tokens` e `PayloadHash` identici con e senza memo; `/tokenize` ridotte esattamente di (misure − 3);
  - impronta diversa ⇒ nessun riuso;
  - per v6 e v10 sul ledger di RH-004, la sequenza (`Tokens`, `PayloadHash`) di ogni misura e i `batchId` sono identici con e senza memo.
- **Dipendenze:** C0, indipendente da M1 e M2. **Effort:** 0,5 gg.
- **Decisioni risolte:** memo con la vita del piano, mai globale.
- **STOP:** se un solo `Tokens` differisce.

### [ ] RH-031 — Misura della v11 e della v10 con P-1
- **Test:** con `CountingLlamaRuntime` sul ledger di RH-004, contare le richieste fino al primo batch di v10 (senza e con P-1) e di v11 (con P-1), sia nel primo avvio sia a processo nuovo.
- **Criterio:**
  - v11 con P-1 ≤ v10 senza P-1;
  - riduzione dei `/tokenize` pari al numero delle misure con schema meno gli schemi distinti;
  - tabella in «Evidenze M3».
- **Dipendenze:** RH-030, RH-015. **Effort:** 0,25 gg.

**Checkpoint C3:** M3 verde; suite completa con 0 falliti.

---

## M4 — Verifica integrata offline, contratti e congelamento

### [ ] RH-040 — Fixture rappresentativa ERP/EDI (sintetica, scritta da zero)
- **File:** `backend/tests/bProjects.IntegrationTests/Reconciliation/Representative/`:
  - `af-integrazione-edi.md`: analisi funzionale fra ERP «Gestionale Alfa» e hub EDI «Hub Beta», con
    - flussi ORDERS, DESADV e INVOIC;
    - circa 25 requisiti funzionali e operativi;
    - 3 tracciati in tabelle Markdown da circa 10 campi;
    - 2 tabelle di trascodifica;
    - gestione errori e riprocessamento;
    - 3 decisioni aperte;
    - vincoli di integrazione;
  - `verbale-riunione-2.md`: due regole cambiate (timeout di riprocessamento 2 h → 4 h; «codice destinazione» da facoltativo a obbligatorio);
  - `pm-messages.json`: 3 dichiarazioni, di cui 2 Gap («Chi gestisce gli scarti della DESADV?», «La fattura va inviata entro 24 o 48 ore?»);
  - `gold.json`: fatti attesi (≥ 20, frase esatta), 2 conflitti di versione attesi, Gap con esito atteso, sezione e classificazione attese per tipo e certezza. Annotato **prima** di ogni esecuzione;
  - `PROVENANCE.json`: autore, data, SHA-256, «sintetico, nessun documento cliente»;
  - `RepresentativeLedger.cs`: ledger controllato dal gold, con enunciato, citazione letterale e offset reali, tipo, origine e certezza. Fra 160 e 190 evidenze, così che con W=32768 la gerarchia sia instradata **e** lo stadio Gap entri nel budget. La densità reale (evidenze per pagina) è registrata e serve a verificare l'ipotesi dell'assessment §8.
- **Test** (`RepresentativeFixtureTests`): hash uguali a `PROVENANCE`; ogni citazione si trova letteralmente nel file; dimensione nell'intervallo.
- **Dipendenze:** nessuna. **Effort:** 1,5 gg.
- **STOP:** mai copiare o parafrasare documenti cliente.

### [ ] RH-041 — Workflow v11 completo con modello finto (prova strutturale)
- **File:** nuovo `DocumentReconcileV11FlowTests.cs`.
- **Scenari** (il test asserisce il ramo di instradamento scelto da `DocumentAnalysisWorkflow`):
  - A: ledger rappresentativo, W=32768, O=16384, M=1024, ramo «gerarchia + stadio Gap»; **almeno una coppia in conflitto ha come estremo una Gap** (rischio R-D2b, RH-021(b)): il modello finto la dichiara `Open` e il test asserisce `MissingGaps` vuoto e domanda canonica presente;
  - B: ledger T040-single di `RetrievalIsolatedFixture` **senza** le evidenze Gap, ramo «gerarchia senza Gap»;
  - C (inflazione): scenario A con un modello finto che emette 30 candidati per foglia.
- **Modello finto:** foglie, riduzione, cross ed espansione (≤ 2 figli per blocco) secondo uno script; confronti `Open` per le coppie in conflitto dello script, `Compared` altrimenti; stadio Gap con output valido.
- **Asserzioni:**
  - A e B `Completed`;
  - ogni evidenza produce esattamente un item proiettato oppure compare solo come alternativa (D2b);
  - ogni Gap ha la sua domanda canonica; avvisi D3 = 0;
  - nessun WorkOutput duplicato;
  - ripresa da zero in un processo nuovo senza inferenza, con la stessa revisione;
  - C termina `Completed` oppure con un limite tipizzato (`coverage_incomplete` o `call_limit_exceeded`), mai con un successo parziale; le chiamate restano ≤ 512.
- **Nota:** è una prova **strutturale**. La conservazione semantica dei conflitti si misura solo in M5.
- **Dipendenze:** C3, RH-040. **Effort:** 2 gg.
- **STOP:** se un ramo atteso non si raggiunge con la configurazione indicata, segnalare senza cambiare budget né policy.

### [ ] RH-042 — Fault injection
- **Test** (estensione di RH-041):
  - crash dopo il commit di una foglia A, a metà espansione, prima del commit di un confronto;
  - takeover con nuovo lease;
  - replay senza inferenza;
  - deadline a metà gerarchia;
  - limite di chiamate esaurito ⇒ fail-closed;
  - nessun effetto duplicato (WorkOutput = batch).
- **Dipendenze:** RH-041. **Effort:** 1 gg.

### [ ] RH-043 — Emendamento dei contratti della 002
- **File:** `specs/002-project-documents/contracts/ai-workflow.md`, nuovo paragrafo «Emendamento v11 (`project-analysis-documents-v11`, `reconcile-stage-contract-1`)» in §4.1, §4.3, §11 e §13.7. Elenca:
  - item delle foglie A composti dal server (tabella con la certezza);
  - estremi dei conflitti `Open` solo come alternative;
  - `noFindingsReason` informativo;
  - confronto senza candidati, con `Open` ⇒ motivo;
  - validazione v2; formato `items-ref-1`;
  - precondizione §13.7 garantita per costruzione.
  
  Il testo per v2–v10 non cambia.
- **Completamento:** coerenza verificata fra emendamento, codice e golden.
- **Dipendenze:** C3. **Effort:** 0,5 gg.

### [ ] RH-044 — Congelamento della v11
- **Modifiche:** pinnare i golden di `configHash` v11 (con il rig di `RetrievalGapFlowTests`), degli hash del profilo `by-stage-1`, dello SHA del prompt v6, della tabella `leaf-projection-1` (JSON canonico) e del JSON canonico di `stageContract`.
- **Test:** suite completa con PostgreSQL e parser reale: 0 falliti; ignorati invariati salvo i nuovi test reali opt-in.
- **Completamento:** risultati in «Evidenze M4».
- **Dipendenze:** RH-041…RH-043. **Effort:** 0,5 gg.
- **STOP:** dopo il congelamento ogni cambiamento della v11 richiede una definizione nuova.

**Checkpoint C4:** criteri 1–7 del piano §9 soddisfatti.

---

## M5 — Verifiche reali (da autorizzare separatamente, D8)

### [ ] RH-050 — Sonda della grammatica v11 e batch singoli reali (al massimo 14 richieste)
- **Contenuto:**
  - sonda (≤ 4 richieste, controllo più variante): `anyOf` con `enum` del confronto (un prompt che induce `Open` senza motivo); `candidates.maxItems = 0` del confronto;
  - batch singoli (≤ 10), ricostruiti con `ReplayFixtureBuilder` e inviati con la v11:
    - confronto tipo 030 del Run 5;
    - foglie B tipo 026/027 del Run 5: si registra il numero di candidati rispetto ai 16+16 della v5 (criterio dell'opzione O2);
    - una foglia A tipo 022 del Run 5, che deve produrre `items: []`;
    - due blocchi di espansione con membership;
  - esame del PM degli item proiettati della fixture ERP/EDI (foglio di revisione con sezione, classificazione e testo).
- **Pattern:** `ReconcileSchemaV9SupportRealTests` (`RequestCapHandler`, opt-in `BPROJECTS_TEST_DGX_ALLOW=1`, nessun retry).
- **Registrazione:** violazioni residue osservate, criterio dell'opzione O1.
- **Dipendenze:** C4, autorizzazione D8. **Effort:** 1 gg di preparazione.

### [ ] RH-051 — Prova integrata v11 sulla fixture ERP/EDI
- **Contenuto:** una sola esecuzione reale (parser, estrazione, gerarchia v11, stadio Gap), con W=32768 per forzare la gerarchia.
- **Criteri dal gold:**
  - ≥ 90% dei fatti gold presenti come item o alternative;
  - conflitti gold conservati come `Open`;
  - esiti dei Gap;
  - 0 citazioni inventate;
  - chiamate e tempi riportati.
- **Esiti:** quelli non favorevoli sono conservati; nessun rilancio automatico.
- **Dipendenze:** RH-050, autorizzazione D8. **Effort:** 0,5 gg di preparazione.

---

## M6 — Attivazione (D1)

### [ ] RH-060 — v11 come default delle nuove analisi senza recupero
- **File:** `Documents/SourceSnapshotStore.DocumentaryAnalysisFreeze`: con `useRetrieval == false` restituisce `(AnalysisDefinitionV11, AnalysisPromptVersionsV11())`; il ramo con recupero resta v10.
- **Test:**
  - aggiornare **deliberatamente** i test che fissano la v6 come default delle nuove richieste senza recupero (individuarli cercando `AnalysisDefinitionV6` e `DocumentaryAnalysisFreeze` nei test), motivandolo nel commit;
  - i run v6 congelati restano v6.
- **Documenti:** nota in `specs/002-project-documents/validation/operations.md`.
- **Dipendenze:** RH-044, RH-050 e RH-051 superati, approvazione D1 registrata. **Effort:** 0,25 gg.
- **STOP:** senza i due esiti reali positivi o senza D1 registrata. Niente merge, niente push.

**Checkpoint finale:**
- criteri 1–9 del piano §9;
- nessuna voce T040, T027 o T150 dichiarata superata;
- task riportati nel `tasks.md` ufficiale (D11).

---

## Riepilogo

| Milestone | Task | Effort (gg) |
|---|---|---|
| M0 Baseline offline | RH-001…RH-006 | 5,75 |
| M1 Definizione v11 e contratto per stadio | RH-010…RH-017 | 6,5 |
| M2 Item composti dal server | RH-020…RH-022 (alternativa RH-023) | 3,0 |
| M3 Prestazioni a risultato identico | RH-030, RH-031 | 0,75 |
| M4 Verifica integrata offline, contratti, congelamento | RH-040…RH-044 | 5,5 |
| **Totale offline (M0–M4)** | **24 task** (+1 alternativo) | **≈ 21,5 gg** (intervallo 18–25) |
| M5 Verifiche reali (D8) | RH-050, RH-051 | 1,5 di preparazione |
| M6 Attivazione (D1) | RH-060 | 0,25 |
| **Totale** | **27 task** | **≈ 23,25 gg** |

Opzioni differite O1–O5: nessun task finché il criterio d'ingresso di [plan.md](plan.md) §12 non è soddisfatto.
