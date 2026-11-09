using System.Text.Json;
using System.Text.RegularExpressions;

namespace bProjects.App.Analysis;

// Validazione strutturale e semantica di AnalysisContent 1.0.0 (contracts/ai-workflow.md §4, regole 1-9).
// Le stesse regole valgono per l'output dell'AI, per il PUT del PM e per la conferma; cambiano solo i privilegi
// dell'attore. Nessuna riparazione: il contenuto è accettato così com'è oppure rifiutato per intero.

/// <summary>Chi produce il contenuto: determina i privilegi, non le regole comuni.</summary>
public enum AnalysisActorKind
{
    /// <summary>Output del modello: solo Unvalidated, fonti Message, conflitti Open.</summary>
    Ai,

    /// <summary>PUT dell'anteprima: nuova revisione Unvalidated con fonti PmRevision e risoluzioni motivate.</summary>
    PmEdit,

    /// <summary>Conferma: identica alla revisione corrente salvo validationState.</summary>
    PmConfirm,
}

/// <summary>Messaggio noto al server (stesso progetto/conversazione dell'analisi).</summary>
public sealed record AnalysisMessageRef(Guid Id, Guid ProjectId, AnalysisMessageRole Role, long Sequence);

/// <summary>Input fidato: ciò che il modello e il PM non possono dichiarare da soli.</summary>
public sealed record AnalysisValidationContext(
    Guid ProjectId,
    Guid AnalysisId,
    long SourceCutoffSequence,
    IReadOnlyCollection<AnalysisMessageRef> Messages,
    IReadOnlySet<Guid> PmRevisionIds);

public sealed record AnalysisValidationResult(bool Accepted, string? Code, IReadOnlyList<string> Violations, AnalysisContent? Content)
{
    public static AnalysisValidationResult Ok(AnalysisContent content) => new(true, null, [], content);
}

public sealed record DocumentAnalysisValidationResult(bool Accepted, string? Code, IReadOnlyList<string> Violations, DocumentAnalysisContent? Content);

public static class AnalysisContentValidator
{
    public const string InvalidAgentOutput = "invalid_agent_output";
    public const string PreviewInvalid = "preview_invalid";

    /// <summary>
    /// Testo → JSON puro → DTO tipizzato strict → regole semantiche. Markdown, recinti di codice, prosa, JSON
    /// troncato, null o non-oggetti sono rifiutati senza alcun tentativo di estrazione o riparazione.
    /// <paramref name="currentJson"/> è la revisione corrente (obbligatoria per la conferma, opzionale per il PUT).
    /// </summary>
    public static AnalysisValidationResult ValidateText(string? text, AnalysisValidationContext context, AnalysisActorKind actor, string? currentJson = null)
    {
        var code = CodeFor(actor);
        var parsed = Parse(text, out var content, out var error);
        if (!parsed)
        {
            return new(false, code, [error!], null);
        }

        AnalysisContent? current = null;
        if (currentJson is not null && !Parse(currentJson, out current, out _))
        {
            return new(false, code, ["La revisione corrente non è un AnalysisContent valido."], null);
        }

        return Validate(content!, context, actor, current);
    }

    public static AnalysisValidationResult Validate(AnalysisContent content, AnalysisValidationContext context, AnalysisActorKind actor, AnalysisContent? current = null)
    {
        var violations = new List<string>();
        var checker = new Checker(content, context, actor, violations);
        checker.Run();

        if (actor == AnalysisActorKind.PmConfirm)
        {
            if (current is null)
            {
                violations.Add("conferma: revisione corrente mancante.");
            }
            else
            {
                CheckConfirmOnlyChangesState(content, current, violations);
            }
        }
        else if (actor == AnalysisActorKind.PmEdit && current is not null)
        {
            CheckHistoricalSourcesPreserved(content, current, violations);
        }

        return violations.Count == 0
            ? AnalysisValidationResult.Ok(content)
            : new(false, CodeFor(actor), violations, null);
    }

    /// <summary>
    /// Validatore di revisione 1.1.0 (T060): regole strutturali di <see cref="DocumentAnalysisContentRules"/> più identità del server, stato per attore,
    /// fonti Message/PmRevision risolte e privilegi (l'AI non risolve conflitti né crea PmRevision; il PM non crea né altera fonti documentali, che
    /// devono coincidere con quelle della revisione corrente; risoluzioni motivate con fonte PmRevision). La verifica di blocco/quote contro l'originale
    /// delle fonti documentali dell'AI resta a monte (snapshot congelato, T059): qui le fonti documentali sono immutabili.
    /// </summary>
    public static DocumentAnalysisValidationResult Validate(
        DocumentAnalysisContent content, AnalysisValidationContext context, AnalysisActorKind actor, DocumentAnalysisContent? current, int readyDocumentSourceCount)
    {
        var violations = new List<string>();
        var pmMessages = context.Messages.Count(m => m.Role == AnalysisMessageRole.User && m.Sequence <= context.SourceCutoffSequence);
        violations.AddRange(DocumentAnalysisContentRules.Check(
            content, new DocumentAnalysisFacts(context.ProjectId, context.AnalysisId, context.SourceCutoffSequence, pmMessages, readyDocumentSourceCount)));

        var expected = actor == AnalysisActorKind.PmConfirm ? AnalysisContentState.PmConfirmed : AnalysisContentState.Unvalidated;
        if (content.ValidationState != expected)
        {
            violations.Add($"validationState: atteso {expected} per questo attore.");
        }

        var historical = (current?.Sources ?? []).ToDictionary(x => x.Id);
        for (var i = 0; i < content.Sources.Count; i++)
        {
            var source = content.Sources[i];
            var path = $"sources[{i}]";
            switch (source.Kind)
            {
                case DocumentAnalysisSourceKind.Message when source.MessageId is not null && source.MessageRole is not null:
                    var message = context.Messages.FirstOrDefault(m => m.Id == source.MessageId);
                    if (message is null || message.ProjectId != context.ProjectId)
                    {
                        violations.Add($"{path}.messageId: messaggio inesistente nel progetto.");
                    }
                    else if (message.Sequence > context.SourceCutoffSequence)
                    {
                        violations.Add($"{path}.messageId: messaggio oltre il cutoff.");
                    }
                    else if (message.Role != source.MessageRole)
                    {
                        violations.Add($"{path}.messageRole: incoerente con il messaggio.");
                    }

                    break;
                case DocumentAnalysisSourceKind.PmRevision when source.RevisionId is not null:
                    if (actor == AnalysisActorKind.Ai)
                    {
                        violations.Add($"{path}: l'AI non può creare fonti PmRevision.");
                    }
                    else if (!context.PmRevisionIds.Contains(source.RevisionId.Value))
                    {
                        violations.Add($"{path}.revisionId: non è una revisione PM di questa analisi.");
                    }

                    break;
                case DocumentAnalysisSourceKind.DocumentPassage when actor != AnalysisActorKind.Ai:
                    if (!historical.TryGetValue(source.Id, out var known) || known != source)
                    {
                        violations.Add($"{path}: il PM non può aggiungere o modificare fonti documentali.");
                    }

                    break;
            }
        }

        for (var i = 0; i < content.Conflicts.Count; i++)
        {
            CheckResolution(content.Conflicts[i], $"conflicts[{i}]", actor, content.Sources, violations);
        }

        if (actor == AnalysisActorKind.PmConfirm)
        {
            if (current is null)
            {
                violations.Add("conferma: revisione corrente mancante.");
            }
            else
            {
                if (current.ValidationState != AnalysisContentState.Unvalidated)
                {
                    violations.Add("conferma: la revisione corrente è già confermata.");
                }

                var before = JsonSerializer.SerializeToNode(current with { ValidationState = AnalysisContentState.PmConfirmed }, DocumentAnalysisContentJson.Options);
                var after = JsonSerializer.SerializeToNode(content, DocumentAnalysisContentJson.Options);
                if (!System.Text.Json.Nodes.JsonNode.DeepEquals(before, after))
                {
                    violations.Add("conferma: può cambiare soltanto validationState rispetto alla revisione corrente.");
                }
            }
        }
        else if (actor == AnalysisActorKind.PmEdit && current is not null)
        {
            var kept = content.Sources.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
            violations.AddRange(current.Sources
                .Where(h => !kept.TryGetValue(h.Id, out var k) || k != h)
                .Select(h => $"sources: la fonte storica {h.Id} non può essere rimossa o mutata."));
        }

        var distinct = violations.Distinct().ToList();
        return distinct.Count == 0
            ? new(true, null, [], content)
            : new(false, CodeFor(actor), distinct, null);
    }

    private static void CheckResolution(
        DocumentAnalysisConflict conflict, string path, AnalysisActorKind actor, IReadOnlyList<DocumentAnalysisSource> sources, List<string> violations)
    {
        if (conflict.Status != AnalysisConflictStatus.Resolved)
        {
            return;
        }

        if (actor == AnalysisActorKind.Ai)
        {
            violations.Add($"{path}.status: l'AI non può risolvere conflitti.");
            return;
        }

        var resolution = conflict.Resolution;
        if (resolution is null)
        {
            return; // già segnalato dalle regole strutturali
        }

        if (resolution.SelectedAlternativeIds.Count < 1 || resolution.SelectedAlternativeIds.Distinct().Count() != resolution.SelectedAlternativeIds.Count)
        {
            violations.Add($"{path}.resolution.selectedAlternativeIds: almeno una scelta, senza duplicati.");
        }

        var alternativeIds = conflict.Alternatives.Select(a => a.Id).ToHashSet();
        if (resolution.SelectedAlternativeIds.Any(id => !alternativeIds.Contains(id)))
        {
            violations.Add($"{path}.resolution.selectedAlternativeIds: la scelta deve essere fra le alternative del conflitto.");
        }

        if (string.IsNullOrWhiteSpace(resolution.Reason))
        {
            violations.Add($"{path}.resolution.reason: motivo vuoto o solo spazi.");
        }

        var byId = sources.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        if (!resolution.EvidenceRefs.Any(r => byId.TryGetValue(r, out var s) && s.Kind == DocumentAnalysisSourceKind.PmRevision))
        {
            violations.Add($"{path}.resolution.evidenceRefs: la risoluzione deve citare la modifica PM (fonte PmRevision).");
        }
    }

    private static string CodeFor(AnalysisActorKind actor) => actor == AnalysisActorKind.Ai ? InvalidAgentOutput : PreviewInvalid;

    private static bool Parse(string? text, out AnalysisContent? content, out string? error)
    {
        content = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Output vuoto: atteso un unico oggetto JSON.";
            return false;
        }

        try
        {
            content = AnalysisContentJson.Deserialize(text);
            return true;
        }
        catch (JsonException ex)
        {
            // Il messaggio del serializzatore può riportare nomi di proprietà o caratteri scelti dal modello (output non validato,
            // che non deve raggiungere il browser attraverso l'errore del run): si indicano solo percorso redatto e posizione.
            var position = ex.LineNumber is { } line && ex.BytePositionInLine is { } column ? $" (riga {line + 1}, posizione {column + 1})" : "";
            error = $"JSON non valido per AnalysisContent 1.0.0 al percorso {RedactedPath(ex.Path)}{position}.";
            return false;
        }
    }

    private static readonly Regex PlainPath = new(@"^\$(?:\.[A-Za-z][A-Za-z0-9]*|\[\d+\])*$", RegexOptions.CultureInvariant);

    private static readonly Regex PathName = new(@"\.([A-Za-z][A-Za-z0-9]*)", RegexOptions.CultureInvariant);

    /// <summary>Nomi dei campi dello schema: gli unici segmenti di percorso che un errore di parsing può riportare.</summary>
    private static readonly HashSet<string> SchemaNames =
    [
        .. new[]
        {
            typeof(AnalysisContent), typeof(AnalysisContextAndScope), typeof(AnalysisItem),
            typeof(AnalysisSource), typeof(AnalysisConflict), typeof(AnalysisConflictResolution),
        }.SelectMany(t => t.GetProperties()).Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)),
    ];

    /// <summary>Percorso JSON dell'errore con i soli campi dello schema; un nome sconosciuto (scelto dal modello) diventa «?».</summary>
    private static string RedactedPath(string? path) =>
        path is null || !PlainPath.IsMatch(path)
            ? "$"
            : PathName.Replace(path, m => SchemaNames.Contains(m.Groups[1].Value) ? m.Value : ".?");

    private static void CheckConfirmOnlyChangesState(AnalysisContent content, AnalysisContent current, List<string> violations)
    {
        if (current.ValidationState != AnalysisContentState.Unvalidated)
        {
            violations.Add("conferma: la revisione corrente è già confermata.");
        }

        var expected = JsonSerializer.SerializeToNode(current with { ValidationState = AnalysisContentState.PmConfirmed }, AnalysisContentJson.Options);
        var actual = JsonSerializer.SerializeToNode(content, AnalysisContentJson.Options);
        if (!System.Text.Json.Nodes.JsonNode.DeepEquals(expected, actual))
        {
            violations.Add("conferma: può cambiare soltanto validationState rispetto alla revisione corrente.");
        }
    }

    private static void CheckHistoricalSourcesPreserved(AnalysisContent content, AnalysisContent current, List<string> violations)
    {
        var byId = content.Sources.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var historical in current.Sources)
        {
            if (!byId.TryGetValue(historical.Id, out var kept) || kept != historical)
            {
                violations.Add($"sources: la fonte storica {historical.Id} non può essere rimossa o mutata.");
            }
        }
    }

    private sealed class Checker(AnalysisContent content, AnalysisValidationContext context, AnalysisActorKind actor, List<string> violations)
    {
        private readonly Dictionary<Guid, AnalysisSource> _sources = [];
        private readonly HashSet<Guid> _ids = [];

        public void Run()
        {
            CheckEnvelope();
            CheckSources();
            CheckSections();
            CheckConflicts();
        }

        private void Fail(string message) => violations.Add(message);

        private void CheckEnvelope()
        {
            if (content.AnalysisId != context.AnalysisId)
            {
                Fail("analysisId: diverso dall'analisi del server.");
            }

            if (content.ProjectId != context.ProjectId)
            {
                Fail("projectId: diverso dal progetto del server.");
            }

            if (content.SourceCutoffSequence < 1)
            {
                Fail("sourceCutoffSequence: minimo 1.");
            }

            if (content.SourceCutoffSequence != context.SourceCutoffSequence)
            {
                Fail("sourceCutoffSequence: diverso dal cutoff del server.");
            }

            var expected = actor == AnalysisActorKind.PmConfirm ? AnalysisContentState.PmConfirmed : AnalysisContentState.Unvalidated;
            if (content.ValidationState != expected)
            {
                Fail($"validationState: atteso {expected} per questo attore.");
            }
        }

        private void CheckSources()
        {
            for (var i = 0; i < content.Sources.Count; i++)
            {
                var source = content.Sources[i];
                var path = $"sources[{i}]";

                if (!_ids.Add(source.Id) || !_sources.TryAdd(source.Id, source))
                {
                    Fail($"{path}.id: duplicato.");
                }

                switch (source.Kind)
                {
                    case AnalysisSourceKind.Message:
                        CheckMessageSource(source, path);
                        break;
                    case AnalysisSourceKind.PmRevision:
                        CheckPmRevisionSource(source, path);
                        break;
                    default:
                        Fail($"{path}.kind: sconosciuto.");
                        break;
                }
            }
        }

        private void CheckMessageSource(AnalysisSource source, string path)
        {
            if (source.MessageId is null || source.MessageRole is null || source.RevisionId is not null)
            {
                Fail($"{path}: una fonte Message richiede messageId e messageRole e revisionId=null.");
                return;
            }

            var message = context.Messages.FirstOrDefault(m => m.Id == source.MessageId);
            if (message is null || message.ProjectId != context.ProjectId)
            {
                Fail($"{path}.messageId: messaggio inesistente nel progetto.");
            }
            else if (message.Sequence > context.SourceCutoffSequence)
            {
                Fail($"{path}.messageId: messaggio oltre il cutoff.");
            }
            else if (message.Role != source.MessageRole)
            {
                Fail($"{path}.messageRole: incoerente con il messaggio.");
            }
        }

        private void CheckPmRevisionSource(AnalysisSource source, string path)
        {
            if (actor == AnalysisActorKind.Ai)
            {
                Fail($"{path}: l'AI non può creare fonti PmRevision.");
                return;
            }

            if (source.MessageId is not null || source.MessageRole is not null || source.RevisionId is null)
            {
                Fail($"{path}: una fonte PmRevision richiede revisionId e messageId/messageRole=null.");
                return;
            }

            if (!context.PmRevisionIds.Contains(source.RevisionId.Value))
            {
                Fail($"{path}.revisionId: non è una revisione PM di questa analisi.");
            }
        }

        private void CheckSections()
        {
            var c = content;
            Items(c.Goals, "goals");
            Items(c.ContextAndScope.Overview, "contextAndScope.overview");
            Items(c.ContextAndScope.Included, "contextAndScope.included");
            Items(c.ContextAndScope.Excluded, "contextAndScope.excluded");
            Items(c.Requirements, "requirements");
            Items(c.OperationalFlows, "operationalFlows");
            Items(c.Constraints, "constraints");
            Items(c.Dependencies, "dependencies");
            Items(c.Assumptions, "assumptions", mustBeAssumed: true);
            Items(c.OpenQuestions, "openQuestions", evidenceOptional: true);
        }

        private void Items(IReadOnlyList<AnalysisItem> items, string section, bool mustBeAssumed = false, bool evidenceOptional = false)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var path = $"{section}[{i}]";
                CheckItem(item, path, evidenceOptional);

                if (mustBeAssumed && item.Classification != AnalysisClassification.Assumed)
                {
                    Fail($"{path}.classification: gli elementi di assumptions sono Assumed.");
                }
            }
        }

        private void CheckItem(AnalysisItem item, string path, bool evidenceOptional, bool evidenceRequired = false)
        {
            if (!_ids.Add(item.Id))
            {
                Fail($"{path}.id: duplicato.");
            }

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                Fail($"{path}.text: vuoto o solo spazi.");
            }

            var resolved = CheckRefs(item.EvidenceRefs, $"{path}.evidenceRefs");

            // Un fatto (Declared) o un'alternativa di conflitto richiede evidenza user o PmRevision: assistant è contesto.
            if ((evidenceRequired || (item.Classification == AnalysisClassification.Declared && !evidenceOptional))
                && !resolved.Any(IsFactualSupport))
            {
                Fail($"{path}.evidenceRefs: richiesta evidenza di un messaggio utente o di una revisione PM.");
            }
        }

        private static bool IsFactualSupport(AnalysisSource s) =>
            s.Kind == AnalysisSourceKind.PmRevision || s.MessageRole == AnalysisMessageRole.User;

        private List<AnalysisSource> CheckRefs(IReadOnlyList<Guid> refs, string path)
        {
            var resolved = new List<AnalysisSource>();
            if (refs.Distinct().Count() != refs.Count)
            {
                Fail($"{path}: riferimenti duplicati.");
            }

            foreach (var reference in refs)
            {
                if (_sources.TryGetValue(reference, out var source))
                {
                    resolved.Add(source);
                }
                else
                {
                    Fail($"{path}: riferimento {reference} non risolto tra le fonti.");
                }
            }

            return resolved;
        }

        private void CheckConflicts()
        {
            for (var i = 0; i < content.Conflicts.Count; i++)
            {
                var conflict = content.Conflicts[i];
                var path = $"conflicts[{i}]";

                if (!_ids.Add(conflict.Id))
                {
                    Fail($"{path}.id: duplicato.");
                }

                if (string.IsNullOrWhiteSpace(conflict.Description))
                {
                    Fail($"{path}.description: vuota o solo spazi.");
                }

                if (conflict.Alternatives.Count < 2)
                {
                    Fail($"{path}.alternatives: servono almeno due alternative.");
                }

                for (var a = 0; a < conflict.Alternatives.Count; a++)
                {
                    CheckItem(conflict.Alternatives[a], $"{path}.alternatives[{a}]", evidenceOptional: false, evidenceRequired: true);
                }

                CheckResolution(conflict, path);
            }
        }

        private void CheckResolution(AnalysisConflict conflict, string path)
        {
            var resolution = conflict.Resolution;

            if (conflict.Status == AnalysisConflictStatus.Open)
            {
                if (resolution is not null)
                {
                    Fail($"{path}.resolution: un conflitto Open richiede resolution=null.");
                }

                return;
            }

            if (actor == AnalysisActorKind.Ai)
            {
                Fail($"{path}.status: l'AI non può risolvere conflitti.");
                return;
            }

            if (resolution is null)
            {
                Fail($"{path}.resolution: un conflitto Resolved richiede la risoluzione.");
                return;
            }

            if (resolution.SelectedAlternativeIds.Count < 1 || resolution.SelectedAlternativeIds.Distinct().Count() != resolution.SelectedAlternativeIds.Count)
            {
                Fail($"{path}.resolution.selectedAlternativeIds: almeno una scelta, senza duplicati.");
            }

            var alternativeIds = conflict.Alternatives.Select(a => a.Id).ToHashSet();
            if (resolution.SelectedAlternativeIds.Any(id => !alternativeIds.Contains(id)))
            {
                Fail($"{path}.resolution.selectedAlternativeIds: la scelta deve essere fra le alternative del conflitto.");
            }

            if (string.IsNullOrWhiteSpace(resolution.Reason))
            {
                Fail($"{path}.resolution.reason: motivo vuoto o solo spazi.");
            }

            var evidence = CheckRefs(resolution.EvidenceRefs, $"{path}.resolution.evidenceRefs");
            if (!evidence.Any(s => s.Kind == AnalysisSourceKind.PmRevision))
            {
                Fail($"{path}.resolution.evidenceRefs: la risoluzione deve citare la modifica PM (fonte PmRevision).");
            }
        }
    }
}
