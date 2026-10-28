# Quickstart di validazione — consolidamento finale

**Updated**: 2026-10-28.
Guida per l'implementazione successiva: solution, frontend e Compose non sono ancora
implementati. I comandi indicano il percorso da rendere disponibile, non prove eseguite.
Controlli documentali e verifiche runtime sono distinti nella checklist canonica.

## Prerequisiti e configurazione

.NET 10, Node/TypeScript compatibili con Angular 22, Docker Compose v2 e PostgreSQL 18
persistente. Due PM locali predisposti, origine HTTPS same-origin per API/UI,
endpoint Chat Completions DGX interno con model ID e capacità reali note.
Nessun ricorso automatico a credenziali scoperte nell'ambiente.

Configurazione prevista: ConnectionStrings__AppDb; Ai__Endpoint/ModelId/ApiKey;
Ai__ContextWindowTokens/MaxOutputTokens/SafetyMarginTokens/CompactionTriggerTokens;
Ai__StructuredOutputMode=JsonSchema oppure ValidatedTextJson.
Execution__LeaseDurationSeconds=30, RenewIntervalSeconds=10, MaxAttempts=3,
RetryBaseDelaySeconds=2, RetryMaxDelaySeconds=10, RunDeadlineSeconds=180.
I valori risolti sono fissati nella richiesta; segreti fuori da repo/prompt/log.
DB/configurazioni/volumi di test distinti da produzione; non eliminare i volumi
durante le prove di riavvio.

## Percorso di avvio previsto

Dalla root, soltanto dopo l'implementazione:

```powershell
dotnet restore backend/bProjects.sln --locked-mode
dotnet build backend/bProjects.sln --no-restore
npm --prefix frontend ci
npm --prefix frontend run build
docker compose -f infra/compose.yaml up -d --build
```

Migrazioni e bootstrap account devono essere operazioni esplicite e non distruttive.
Mantenere MAF core/OpenAI/Workflows 1.26.0 nel lock effettivo. La compilazione iniziale
deve includere primitive concrete, binding .NET e store JSON; niente framework alternativo.

Comandi di test previsti, non eseguiti qui; categorie/script da fornire nell'implementazione:

```powershell
dotnet test backend/bProjects.sln --no-build --filter Category=Integration
dotnet test backend/bProjects.sln --no-build --filter Category=MafReal
npm --prefix frontend run test:ci
npm --prefix frontend run test:e2e
```

Integration usa PostgreSQL reale per commit, concorrenza e serializzazione.
MafReal usa anche endpoint DGX e grafi MAF effettivi; mock non sostituiscono le prove
critiche. Registrare versioni, ID, conteggi, token/durate ed esiti, non contenuti integrali.

## Prove nominali separate — successo richiesto

Preparare cinque casi: note frammentarie, negazioni/esclusioni, informazioni mancanti,
conflitti e due progetti distinti. Prima delle prove annotare obiettivi, requisiti,
vincoli, flussi ed evidenze attesi; non basarsi sullo stile dell'output.
Servizio disponibile, nessun guasto indotto e nessun'altra coda per le soglie nominali.

1. **Chat riuscita**: cinque scambi con risposte reali, ciascuno entro 180 secondi;
   chiarimenti pertinenti, al massimo uno per turno e nessuna domanda ripetuta dopo
   risposta. Originali e assistant distinguibili. Nessun ProcessingRun formale.
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

## Esito da raccogliere

Per ogni caso: risultato atteso/osservato, versioni, ID di run/revisione/bozza,
durata/attempt, conteggi originali, stato e valutazione qualitativa.
I test di guasto dimostrano recovery, non completamento della funzionalità.
Nessun test di KnowledgeAgent/KB/Docling/RAG/web è richiesto ora; nessun tool futuro
deve essere registrato. Non utilizzare il PASS documentale come prova runtime.

Riferimenti: [spec](spec.md), [data model](data-model.md),
[API](contracts/http-api.md), [contratto AI](contracts/ai-workflow.md),
[checklist canonica](checklists/requirements.md).
