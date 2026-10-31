# Quickstart di validazione — consolidamento finale

**Updated**: 2026-10-28.
Solution, frontend e Compose sono implementati. Le verifiche tecniche non dipendenti dal
DGX sono state eseguite il 2026-10-28 (sezione "Esito verifica tecnica T053"); le prove con
DGX reale (MafReal ed E2E reale) e la revisione umana restano da eseguire. Controlli
documentali e verifiche runtime sono distinti nella checklist canonica.

## Prerequisiti e configurazione

.NET 10, Node/TypeScript compatibili con Angular 22, Docker Compose v2 e PostgreSQL 18
persistente. Due PM locali predisposti, origine HTTPS same-origin per API/UI,
endpoint Chat Completions DGX interno con model ID e capacità reali note.
Nessun ricorso automatico a credenziali scoperte nell'ambiente.

Configurazione prevista: ConnectionStrings__AppDb; Ai__Endpoint/ModelId/ApiKey;
Ai__ContextWindowTokens/MaxOutputTokens/SafetyMarginTokens/CompactionTriggerTokens;
Ai__StructuredOutputMode=JsonSchema oppure ValidatedTextJson;
Ai__Phases__{Chat,Analysis,Document,Compaction}__MaxOutputTokens/EnableThinking
(vuoto = eredita il globale; in compose Analysis vale 8192 di default perché
l'analisi con UUID validi supera i 4096 token, vedi plan.md, esito invalid_agent_output).
Execution__LeaseDurationSeconds=30, RenewIntervalSeconds=10, MaxAttempts=3,
RetryBaseDelaySeconds=2, RetryMaxDelaySeconds=10, RunDeadlineSeconds=180.
I valori risolti sono fissati nella richiesta; segreti fuori da repo/prompt/log.
DB/configurazioni/volumi di test distinti da produzione; non eliminare i volumi
durante le prove di riavvio.

## Percorso di avvio

Node >= 22.22.3 oppure >= 24.15 (Angular 22); verificato con Node 24.22.1/npm 11.19.1 e
.NET SDK 10.0.401. Con nvm-windows, Node 24.22.1 e' stato messo nel PATH della shell
(cartella `nvm\v24.22.1`) senza `nvm use`. Dalla root:

```powershell
dotnet restore backend/bProjects.sln --locked-mode
dotnet build backend/bProjects.sln --no-restore
npm --prefix frontend ci
npm --prefix frontend run build
docker compose -f infra/compose.yaml up -d --build
```

Compose legge `infra/.env` (non versionato; modello in `infra/.env.example`) e richiede
POSTGRES_PASSWORD, AUTH_SIGNING_KEY, AI_ENDPOINT, AI_MODEL_ID, AI_API_KEY. L'avvio non
richiede il DGX raggiungibile: per prove senza DGX usare un env-file di prova con valori
finti (`--env-file <file>`, endpoint su porta chiusa, WEB_PORT diverso) e `-p <progetto>`
per un volume dedicato. Poi, esplicitamente:

```powershell
docker compose -f infra/compose.yaml run --rm app migrate
docker compose -f infra/compose.yaml run --rm -e Bootstrap__Accounts__0__UserName=<pm> -e Bootstrap__Accounts__0__Password=<password-12+> app bootstrap-accounts
```

Migrazioni e bootstrap account devono essere operazioni esplicite e non distruttive.
Mantenere MAF core/OpenAI/Workflows 1.26.0 nel lock effettivo. La compilazione iniziale
deve includere primitive concrete, binding .NET e store JSON; niente framework alternativo.

Comandi di test (Microsoft.Testing.Platform: `--solution` e `--filter-trait`, non `--filter`):

```powershell
dotnet test --solution backend/bProjects.sln --no-build
dotnet test --solution backend/bProjects.sln --no-build --filter-trait "Category=Integration"   # BPROJECTS_TEST_POSTGRES
dotnet test --solution backend/bProjects.sln --no-build --filter-trait "Category=MafReal"       # BPROJECTS_TEST_DGX_ENDPOINT
npm --prefix frontend run test:ci
npm --prefix frontend run test:e2e
```

Variabili di test: `BPROJECTS_TEST_POSTGRES` (connection string di un PostgreSQL 18 di
prova, es. `Host=localhost;Port=55432;Username=postgres;Password=<test>`); senza, i test
Integration risultano ignorati, mai superati. MafReal richiede `BPROJECTS_TEST_DGX_ENDPOINT`
(piu' modello/chiave); senza, risulta ignorato. E2E: `E2E_BASE_URL` (stack avviato),
`E2E_USER`/`E2E_PASSWORD` (account da bootstrap), `E2E_MODEL_MODE=offline|real`,
`E2E_DB_COMMAND` (psql -At sul DB dello stack di prova, SQL da stdin) e
`E2E_RESTART_COMMAND` (`down` senza `-v`, poi `up -d --wait`). Il comando di riavvio gira
con cwd `tests/e2e` e, su Windows, in cmd: usare percorsi assoluti con lettera di unita'
(`C:/...`) per `-f` ed `--env-file`.

Metriche di inferenza e monitor del contesto (UI): `context-monitor.spec.ts` usa lo stack finto (nessun DGX);
`context-monitor-real.spec.ts` richiede `E2E_MODEL_MODE=real`, uno stack con `AI_COMPACTION_TRIGGER_TOKENS` basso (es. 3000)
e, facoltative, `E2E_APP_RESTART_COMMAND` (`restart app`), `E2E_DGX_ENDPOINT`/`E2E_DGX_API_KEY`/`E2E_DGX_MODEL` (chiamata diretta di
confronto) ed `E2E_API_KEY_NEEDLE` (chiave da non trovare in DOM e risposte; mai stampata). Gli screenshot vanno in
`tests/e2e/test-results/screens` (`E2E_SCREENSHOT_DIR`).

Integration usa PostgreSQL reale per commit, concorrenza e serializzazione.
MafReal usa anche endpoint DGX e grafi MAF effettivi; mock non sostituiscono le prove
critiche. Registrare versioni, ID, conteggi, token/durate ed esiti, non contenuti integrali.

## Esito verifica tecnica T053 (2026-10-28, senza DGX)

Eseguita sul branch `feature/001-project-draft`, working tree pulito. Il DGX risponde 401:
nessuna prova con modello reale; `infra/.env` non e' stato usato (env-file di prova finto).
Non ripetere, salvo modifiche al codice.

| Verifica | Esito |
| --- | --- |
| `dotnet restore --locked-mode` e `dotnet build` | PASS, 0 errori, 0 avvisi |
| Suite backend senza PostgreSQL | PASS 423, 0 falliti, 444 ignorati (PostgreSQL non impostato) |
| Integration su PostgreSQL 18 (container `bprojects-test-pg18`) | PASS 477/477, 0 ignorati (circa 2m42s) |
| Intera suite con PostgreSQL, senza DGX | PASS 840, 0 falliti, 27 ignorati (tutti MafReal) |
| MafReal | SKIP 27 su 28: `BPROJECTS_TEST_DGX_ENDPOINT` non impostata. Non e' un PASS (il 28esimo non usa il DGX ed e' passato) |
| `npm ci`, `npm run build` | PASS; avviso: budget del bundle iniziale superato (607,35 kB contro 600 kB), non bloccante |
| `npm run test:ci` (Vitest 5.0.5) | PASS 83/83 in 9 file |
| `npm run test:e2e` (Playwright 1.60.0, desktop-1440 e mobile-390, stack Docker, `E2E_MODEL_MODE=offline`) | 120 PASS, 0 falliti, 18 SKIP: 7 percorsi con modello reale per ciascun viewport (14) e 4 prove limitate al solo desktop (chat offline, Analizza offline, due riavvii) sul mobile |
| Docker Compose `up -d --build --wait`, `migrate`, `bootstrap-accounts` | PASS: db, app e web healthy, UI 200, API non autenticata 401 |
| Riavvio `down` senza `-v` e `up --wait` (E2E) | PASS: progetti, sessione (refresh), revisioni 1-4, conferma, conflitti risolti e date sopravvivono |
| `kill app` e `up -d app` (volume intatto) | PASS: conteggi invariati (44 progetti, 10 messaggi, 10 ChatTurn, 6 run, 16 revisioni, 109 RefreshSessions, 1 account), `/health` 200, login 200 |

Limiti: la tabella WorkflowCheckpoints era vuota nello stack senza modello (nessun run MAF
reale), quindi resume da checkpoint dopo il riavvio e compattazione MAF reale (T047) non
sono verificati in Docker; restano coperti solo dai test Integration su PostgreSQL.
Nella prima esecuzione E2E 2 prove di riavvio sono fallite solo per il comando di riavvio
di prova (percorso `../infra` errato per la cwd, percorso MSYS in cmd), corretto nella
variabile e non nel prodotto; nelle esecuzioni successive 0 fallimenti, nessuna
instabilita' osservata. Stack di prova fermato; volume `bprojects-t053_pgdata` conservato.

## Prove nominali separate — successo richiesto

Preparare cinque casi: note frammentarie, negazioni/esclusioni, informazioni mancanti,
conflitti e due progetti distinti. Prima delle prove annotare obiettivi, requisiti,
vincoli, flussi ed evidenze attesi; non basarsi sullo stile dell'output.
Servizio disponibile, nessun guasto indotto e nessun'altra coda per le soglie nominali.

1. **Chat riuscita**: cinque scambi con risposte reali, ciascuno entro 180 secondi;
   chiarimenti pertinenti, al massimo uno per turno e nessuna domanda ripetuta, anche se la
   precedente non ha ricevuto risposta. Originali e assistant distinguibili. Nessun ProcessingRun formale.
2. **Analisi riuscita**: Analizza crea solo Kind=Analysis. Entro 180 secondi restituisce
   revisione AnalysisContent 1.0.0 valida, Unvalidated, e anteprima leggibile. Nessun
   Markdown o chiamata DocumentAgent. Tutti i casi nominali devono riuscire.
3. **Correzione/validazione**: modificare requisito, integrare informazione e assunzione;
   mantenere un conflitto Open e risolverne un altro con motivo. Nuova revisione,
   fonti PmRevision/utente/data distinguibili, risultato AI e messaggi originali intatti.
   Confermare esplicitamente la revisione corrente; PmConfirmed senza approvazione cliente.
4. **Generazione riuscita**: Genera documento su quella revisione produce Markdown
   entro 180 secondi dal proprio comando, legato a InputRevisionId e Proposed/NotApproved.
   Ogni requisito/vincolo applicabile atteso è presente e correttamente sostenuto.
5. **Comprensibilità**: un tecnico non presente al dialogo ricostruisce da solo
   obiettivi/perimetro, requisiti e flussi applicabili, dipendenze e questioni aperte.
   Usare le attese del caso: zero omissioni sostanziali, contraddizioni introdotte,
   ripetizioni sostanziali o informazioni inventate. Entrambi i PM confermano l'utilità.
   Sezioni non pertinenti omesse; niente riempitivi o misure basate sulle parole.
6. **Riapertura**: tre sessioni e restart dell'app conservano dialogo, anteprima,
   conferme e bozza. Uno stato pendente può avanzare; gli originali no.
   Il PM può attendere e riprendere l'anteprima anche oltre la deadline dei run AI.

Un errore/timeout nei casi nominali è una prova non superata, anche se ben presentato.
L'attesa umana non deve restare dentro un run o consumare il tempo di generazione.
Percorso attivo del caso preparato <= cinque minuti per PM, esclusa l'attesa AI.

## Guardie, revisioni e concorrenza

- Generazione prima della conferma: 409 analysis_not_confirmed e zero invocazioni DocumentAgent.
- If-Match obsoleto in edit/conferma: 409 revision_conflict, nessuna sovrascrittura.
- Conferma non genera documenti. Conferma ripetuta sulla stessa revisione corrente è idempotente.
- Edit dopo conferma: nuova Unvalidated; vecchio JSON confermato invariato.
- Generazione già partita, poi edit: il run termina sul vecchio InputRevisionId,
  la bozza indica tale versione; ulteriore generazione impedita senza nuova conferma.
- Idempotency-Key ripetuta: stesso run/risultato; nuova chiave durante run formale
  attivo: 409 processing_active, senza seconda elaborazione formale.
- Una nuova Analysis fallita non nasconde l'ultima anteprima riuscita.
- Punti aperti confermati rimangono aperti nel Markdown, non diventano fatti.
- Nuovi PM oltre cutoff: hasNewNotes; risposta assistant da sola non attiva tale indicatore.

## FIFO e cutoff rigoroso — F-01

Preparare U1 e U2 prima che sia pubblicata A1: U1(seq=1), U2(seq=2), A1(seq=3).
T2 deve avere cutoff=2; la sessione post-T1 ha watermark=3 e viene ricostruita da
U1/U2, senza A1 o sintesi di A1. Registrare gli ID ammessi e realmente inviati tramite
harness riservato senza log di contenuti, non assumere che l'ordine delle risposte
da solo provi l'assenza di dati oltre cutoff.
Provare anche una nuova vista con inserimenti intermedi rispetto ai riferimenti
memorizzati: la sessione non compatibile viene ricostruita, senza riordinare originali.
Nessun turno successivo supera un precedente in backoff; un solo lease valido
per conversazione. Niente copie integrali dello storico per ogni turno.

## Crash, recovery e retry — F-02

Punti di interruzione controllati dal solo harness; nessun endpoint di prodotto per
pilotare executor. Il nuovo processo deve leggere PostgreSQL, non ripristinare solo
lo stesso oggetto in memoria.

```powershell
docker compose -f infra/compose.yaml kill app
docker compose -f infra/compose.yaml up -d app
```

| Punto | Attesa documentata |
| --- | --- |
| Dopo commit PM, prima dell'inferenza | Originale e ChatTurn persistono anche con modello offline |
| ChatTurn Running abbandonato | Dopo lease scaduto, reclaim automatico con fence/attempt nuovo |
| Errore transitorio osservato | Queued con backoff, retry entro tentativi/deadline senza sorpasso FIFO |
| Tentativi esauriti o errore permanente | Failed terminale, nessuna nuova inferenza implicita |
| Prima del commit risposta | Ripetizione possibile, una sola risposta pubblicata |
| Dopo commit risposta/sessione | Medesimo ResultMessageId, nessuna rigenerazione |
| Prima del primo checkpoint formale | Stesso input/run/limiti, niente duplicato |
| Dopo checkpoint di una fase MAF | Resume nativo in nuovo processo, stessa definizione/identità/input |
| Dopo commit revisione AI o bozza, prima checkpoint terminale | Completed riconciliato e risultato unico, non Failed |
| Durante l'attesa della verifica PM | Nessun run Running richiesto; anteprima/conferma recuperabili |
| Vecchio worker termina dopo reclaim | Nessun commit autorizzato dal fencing obsoleto |
| Versione checkpoint incompatibile o deadline superata | Failed esplicito, risultati precedenti conservati |

Verificare lease 30 s rinnovato ogni 10 s, max 3 attempt, backoff 2/4 s con default,
contatori persistenti e retry SDK non moltiplicati. Un'inferenza remota interrotta
può ripetersi; non si pretende ripresa dal token o exactly-once delle chiamate.
Per ChatTurn singolo non dichiarare checkpoint workflow assenti: riparte da sessione
committata compatibile. Per ProcessingRun usare checkpoint MAF validi quando presenti.

## Errori dedicati — non equivalgono a successo nominale

- Errore modello in Analysis: Failed, nessuna falsa anteprima; vecchi dati intatti.
- Errore modello in Document: revisione confermata e ultima bozza riuscita intatte;
  nuovo comando PM possibile con chiave nuova, senza mutare lo snapshot del tentativo.
- JSON/schema/fonti inventati: invalid_agent_output, nessun salvataggio di anteprima
  visibile. Provare stesso insieme di casi invalidi in JsonSchema e ValidatedTextJson.
- Errore della sintesi, contesto non riducibile o JSON confermato fuori budget:
  errore esplicito, niente omissioni o documento parziale presentato come completo.
- Errore di salvataggio PM: il browser conserva il testo e non dichiara il commit.
- 408/429/5xx e guasti permanenti hanno transizioni differenti; nessun retry infinito.
- Accesso non autorizzato, prompt injection e output pericoloso non cambiano scope,
  non contattano LLM esterni e non producono esecuzioni/pubblicazioni.

## Compattazione effettiva e conversazioni lunghe — F-06

Misurare capacità/finestra/tokenizer reali del modello, non volume in parole.
Per chat e Analysis configurare CompactionTriggerTokens più basso del contesto di
prova ma entro InputBudget, senza dichiarare una finestra modello inesistente.
Attivare realmente SummarizationCompactionStrategy MAF e registrare:
attivazione, token prima/dopo e conteggio finale comprensivo di istruzioni/payload
entro ContextWindowTokens - MaxOutputTokens - SafetyMarginTokens.
Confrontare originali/ID/negazioni e riferimenti con le attese dopo compattazione.

DocumentAgent ha solo JSON confermato protetto e istruzioni, senza history da ridurre:
nessun provider deve riassumerlo. Prova positiva entro budget e prova negativa oltre
budget, con zero omissioni e snapshot invariato. Non creare un summarizer custom.
I guasti di compattazione sono prove di errore distinte, non scorciatoie per SC-010.

## Schema e provenienza — F-03

Usare lo schema normativo e le regole del [contratto AI](contracts/ai-workflow.md).
Casi: campi mancanti/null non ammessi, ID duplicati, refs inesistenti/fuori progetto,
assistant come unica fonte fattuale, PmConfirmed inventato dall'AI, conflitto risolto
dall'AI, revisione PM attribuita erroneamente, alternative o risoluzioni incoerenti.
PUT/confirm devono validare nuovamente; revisione AI iniziale e originali rimangono intatti.

## Registro atomico delle evidenze — validazione (estensione 2026-10-31)

Prerequisiti come sopra; variabili opzionali della fase di estrazione in `infra/.env` (`AI_EXTRACTION_MODEL_ID`,
`AI_EXTRACTION_ENABLE_THINKING`, `AI_EXTRACTION_MAX_OUTPUT_TOKENS`, `AI_EXTRACTION_REQUEST_TIMEOUT_SECONDS`,
`EXECUTION_EXTRACTION_DEADLINE_SECONDS`); senza override valgono i default documentati in `infra/compose.yaml`.

1. **Durabilità**: con il modello offline inviare un messaggio PM: risposta 201 immediata; `GET /api/projects/{p}/evidence/status`
   mostra il messaggio `pending` (lavoro Queued). Riavviare `app`: il lavoro resta; a modello disponibile passa a `extracted` o `empty`.
2. **Estrazione**: nella vista Evidenze ogni evidenza mostra tipo, polarità, certezza e la frase originale con lo span evidenziato;
   un messaggio di sola cortesia risulta «zero evidenze» con motivo, distinto da un lavoro `failed` (che mostra errore e Riprova).
3. **Analizza**: con un'estrazione ancora in coda il run resta Queued finché le estrazioni entro il cutoff non terminano; con
   un'estrazione fallita il run termina Failed `evidence_unavailable`. `GET .../analyses/{a}/evidence-selection` riporta versioni,
   vista e mappa elementi→evidenze del run.
4. **Rianalisi e confronto**: «Rianalizza» su un messaggio crea una versione candidata; il confronto mostra aggiunte, rimosse,
   modificate e ambigue; la promozione richiede la revisione attesa (una seconda promozione concorrente risponde 409). Analisi,
   revisioni e bozze già prodotte restano invariate.
5. **Backfill**: in un progetto con messaggi antecedenti al registro, «Backfill» accoda solo i messaggi senza versione; ripeterlo con
   la stessa chiave restituisce lo stesso batch; interrompere e riavviare non crea duplicati.
6. **Copertura**: «Verifica copertura» su analisi, revisione confermata e bozza produce un report con errori deterministici, rischi
   stimati e valutazioni PM separati; i rischi stimati sono euristiche, non giudizi certi.

Test: `dotnet test --solution bProjects.sln --filter-class "*Evidence*"` (deterministici e, con `BPROJECTS_TEST_POSTGRES`, su
PostgreSQL); `--filter-trait "Category=MafReal"` per l'estrattore reale e i nominali N1–N5. Esiti registrati in T070–T073 e in `HANDOFF.md`.

## Esito da raccogliere

Per ogni caso: risultato atteso/osservato, versioni, ID di run/revisione/bozza,
durata/attempt, conteggi originali, stato e valutazione qualitativa.
I test di guasto dimostrano recovery, non completamento della funzionalità.
Nessun test di KnowledgeAgent/KB/Docling/RAG/web è richiesto ora; nessun tool futuro
deve essere registrato. Non utilizzare il PASS documentale come prova runtime.

Riferimenti: [spec](spec.md), [data model](data-model.md),
[API](contracts/http-api.md), [contratto AI](contracts/ai-workflow.md),
[checklist canonica](checklists/requirements.md).

## Esito verifica tecnica T053 PRELIMINARE (2026-10-31, Qwen3.6-35B-Smart, DGX reale)

Commit di partenza `ca945c6` + modifiche di sessione 8 (solo test/documentazione). **Preliminare: T053 resta aperto** perche' T052 non e' superato.

| Verifica | Esito |
| --- | --- |
| `dotnet restore` e `dotnet build` | PASS, 0 errori, 0 avvisi |
| Suite backend senza MafReal (deterministica + Integration su PostgreSQL 18) | PASS 1002, 0 falliti, 1 ignorato (`V7_replay_dump`, richiede traccia V7) |
| MafReal su DGX (32 test, un solo run, in parallelo su `parallel=1`) | **25 PASS, 7 FAIL, 0 SKIP**; `LocalProviderTests` corretto e ripassato 8/8 (attesa finestra obsoleta); restano: N2, N3, N5 nominali, esplorativo grande, T047 chat ridotta, T047 analisi 10 messaggi grandi (vedi HANDOFF 8.2/8.3) |
| `npm run test:ci` (Vitest, Node 24.22.1) | PASS 168/168 in 13 file |
| `npm run test:e2e` (Playwright, desktop-1440 + mobile-390, stack Docker isolato, `E2E_MODEL_MODE=offline`) | 177 PASS, 0 falliti, 33 SKIP (percorsi con modello reale e prove solo desktop) |
| Docker `up --build --wait`, `migrate`, bootstrap account, riavvio `down`/`up` nei test E2E | PASS (le prove di riavvio desktop sono incluse nei 177) |
| Persistenza/SSE/idempotenza | coperte dalle suite Integration ed E2E sopra; nessuna prova di crash con modello reale |
| Nominali reali N1–N5 | **FAIL** (prima esecuzione 0/5; rerun: N1 e Boreale PASS, Aurora FAIL) |
| Compattazione reale, fasi di produzione | 1 PASS + 1 FAIL (perdita SMS nell'analisi finale) |

Gli SKIP non sono PASS. Nessuna attestazione di SC-001–SC-012: mancano le valutazioni umane.
