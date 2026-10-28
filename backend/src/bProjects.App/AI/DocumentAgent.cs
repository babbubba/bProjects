using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using bProjects.App.Analysis;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

/// <summary>Input fidato del DocumentAgent: identità del server, versione e contenuto confermato della revisione di input.</summary>
public sealed record DocumentInput(Guid ProjectId, Guid AnalysisId, Guid RevisionId, int Version, AnalysisContent Content);

/// <summary>
/// DocumentAgent (T039, contracts/ai-workflow.md §6): un ChatClientAgent con prompt e sessione propri che riceve l'INTERO JSON
/// confermato più le istruzioni e produce il Markdown della bozza. Nessuna history né dialogo (un solo messaggio utente), nessun
/// strumento e nessun provider di compattazione: il client è <see cref="AgentFactory.CreateDocumentClient"/>, quindi una riduzione
/// nativa non può riassumere involontariamente i requisiti confermati. Prima di ogni chiamata il preflight dei token fallisce con
/// <see cref="ContextOverflowException"/> (context_overflow, permanente) se il contenuto necessario non rientra nel budget:
/// mai omissioni né documento parziale. Il formato Markdown è validato altrove (<c>DocumentDraftValidator</c>).
/// </summary>
public sealed class DocumentAgent(AgentFactory factory, ChatOptions chatOptions)
{
    public const string AgentName = "DocumentAgent";

    /// <summary>Id stabile dell'agente nel grafo project-document-v1 (identità richiesta dal resume).</summary>
    public const string AgentId = "document";

    public const string PromptVersion = "document-v1";

    /// <summary>Id nativo dell'executor che ospita l'agente: «{Name}_{Id}» come assegnato da MAF.</summary>
    public const string HostExecutorId = $"{AgentName}_{AgentId}";

    private static readonly Lazy<string> Prompt = new(() => LoadPrompt("document.md"));

    /// <summary>Opzioni JSON per il prompt: stesso schema dello storage, ma con le lettere accentate in chiaro (meno token, leggibile dal modello).</summary>
    private static readonly JsonSerializerOptions PromptJson = new(AnalysisContentJson.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Istruzioni della generazione (AI/Prompts/document.md, risorsa incorporata).</summary>
    public static string Instructions => Prompt.Value;

    /// <summary>
    /// Il messaggio utente unico: dati affidabili del server e intero JSON della revisione. Il JSON è l'unico testo con graffe, così
    /// il contenuto è isolabile dal primo «{» all'ultimo «}».
    /// </summary>
    public static List<ChatMessage> BuildMessages(DocumentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = new StringBuilder()
            .AppendLine("Dati affidabili del server per questa generazione:")
            .AppendLine($"- analysisId: {input.AnalysisId}")
            .AppendLine($"- projectId: {input.ProjectId}")
            .AppendLine($"- revisionId: {input.RevisionId} (versione {input.Version})")
            .AppendLine("Analisi confermata dal project manager, JSON AnalysisContent 1.0.0 completo:")
            .AppendLine(JsonSerializer.Serialize(input.Content, PromptJson))
            .Append("Scrivi ora la bozza del documento in Markdown.")
            .ToString();
        return [new ChatMessage(ChatRole.User, text)];
    }

    /// <summary>Contenuto, istruzioni comprese, entro il budget di input; altrimenti <see cref="ContextOverflowException"/> prima di ogni chiamata.</summary>
    public ValueTask PreflightAsync(IEnumerable<ChatMessage> messages, CancellationToken ct) =>
        factory.EnsureDocumentWithinBudgetAsync(messages, Instructions, ct);

    /// <summary>
    /// L'agente vero, pronto per l'host nativo del workflow (<c>BindAsExecutor</c>) o per <c>RunAsync</c>. Senza
    /// ChatHistoryProvider applicativo né provider di compattazione; testo libero, nessun formato strutturato.
    /// </summary>
    public ChatClientAgent CreateAgent()
    {
        var options = new ChatOptions
        {
            ModelId = chatOptions.ModelId,
            MaxOutputTokens = chatOptions.MaxOutputTokens,
            Temperature = chatOptions.Temperature,
            RawRepresentationFactory = chatOptions.RawRepresentationFactory,
            Instructions = Instructions,
        };
        return new ChatClientAgent(factory.CreateDocumentClient(), new ChatClientAgentOptions { Id = AgentId, Name = AgentName, ChatOptions = options });
    }

    private static string LoadPrompt(string file)
    {
        using var stream = typeof(DocumentAgent).Assembly.GetManifestResourceStream("Prompts." + file)
            ?? throw new InvalidOperationException($"Prompt {file} non incorporato nell'assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }
}
