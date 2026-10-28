# Ricerca tecnica — consolidamento F-01–F-07

**Updated**: 2026-10-28. Baseline: [spec](spec.md), [Constitution](../../.specify/memory/constitution.md)
e decisioni approvate. Revisione documentale su fonti ufficiali, non prova compilativa,
runtime DGX, crash/restart o qualità già superata. Nessuna API Python è assunta C#.

## 1. Versioni e primitive .NET effettivamente documentate

**Decisione**: Microsoft.Agents.AI, Microsoft.Agents.AI.OpenAI e
Microsoft.Agents.AI.Workflows esattamente 1.26.0; .NET 10 LTS.
NuGet/release riportano il tag dotnet-1.26.0, commit 9c8a351 del 2026-10-23.
Non tutti gli aspetti del pacchetto stabile sono API stabili: CompactionProvider è
marcato Experimental/MAAI001 nel sorgente pinned consultato.

| Capacità | Evidenza .NET e limite |
| --- | --- |
| Agente/sessione | AIAgent: CreateSessionAsync, SerializeSessionAsync e DeserializeSessionAsync; JsonElement per la rappresentazione, confermati nel tag |
| History | ChatHistoryProvider e ProviderSessionState<T> documentati nelle guide C#; adapter PostgreSQL da realizzare, non archivio nativo automaticamente durevole |
| Compattazione | CompactionProvider nel namespace Microsoft.Agents.AI.Compaction, costruttore con CompactionStrategy e StateKeys; sorgente pinned conferma stato sperimentale |
| Grafi/agent binding | WorkflowBuilder, executor e binding AIAgentHostExecutor documentati in C#; protocollo ChatMessage/TurnToken specifico .NET |
| Checkpoint | CheckpointManager.CreateJson(ICheckpointStore<JsonElement>, JsonSerializerOptions?) confermato nel tag, non CreateInMemory per durabilità |
| Restore | RunStreamingAsync/ResumeStreamingAsync e identità/topologia coerenti documentati nelle guide C# |
| HITL | RequestPort, RequestInfoEvent e SendResponseAsync documentati in C#; non si dichiara un crash-test riuscito né una verifica compilativa del sample nel tag |
| Structured output | ResponseFormat e RunAsync<T> documentati, dipendenti da chat client/modello compatibile |

Le reference latest talvolta indicano ancora 1.13.0 o snippet sincroni; le API asincrone
del tag sono il riferimento per AIAgent. Sorgenti raw di alcuni tipi e sample del tag
non erano recuperabili: non si presenta la guida latest come garanzia binaria.
Prima del codice funzionale si deve compilare contro il lock reale, senza aggiungere
un framework alternativo. Questo è un controllo tecnico previsto, non un chiarimento funzionale.

Fonti: [NuGet core](https://www.nuget.org/packages/Microsoft.Agents.AI/1.26.0),
[OpenAI](https://www.nuget.org/packages/Microsoft.Agents.AI.OpenAI/1.26.0),
[Workflows](https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows/1.26.0),
[release](https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.26.0),
[AIAgent pinned](https://raw.githubusercontent.com/microsoft/agent-framework/dotnet-1.26.0/dotnet/src/Microsoft.Agents.AI.Abstractions/AIAgent.cs),
[CompactionProvider pinned](https://raw.githubusercontent.com/microsoft/agent-framework/dotnet-1.26.0/dotnet/src/Microsoft.Agents.AI/Compaction/CompactionProvider.cs),
[CheckpointManager pinned](https://raw.githubusercontent.com/microsoft/agent-framework/dotnet-1.26.0/dotnet/src/Microsoft.Agents.AI.Workflows/CheckpointManager.cs).

## 2. F-01 — Sessione persistente, FIFO e cutoff

**Decisione**: preservare il cutoff assegnato al salvataggio PM. Si conserva una sola
serializzazione derivata della chat con watermark, non snapshot completi per messaggio.
Se la sessione è più avanti, CreateSessionAsync e il history provider ricostruiscono
solo originali ammessi. Non si riutilizza un indice di compattazione che conosca
messaggi successivi; la sessione non prevale sul registro PostgreSQL.

**Motivo**: risolve U1/U2/A1 senza cambiare retroattivamente l'input del secondo turno.
La FIFO e il collegamento al trigger non richiedono un motore di workflow custom.
Il PM può inviare subito anche quando il modello è offline.
**Alternative escluse**: spostare il cutoff al claim; copiare tutto lo storico per
ogni turno; permettere a una sintesi più recente di oltrepassare lo snapshot.

ChatHistoryProvider legge user/assistant con MessageId stabili; il trigger non è
duplicato. Il salvataggio post-run del provider non è il salvataggio immediato PM:
risposta/sessione/esito vengono committati soltanto dall'operazione atomica di dominio.
Sessioni del dialogo e delle fasi formali hanno ambiti distinti.

Fonte: [storage/sessioni C#](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/storage).

## 3. F-02 — Durabilità minima, recovery e retry

**Decisione**: lease rinnovabile e fencing PostgreSQL, con politiche esplicite nel
contratto AI §2. Default lease 30 s/rinnovo 10 s, massimo 3 attempt, backoff 2/4 s,
deadline 180 s. I contatori persistono. Reclaim da crash e retry di un errore osservato
sono transizioni diverse; Failed/Completed restano terminali.

**Motivo**: protezione degli effetti senza promessa exactly-once LLM. Il dispatcher
riavvia/riprende una richiesta, non conosce il prossimo executor. Conflitto di lease
non è un nuovo risultato o un errore da pubblicare sopra uno stato già completato.
La configurazione del client deve disabilitare retry HTTP duplicati: il client .NET
documenta retry propri e opzioni di pipeline; la compatibilità dell'opzione effettiva
sarà verificata nel lock usato, senza introdurre un ulteriore framework di resilienza.

Per ChatTurn RunAsync non è verificato un checkpoint di workflow intra-chiamata:
sessione committata + cutoff e ripetizione controllata sono la soluzione minima.
Le due fasi MAF formali usano CheckpointManager con store PostgreSQL e restore nativo.
Payload e indice sono opachi; oldest-first e parent sono contratti del provider.
Un commit di dominio riuscito prevale sul checkpoint terminale eventualmente fallito.

**Alternative escluse**: broker aggiuntivo, tabella per step, replay illimitato,
scheduler per executor, confondere AgentSession con un checkpoint workflow.
Fonti: [checkpoint C#](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints),
[contratto store .NET](https://raw.githubusercontent.com/microsoft/agent-framework/main/dotnet/src/Microsoft.Agents.AI.Workflows/Checkpointing/ICheckpointStore.cs),
[client .NET e retry](https://github.com/openai/openai-dotnet#automatically-retrying-requests).

## 4. F-03 — Schema minimo e output del modello

**Decisione**: AnalysisContent 1.0.0 normativo in contracts/ai-workflow.md, con JSON
Schema e regole semantiche. Versione, tipi/array, campi obbligatori, ID, fonti,
conflitti e stato di validazione sono espliciti. Solo Message e PmRevision, senza
anticipare KB. Modifiche PM sono attribuite e distinte dal risultato AI originale.

**Motivo**: un solo contratto sia per schema nativo supportato dal runtime sia per
ValidatedTextJson. System.Text.Json con modelli tipizzati e controlli deterministici
deve applicare la medesima forma/semantica; non basta una stringa JSON parseabile.
La proiezione inviata al provider locale può usare un sottoinsieme compatibile del
vocabolario, ma non allentare la validazione prima del salvataggio/presentazione.
Output AI non può dichiarare PmConfirmed o risolvere conflitti.

**Alternative escluse**: DTO ambiguo, schema editor generale, riparatore AI,
nuove entità KB o approvazioni dedotte dal linguaggio del modello.
Fonte: [structured output C#](https://learn.microsoft.com/en-us/agent-framework/agents/structured-outputs).

## 5. F-04 — HITL valutato e due fasi MAF finite

**Decisione**: project-analysis-v2 produce e persiste l'anteprima; il PM corregge e
conferma una revisione. Solo Genera documento avvia project-document-v1 su InputRevisionId.
ProcessingRun generalizza AnalysisRun con Kind Analysis/Document, evitando una nuova
coda/tabella per ogni fase. AnalysisRevision è la sola nuova entità necessaria.

La guida HITL .NET mostra RequestPort.Create<TRequest,TResponse>, RequestInfoEvent
e SendResponseAsync, con checkpoint che possono includere richieste pendenti.
Questo dimostra l'esistenza documentale delle primitive, non una ripresa già provata
nel nostro storage. Un workflow unico sospeso durante la revisione aggiungerebbe
correlazione durevole richiesta/risposta, versioni concorrenti e ripristino dopo giorni
pur avendo già bisogno di revisioni applicative immutabili.

**Motivo della soluzione minima**: l'attesa è Unvalidated della revisione persistita,
senza lease o deadline; conferma e nuovo comando sono guardie di dominio.
Gli step di entrambe le fasi restano orchestrati solo da MAF, non da uno switch
applicativo. È l'alternativa esplicitamente ammessa dalla decisione approvata.
Le due sessioni d'analisi/generazione sono native e checkpointate separatamente dalla chat.

**Alternative escluse**: attesa in memoria, generazione automatica alla conferma,
motore custom per riprendere step, approvazione cliente implicita.
Fonti: [HITL C#](https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop),
[checkpoint C#](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints),
[binding agenti](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/advanced/agent-executor).

## 6. F-05/F-06 — Qualità editoriale e contesto tecnico separati

**Decisione editoriale**: lunghezza adattiva secondo completezza, chiarezza, precisione,
evidenze e assenza di ripetizioni. Le 2–5 pagine restano orientative. Non c'è soglia
di parole come input, obiettivo o proxy di qualità. Sezioni applicabili definite
in FR-024, omesse solo se davvero non pertinenti.

**Decisione tecnica**: compattazione nativa MAF da subito per chat e analisi, sul
ChatClientBuilder via UseAIContextProviders, usando SummarizationCompactionStrategy.
La registrazione sul builder preserva la history; niente IChatReducer sugli originali.
Budget in token derivato da finestra reale, output riservato e margine, inclusi
istruzioni e payload. Tokenizer/stimatore deve essere compatibile/calibrato col modello.
La prova a soglia ridotta verifica attivazione reale, conteggio finale, fonti e
immutabilità degli originali. Errori di sintesi/contesto sono espliciti, senza tagli nascosti.

**Protezione dello snapshot confermato**: DocumentAgent non ha history accessoria;
riceve il JSON confermato completo e nessun provider di riduzione. Si verifica il budget
prima della chiamata e si fallisce se lo snapshot non rientra. Non si assume un'opzione
nativa non verificata che protegga selettivamente parti del prompt; non si inventa
una strategia custom per riassumere requisiti confermati.

**Alternative escluse**: target editoriale a parole, truncation/sliding window
silenziosa, summarizer custom, sostituire il JSON confermato con una sintesi.
Fonti: [compaction C#](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/compaction),
[sample e registrazioni C#](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/Agents/Agent_Step18_CompactionPipeline/README.md).

## 7. Modello locale e configurazione

Adapter MAF OpenAI su endpoint Chat Completions interno esplicito, senza Responses API
cloud. Endpoint/modelId/credenziali sono server-side, allowlist ed egress controllato.
Nome del modello non prova capacità di contesto o schema; tali dati devono provenire
dal runtime DGX reale e superare i gate del quickstart.
Schema nativo solo se compatibile; altrimenti testo-JSON con stessa validazione.
Nessun fallback cloud per generazione o compattazione.
Fonte: [endpoint custom del client .NET](https://github.com/openai/openai-dotnet#using-a-custom-base-url-and-api-key).

## 8. Stack applicativo e F-07

Si mantengono ASP.NET Core/Identity/EF Core 10, Npgsql EF 10.0.3, Angular 22,
Bootstrap 5.3.8, PostgreSQL 18 e Compose web/app/database. Versioni secondarie
saranno fissate nei lockfile con matrice compatibile. Nessun Python/pgvector ora.
Fonti già consultate: [Angular](https://angular.dev/reference/versions),
[Bootstrap](https://getbootstrap.com/docs/5.3/getting-started/introduction/),
[Npgsql](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3),
[PostgreSQL](https://www.postgresql.org/support/versioning/).

Checklist canonica: checklists/requirements.md. I controlli utili della precedente
readiness vengono mappati e deduplicati prima di eliminarla. Documentale superato,
lacuna aperta e verifica futura sono categorie distinte, senza un PASS runtime fittizio.

## 9. KnowledgeAgent futuro e primitive .NET

**Decisione evolutiva, senza implementazione**: KnowledgeAgent sarà un AIAgent
richiamabile come tool da ConversationalAgent, DocumentAgent e futuri agenti.
La guida C# documenta AsAIFunction sull'agente e AIFunctionFactory.Create per
function tools applicativi. La reference precisa che AsAIFunction accetta una query
e restituisce una stringa: non produce automaticamente un DTO di evidenze. Servirà
un risultato JSON validato con citazioni/locator, provenienza, versioni, conflitti
e lacune. Non condividere una sessione tool mutabile fra conversazioni concorrenti.

**Retrieval**: TextSearchProvider è documentato nel namespace Microsoft.Agents.AI,
collegabile con ChatClientAgentOptions.AIContextProviders e una funzione di ricerca.
TextSearchProviderOptions.SearchTime distingue BeforeAIInvoke e
OnDemandFunctionCalling; quest'ultima espone una function di ricerca selettiva.
TextSearchResult documenta Text, SourceName e SourceLink: non garantisce da solo
versioni, scope o gestione conflitti. Il futuro adapter mapperà risultati e metadati
applicando autorizzazioni e filtri prima del retrieval. Non si importa
create_vector_search_tool Python come API C#.

KnowledgeAgent interrogherà KB progetto, cliente e prodotti/versioni; web pubblico
soltanto con autorizzazione e query prive di dati interni. Docling effettuerà parsing
e chunking in ingestion prima dell'indicizzazione. Nessun provider, tool, schema KB,
classe o servizio placeholder viene aggiunto alla slice.

**Verifica e limite**: consultate guide C# e reference .NET ufficiali; alcune reference
indicano ancora package 1.13.0/latest. Il sorgente tagged 1.26.0 di TextSearchProvider
e relative opzioni non è risultato recuperabile nello strumento di consultazione:
non si dichiara verifica compilativa o parità con Python. Le API sono punti
d'integrazione documentati; firme nel pacchetto esatto e function calling del modello
DGX saranno ricontrollati quando questa capacità futura verrà implementata,
senza bloccare il dialogo testuale della slice corrente.

Fonti: [agent-as-tool C#](https://learn.microsoft.com/en-us/agent-framework/agents/tools/),
[AsAIFunction .NET e sessioni](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.aiagentextensions.asaifunction?view=agent-framework-dotnet-latest),
[function tools C#](https://learn.microsoft.com/en-us/agent-framework/get-started/add-tools),
[RAG e TextSearchProvider C#](https://learn.microsoft.com/en-us/agent-framework/agents/rag),
[enum SearchTime .NET](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.textsearchprovideroptions.textsearchbehavior?view=agent-framework-dotnet-latest).

## Esito della ricerca

F-01–F-07 sono risolte come decisioni di progetto negli artefatti.
API .NET documentate/pinned e limitazioni sono distinte; nessuna parità Python assunta.
Rimangono verifiche tecniche della futura implementazione: compile sul lock,
provider DGX e structured output, compattazione sperimentale, durabilità dei due
workflow e FIFO/recovery/retry con PostgreSQL. Non sono blocchi funzionali irrisolti
né verifiche già eseguite. Nessuna ADR o nuovo documento viene creato per questi dettagli.
