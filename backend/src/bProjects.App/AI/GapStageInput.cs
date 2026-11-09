using System.Text;
using System.Text.Json;
using bProjects.App.Analysis;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace bProjects.App.AI;

// Stadio Gap dedicato su ledger completo (002 D1, contracts/ai-workflow.md §13.7): input, messaggio `gap-stage-message-1`, stima dell'output `gap-stage-estimate-1`
// e controlli di budget. Solo definizione project-analysis-documents-v6. Nessun ranking, top-k, partizionamento o indice di ricerca: i candidati sono l'INTERO ledger non Gap.

/// <summary>Identificatori versionati dello stadio Gap (chiavi di freeze, schema, prompt, formato del messaggio, policy e stima).</summary>
public static class GapStage
{
    public const string Algorithm = "gap-stage-1";
    public const string Prompt = "document-gap-v1";
    public const string AiSchema = "gap-stage-alias-1";
    public const string MessageFormat = "gap-stage-message-1";
    public const string Policy = "gap-stage-ledger-1";
    public const string Estimate = "gap-stage-estimate-1";

    /// <summary>Valore costante di <c>contract</c> nel DTO dello stadio.</summary>
    public const string ContractValue = "gap-stage-1";

    /// <summary>Schema del payload del WorkOutput (dentro il payload); lo schema del record WorkOutput è <see cref="AiSchema"/>.</summary>
    public const string OutputSchema = "gap-stage-output-1";

    public const string ValidationVersion = "gap-stage-validation-1";
    public const string BatchStage = "gap";
    public const string SchemaName = "gap_stage";
    public const string PromptFile = "document-gap-v1.md";

    /// <summary>Sorgente dei candidati in 002: l'intero ledger non Gap dello snapshot congelato, in ordine di catalogo.</summary>
    public const string SearchBasisLedgerExhaustive = "ledger_exhaustive";

    /// <summary>Chiavi di freeze (<c>ProcessingRun.PromptVersions</c>) dello stadio: sostituiscono <c>reconcileGapPolicy</c> nella definizione v6.</summary>
    public static IReadOnlyDictionary<string, string> FreezeKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gapStage"] = Algorithm,
        ["gapPrompt"] = Prompt,
        ["gapAiSchema"] = AiSchema,
        ["gapMessageFormat"] = MessageFormat,
        ["gapPolicy"] = Policy,
        ["gapEstimate"] = Estimate,
    };

    private static readonly Lazy<string> InstructionsText = new(() => ConversationalAgent.LoadPrompt(PromptFile));

    public static string Instructions => InstructionsText.Value;
}

/// <summary>Metadati documentali di un'evidenza, letti dallo snapshot in una mappa laterale (mai in <see cref="AliasSourceEntry"/>: <c>CatalogHash</c> e <c>message-format-1</c> restano invariati).</summary>
public sealed record GapStageEvidenceMeta(string? Document, int? Version, string? Section);

/// <summary>VOCE dello stadio: un candidato o un Gap con alias <c>E</c> del catalogo, metadati, enunciato e citazioni (testo-dato).</summary>
public sealed record GapStageVoice(
    string Alias, Guid EvidenceId, EvidenceOriginKind Origin, EvidenceKind Kind, EvidencePolarity Polarity, EvidenceCertainty Certainty, string Statement,
    GapStageEvidenceMeta Meta, IReadOnlyList<(string SourceAlias, EvidenceSourceRole Role, string Quote)> Sources);

/// <summary>Domanda aperta canonica offerta allo stadio con chiave locale <c>i&lt;n&gt;</c>.</summary>
public sealed record GapStageQuestionRef(string Key, Guid ItemId, string Text, IReadOnlyList<string> EvidenceAliases);

/// <summary>Conflitto canonico offerto allo stadio con chiave locale <c>c&lt;n&gt;</c>.</summary>
public sealed record GapStageConflictRef(string Key, Guid ConflictId, string Description, IReadOnlyList<string> EvidenceAliases);

/// <summary>
/// Riferimenti Open canonici dell'analisi assemblata: domande e conflitti con chiavi locali allo stadio (ordine canonico) e, per ogni Gap, la domanda canonica che lo
/// rappresenta. <see cref="MissingGaps"/> sono i Gap (alias) senza domanda canonica: il server non la crea mai (precondizione di §13.7).
/// </summary>
public sealed record GapStageReferences(
    IReadOnlyList<GapStageQuestionRef> Questions, IReadOnlyList<GapStageConflictRef> Conflicts, IReadOnlyDictionary<string, string> GapQuestion, IReadOnlyList<string> MissingGaps)
{
    public static GapStageReferences Empty { get; } = new([], [], new Dictionary<string, string>(), []);

    /// <summary>
    /// Deriva i riferimenti dall'analisi assemblata dalla gerarchia: <c>i&lt;n&gt;</c> = n-esimo elemento di <c>openQuestions</c>, <c>c&lt;n&gt;</c> = n-esimo conflitto Open, entrambi in ordine
    /// canonico. La domanda di un Gap è la prima domanda la cui mappa evidenze→item contiene il Gap. NESSUNA garanzia della gerarchia: l'assenza è un esito, mai creata dal server.
    /// </summary>
    public static GapStageReferences From(DocumentAnalysisContent analysis, IReadOnlyList<EvidenceMapEntry> map, AnalysisAliasCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(catalog);
        var evidenceOfItem = map.GroupBy(m => m.ItemId).ToDictionary(g => g.Key, g => g.SelectMany(m => m.EvidenceIds).Distinct().ToList());

        List<string> Aliases(IEnumerable<Guid> itemIds) => [.. itemIds.SelectMany(i => evidenceOfItem.GetValueOrDefault(i) ?? [])
            .Select(id => catalog.EvidenceById(id)?.Alias).Where(a => a is not null).Select(a => a!).Distinct(StringComparer.Ordinal).OrderBy(AnalysisAliasPattern.Number)];

        var questions = analysis.OpenQuestions
            .Select((q, index) => new GapStageQuestionRef("i" + (index + 1), q.Id, q.Text, Aliases([q.Id]))).ToList();
        var conflicts = analysis.Conflicts.Where(c => c.Status == AnalysisConflictStatus.Open)
            .Select((c, index) => new GapStageConflictRef("c" + (index + 1), c.Id, c.Description, Aliases(c.Alternatives.Select(a => a.Id)))).ToList();

        var gapQuestion = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var gap in catalog.Data.Evidence.Where(e => e.Kind == EvidenceKind.Gap))
        {
            var question = questions.FirstOrDefault(q => q.EvidenceAliases.Contains(gap.Alias, StringComparer.Ordinal));
            if (question is null)
            {
                missing.Add(gap.Alias);
            }
            else
            {
                gapQuestion[gap.Alias] = question.Key;
            }
        }

        return new(questions, conflicts, gapQuestion, missing);
    }

    /// <summary>
    /// Hash canonico dei riferimenti (chiave, testo, evidenze, domanda di ogni Gap): entra nell'<c>inputHash</c> del batch dello stadio. Indipendente dal run (niente id canonici, derivati dall'id del run): uguale
    /// per un root di retry sulle stesse fonti.
    /// </summary>
    public string Hash() => CanonicalJson.HashText(ReconciliationCodec.Canonical(new
    {
        questions = Questions.Select(q => new { key = q.Key, text = q.Text, evidence = q.EvidenceAliases }).ToList(),
        conflicts = Conflicts.Select(c => new { key = c.Key, description = c.Description, evidence = c.EvidenceAliases }).ToList(),
        gapQuestion = GapQuestion.OrderBy(kv => AnalysisAliasPattern.Number(kv.Key)).Select(kv => new { gap = kv.Key, question = kv.Value }).ToList(),
    }));
}

/// <summary>
/// Contratto d'ingresso modulare dello stadio (confine con la 003): lista ORDINATA di evidenze candidate + <see cref="SearchBasis"/> + <see cref="RetrievalComplete"/>, più i Gap da valutare e i
/// riferimenti Open canonici. In 002 i candidati sono l'intero ledger non Gap in ordine di catalogo (<c>ledger_exhaustive</c>, <c>true</c>). Un unico record tipizzato, nessuna interfaccia.
/// </summary>
public sealed record GapStageInput(
    IReadOnlyList<GapStageVoice> Candidates, string SearchBasis, bool RetrievalComplete, IReadOnlyList<GapStageVoice> Gaps, GapStageReferences References);

/// <summary>Stima PREVENTIVA e prudente dell'output dello stadio (<c>gap-stage-estimate-1</c>): limiti superiori fissati dalla versione, cambiarli richiede una nuova versione.</summary>
public static class GapStageEstimate
{
    /// <summary>Scheletro: <c>contract</c>, array e punteggiatura.</summary>
    public const int FixedTokens = 100;

    /// <summary>Per Gap: record con alias, esito, motivo, motivazione (fino a ~400 caratteri), riscontri e riferimenti.</summary>
    public const int PerGapTokens = 260;

    /// <summary>Margine moltiplicativo come <c>single-call-output-estimate-1</c>.</summary>
    public const double SafetyMargin = 1.25;

    public static long Estimate(int gaps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gaps);
        return (long)Math.Ceiling((FixedTokens + (double)gaps * PerGapTokens) * SafetyMargin);
    }

    /// <summary>Un tetto non positivo è trattato come superato (nessun invio incerto).</summary>
    public static bool Exceeds(int gaps, int outputTokens) => outputTokens <= 0 || Estimate(gaps) > outputTokens;
}

/// <summary>Esito di un controllo di budget dello stadio: <see cref="Reason"/> è il motivo tipizzato (prefisso stabile di <c>ErrorMessage</c>) o null se entra.</summary>
public sealed record GapStageVerdict(bool Fits, string? Reason, string Message)
{
    public DocumentWorkException ToException() =>
        new(DocumentWorkException.CallLimitExceeded, Message);
}

/// <summary>Controlli di budget dello stadio: input (tokenizer/calibrazione, <see cref="ContextBudget"/> già ammesse) e output (<see cref="GapStageEstimate"/>).</summary>
public static class GapStageBudget
{
    /// <summary>Maggiorazione prudente fissata dalla versione per la sezione dei riferimenti canonici non ancora nota al controllo (1) del routing.</summary>
    public const int ReferenceAllowanceFixedTokens = 1000;

    public const int ReferenceAllowancePerGapTokens = 150;

    public static int ReferenceAllowance(int gaps) => ReferenceAllowanceFixedTokens + (ReferenceAllowancePerGapTokens * gaps);

    /// <summary>
    /// Controllo (1) al routing con <paramref name="routing"/> = true (input = ledger intero + maggiorazione dei riferimenti), controllo (2) esatto con false (messaggio con i riferimenti reali).
    /// Input oltre B ⇒ <c>gap_ledger_exceeds_input</c>; output atteso oltre O ⇒ <c>gap_assessment_exceeds_output</c>. Un conteggio non disponibile non è mai «entro budget» (il chiamante lo tratta prima).
    /// </summary>
    public static GapStageVerdict Evaluate(PayloadPreflight measure, int gapCount, bool routing)
    {
        ArgumentNullException.ThrowIfNull(measure);
        var allowance = routing ? ReferenceAllowance(gapCount) : 0;
        var tokens = (long)measure.Tokens + allowance;
        var detail = $"{measure.Tokens} token{(routing ? $" più la maggiorazione prudente di {allowance} token per i riferimenti" : string.Empty)}, qualità della misura: {measure.Quality}";
        if (measure.Quality == TokenCountQuality.Unavailable || tokens > measure.InputBudgetTokens)
        {
            return new(false, DocumentWorkException.GapLedgerExceedsInput,
                $"{DocumentWorkException.GapLedgerExceedsInput}: l'intero ledger delle fonti selezionate ({detail}) supera il budget di input dello stadio Gap ({measure.InputBudgetTokens} token) "
                + $"con {gapCount} evidenze di tipo Gap: la valutazione dei Gap oltre il contesto di una sola chiamata non è supportata e nessuna valutazione è stata presentata. Riduci la selezione delle fonti e riprova.");
        }

        if (GapStageEstimate.Exceeds(gapCount, measure.Profile.OutputTokens))
        {
            return new(false, DocumentWorkException.GapAssessmentExceedsOutput,
                $"{DocumentWorkException.GapAssessmentExceedsOutput}: le valutazioni di {gapCount} evidenze di tipo Gap hanno una risposta attesa di circa {GapStageEstimate.Estimate(gapCount)} token "
                + $"(stima prudente {GapStage.Estimate}) che supera il budget di output ({measure.Profile.OutputTokens} token); misura dell'input: {detail}. "
                + "Nessuna valutazione parziale è stata presentata. Riduci la selezione delle fonti o i Gap e riprova.");
        }

        return new(true, null, string.Empty);
    }
}

/// <summary>Caricamento dei metadati documentali dallo snapshot (documento, versione, sezione) per le sole evidenze di origine documentale.</summary>
public static class GapStageMetadata
{
    public static async Task<IReadOnlyDictionary<Guid, GapStageEvidenceMeta>> LoadAsync(AppDbContext db, DocumentAnalysisInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(input);
        var passages = input.Evidence.Select(e => (Evidence: e, Source: e.Sources.FirstOrDefault(s => s.Kind == EvidenceSourceKind.DocumentPassage))).Where(t => t.Source is not null).ToList();
        var documentIds = passages.Select(t => t.Source!.DocumentId!.Value).Distinct().ToList();
        var versionIds = passages.Select(t => t.Source!.DocumentVersionId!.Value).Distinct().ToList();
        var blockIds = passages.Where(t => t.Source!.BlockId is not null).Select(t => t.Source!.BlockId!.Value).Distinct().ToList();
        var names = await db.ProjectDocuments.AsNoTracking().Where(d => documentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.DisplayName, ct);
        var versions = await db.DocumentVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Version, ct);
        var headings = await db.DocumentBlocks.AsNoTracking().Where(b => blockIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.HeadingPathJson, ct);

        var map = new Dictionary<Guid, GapStageEvidenceMeta>();
        foreach (var (evidence, source) in passages)
        {
            string? section = null;
            if (source!.BlockId is { } block && headings.TryGetValue(block, out var json))
            {
                var path = JsonSerializer.Deserialize<string[]>(json) ?? [];
                section = path.Length == 0 ? null : string.Join(" > ", path);
            }

            map[evidence.EvidenceId] = new(
                names.GetValueOrDefault(source.DocumentId!.Value), versions.TryGetValue(source.DocumentVersionId!.Value, out var version) ? version : null, section);
        }

        return map;
    }
}

/// <summary>Costruzione dell'input e del messaggio <c>gap-stage-message-1</c>: stessa funzione per la misura, il controllo e l'invio.</summary>
public static class GapStageBuilder
{
    /// <summary>
    /// Input dello stadio dal ledger congelato: TUTTE le evidenze non Gap come candidati (conteggio e ordine del catalogo, nessuna esclusione né sintesi) e i Gap da valutare.
    /// Con <paramref name="references"/> null (controllo 1 al routing) i riferimenti Open non sono ancora noti.
    /// </summary>
    public static GapStageInput Build(
        DocumentAnalysisInput input, AnalysisAliasCatalog catalog, IReadOnlyDictionary<Guid, GapStageEvidenceMeta> meta, GapStageReferences? references)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(meta);
        var byId = input.Evidence.ToDictionary(e => e.EvidenceId);
        var candidates = new List<GapStageVoice>();
        var gaps = new List<GapStageVoice>();
        foreach (var entry in catalog.Data.Evidence)
        {
            var evidence = byId[entry.EvidenceId];
            var voice = new GapStageVoice(
                entry.Alias, entry.EvidenceId, entry.Origin, entry.Kind, entry.Polarity, entry.Certainty, evidence.Statement,
                meta.GetValueOrDefault(entry.EvidenceId) ?? new(null, null, null),
                [.. entry.Sources.Select(l => (l.SourceAlias, l.Role, catalog.Source(l.SourceAlias)!.Quote))]);
            (entry.Kind == EvidenceKind.Gap ? gaps : candidates).Add(voice);
        }

        return new(candidates, GapStage.SearchBasisLedgerExhaustive, true, gaps, references ?? GapStageReferences.Empty);
    }

    /// <summary>Testo-dato: i delimitatori del formato (<c>&lt;&lt;&lt;</c> e <c>&gt;&gt;&gt;</c>) nel contenuto sono resi con virgolette angolari singole, mai interpretabili come struttura.</summary>
    internal static string Data(string text) => text.Replace("<<<", "‹‹‹", StringComparison.Ordinal).Replace(">>>", "›››", StringComparison.Ordinal);

    /// <summary>Valore di attributo fra virgolette: escape di barra, virgolette e a capo, più i delimitatori.</summary>
    internal static string Attribute(string value) =>
        Data(value).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>Messaggio unico: intestazioni, Gap da valutare, ledger completo, riferimenti Open canonici. A capo sempre «\n». Mai UUID, hash o blocchi.</summary>
    public static IReadOnlyList<ChatMessage> Message(GapStageInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = new StringBuilder();
        var spans = new List<int>();
        text.Append("Formato: ").Append(GapStage.MessageFormat).Append('\n')
            .Append("searchBasis: ").Append(input.SearchBasis).Append('\n')
            .Append("retrievalComplete: ").Append(input.RetrievalComplete ? "true" : "false").Append('\n');

        text.Append("\nGap da valutare (").Append(input.Gaps.Count).Append("):\n");
        foreach (var gap in input.Gaps)
        {
            Voice(text, spans, gap, input.References.GapQuestion.GetValueOrDefault(gap.Alias));
        }

        text.Append("\nEvidenze del ledger completo (").Append(input.Candidates.Count).Append(", tutte, senza ranking né selezione):\n");
        foreach (var candidate in input.Candidates)
        {
            Voice(text, spans, candidate, null);
        }

        text.Append("\nDomande aperte canoniche dell'analisi (riferimenti per openItem):\n");
        if (input.References.Questions.Count == 0)
        {
            text.Append("(nessuna)\n");
        }

        foreach (var question in input.References.Questions)
        {
            text.Append('[').Append(question.Key).Append("] (").Append(string.Join(", ", question.EvidenceAliases)).Append(") ").Append(Data(question.Text.Replace('\n', ' '))).Append('\n');
        }

        text.Append("\nConflitti aperti canonici dell'analisi (riferimenti per openConflict):\n");
        if (input.References.Conflicts.Count == 0)
        {
            text.Append("(nessuno)\n");
        }

        foreach (var conflict in input.References.Conflicts)
        {
            text.Append('[').Append(conflict.Key).Append("] (").Append(string.Join(", ", conflict.EvidenceAliases)).Append(") ").Append(Data(conflict.Description.Replace('\n', ' '))).Append('\n');
        }

        text.Append("\nRispondi ora con l'unico oggetto JSON `").Append(GapStage.ContractValue).Append("`.");
        var message = new ChatMessage(ChatRole.User, text.ToString());
        if (spans.Count > 0)
        {
            message.AdditionalProperties = new() { [ContextComposition.DocumentSpansProperty] = spans.ToArray() };
        }

        return [message];
    }

    private static void Voice(StringBuilder text, List<int> spans, GapStageVoice voice, string? canonicalQuestion)
    {
        var start = text.Length;
        text.Append("<<<VOCE [").Append(voice.Alias).Append("] origine=").Append(voice.Origin).Append(" tipo=").Append(voice.Kind).Append(" polarita=").Append(voice.Polarity)
            .Append(" certezza=").Append(voice.Certainty);
        if (voice.Meta.Document is { } document)
        {
            text.Append(" documento=\"").Append(Attribute(document)).Append('"');
        }

        if (voice.Meta.Version is { } version)
        {
            text.Append(" versione=").Append(version);
        }

        if (!string.IsNullOrEmpty(voice.Meta.Section))
        {
            text.Append(" sezione=\"").Append(Attribute(voice.Meta.Section)).Append('"');
        }

        if (canonicalQuestion is not null)
        {
            text.Append(" domandaCanonica=").Append(canonicalQuestion);
        }

        text.Append(">>>\n").Append(Data(voice.Statement)).Append('\n');
        foreach (var (alias, role, quote) in voice.Sources)
        {
            text.Append("<<<FONTE [").Append(alias).Append("] ruolo=").Append(role).Append(">>>\n").Append(Data(quote)).Append("\n<<<FINE FONTE>>>\n");
        }

        text.Append("<<<FINE VOCE>>>\n");
        if (voice.Origin == EvidenceOriginKind.Document)
        {
            spans.AddRange([start, text.Length - start]);
        }
    }
}
