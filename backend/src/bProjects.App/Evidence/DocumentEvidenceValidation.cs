using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.Conversations;

namespace bProjects.App.Evidence;

// Validazione deterministica dell'uscita `evidence-1.1.0` (contracts/ai-workflow.md §2.1/§2.3). Calcola offset UTF-16 [start,end) sul
// CanonicalText immutabile (documenti) o sul messaggio originale, rifiuta ciò che non è verificabile e non ripara nulla: schema o fonti
// invalidi sono permanenti. Nessun tetto silenzioso copiato dalla 001: l'unità va suddivisa dal planner prima della chiamata.

/// <summary>Input fidato dell'unità, letto dal database: ciò che il modello non può definire da solo.</summary>
public sealed class DocumentExtractionContext
{
    public DocumentExtractionContext(
        Guid projectId, Guid preparationId, int unitOrdinal, DocumentSourceMap map, IReadOnlyList<ExtractionMessage> messages,
        IReadOnlySet<Guid> ownedMessageIds, IReadOnlyList<PriorEvidence> priorEvidences)
    {
        foreach (var range in map.Ranges)
        {
            if (range.PreparationId != preparationId)
            {
                throw new ArgumentException($"L'intervallo «{range.RangeKey}» appartiene a un'altra preparazione.", nameof(map));
            }
        }

        ProjectId = projectId;
        PreparationId = preparationId;
        UnitOrdinal = unitOrdinal;
        Map = map;
        Messages = messages;
        OwnedMessageIds = ownedMessageIds;
        PriorEvidences = priorEvidences;
    }

    public Guid ProjectId { get; }
    public Guid PreparationId { get; }

    /// <summary>Ordinale dell'unità nella preparazione: prefisso della chiave globale di versione.</summary>
    public int UnitOrdinal { get; }
    public DocumentSourceMap Map { get; }

    /// <summary>Messaggi forniti all'unità (mai oltre il cutoff ammesso).</summary>
    public IReadOnlyList<ExtractionMessage> Messages { get; }

    /// <summary>Messaggi PM di cui l'unità è proprietaria (Primary ammessa); gli altri sono solo Context o Referent.</summary>
    public IReadOnlySet<Guid> OwnedMessageIds { get; }

    /// <summary>Evidenze fornite dal server con alias stabile P1…Pn: bersagli ammessi delle relazioni.</summary>
    public IReadOnlyList<PriorEvidence> PriorEvidences { get; }
}

public sealed record ValidatedDocumentEvidence(
    int Ordinal, string Key, string GlobalKey, EvidenceKind Kind, EvidencePolarity Polarity, EvidenceCertainty Certainty, string Statement,
    DocumentEvidenceContent Content);

/// <summary>Esito: rifiutato = permanente (nessuna riparazione né retry dello stesso output); le relazioni usano le chiavi locali dell'unità.</summary>
public sealed record DocumentValidationResult(
    bool Accepted,
    bool Permanent,
    string? Code,
    IReadOnlyList<string> Violations,
    IReadOnlyList<ValidatedDocumentEvidence> Evidences,
    IReadOnlyList<ValidatedRelation> Relations,
    string? NoEvidenceReason,
    IReadOnlyList<string>? OtherWithoutRationaleKeys = null);

public static partial class DocumentEvidenceValidator
{
    [GeneratedRegex(@"\Ae[1-9][0-9]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static DocumentValidationResult ValidateText(string text, DocumentExtractionContext context, bool finishedByLength = false)
    {
        if (finishedByLength)
        {
            return Rejected("output troncato dal limite di lunghezza: incompleto, non accettabile anche se il JSON risulta chiuso");
        }

        DocumentExtractionOutput output;
        try
        {
            output = DocumentEvidenceCodec.DecodeOutput(text.Trim());
        }
        catch (JsonException ex)
        {
            return Rejected($"JSON non valido per evidence-1.1.0: {Redact(ex.Message)}");
        }

        return Validate(output, context);
    }

    public static DocumentValidationResult Validate(DocumentExtractionOutput output, DocumentExtractionContext context)
    {
        var violations = new List<string>();
        var evidences = new List<ValidatedDocumentEvidence>();
        var relations = new List<ValidatedRelation>();
        var items = output.Evidences ?? [];

        if (output.SchemaVersion != DocumentEvidenceContent.SchemaVersionValue)
        {
            violations.Add($"schemaVersion deve essere «{DocumentEvidenceContent.SchemaVersionValue}»");
        }

        var reason = Clean(output.NoEvidenceReason);
        if (items.Count == 0 && reason is null)
        {
            violations.Add("zero evidenze senza noEvidenceReason: un'unità senza fatti va motivata esplicitamente");
        }
        else if (items.Count > 0 && output.NoEvidenceReason is not null)
        {
            violations.Add("noEvidenceReason ammesso solo con zero evidenze");
        }

        var otherWithoutRationale = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in items)
        {
            if (e is null || e.Key is null || !KeyPattern().IsMatch(e.Key))
            {
                violations.Add($"chiave di evidenza non valida «{e?.Key}»");
            }
            else if (!keys.Add(e.Key))
            {
                violations.Add($"chiave duplicata {e.Key}");
            }
        }

        var priors = context.PriorEvidences.ToDictionary(p => p.Alias, StringComparer.Ordinal);
        var ordinal = 0;
        foreach (var e in items)
        {
            if (e is null)
            {
                continue;
            }

            var where = $"evidenza {e.Key}";
            var statement = Clean(e.Statement);
            if (statement is null)
            {
                violations.Add($"{where}: statement vuoto");
            }

            if (!Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.Polarity) || !Enum.IsDefined(e.Certainty))
            {
                violations.Add($"{where}: tipo, polarità o certezza non validi");
            }

            var rationale = Clean(e.KindRationale);
            if (e.Kind == EvidenceKind.Other && rationale is null)
            {
                violations.Add($"{where}: kind Other richiede kindRationale");
                otherWithoutRationale.Add(e.Key!);
            }

            var qualifiers = new List<DocumentEvidenceQualifier>();
            foreach (var q in e.Qualifiers ?? [])
            {
                var text = Clean(q?.Text);
                if (q is null || text is null || !Enum.IsDefined(q.Type))
                {
                    violations.Add($"{where}: qualificatore vuoto o di tipo non valido");
                    continue;
                }

                qualifiers.Add(new DocumentEvidenceQualifier { Type = q.Type, Text = text });
            }

            var sources = ValidateSources(e, context, where, violations);
            if (!sources.Any(s => s.Source.Role == EvidenceSourceRole.Primary))
            {
                violations.Add($"{where}: manca una fonte Primary posseduta dall'unità (un header replicato come contesto non genera evidenza)");
            }

            CheckGroups(sources, where, violations);

            var seen = new HashSet<(string, EvidenceRelationType)>();
            foreach (var r in e.Relations ?? [])
            {
                if (r is null || string.IsNullOrWhiteSpace(r.Target))
                {
                    violations.Add($"{where}: relazione senza bersaglio");
                    continue;
                }

                if (!Enum.IsDefined(r.Type) || !Enum.IsDefined(r.Basis))
                {
                    violations.Add($"{where}: relazione di tipo o base non validi");
                    continue;
                }

                if (r.Target == e.Key)
                {
                    violations.Add($"{where}: relazione verso sé stessa");
                    continue;
                }

                if (!seen.Add((r.Target, r.Type)))
                {
                    violations.Add($"{where}: relazione {r.Type} verso {r.Target} duplicata");
                    continue;
                }

                if (keys.Contains(r.Target))
                {
                    relations.Add(new ValidatedRelation(e.Key, r.Target, null, r.Type, r.Basis, Clean(r.Rationale)));
                }
                else if (priors.TryGetValue(r.Target, out var prior))
                {
                    relations.Add(new ValidatedRelation(e.Key, null, prior, r.Type, r.Basis, Clean(r.Rationale)));
                }
                else
                {
                    violations.Add($"{where}: relazione verso «{r.Target}» inesistente (né chiave di questa uscita né alias fornito dal server)");
                }
            }

            evidences.Add(new ValidatedDocumentEvidence(
                ++ordinal, e.Key, $"{context.UnitOrdinal}{e.Key}", e.Kind, e.Polarity, e.Certainty, statement ?? "",
                new DocumentEvidenceContent
                {
                    SchemaVersion = DocumentEvidenceContent.SchemaVersionValue,
                    KindRationale = rationale,
                    Structure = new DocumentEvidenceStructure
                    {
                        Subject = Clean(e.Subject), Action = Clean(e.Action), Object = Clean(e.Object), Scope = Clean(e.Scope),
                        Purpose = Clean(e.Purpose), Instrument = Clean(e.Instrument), Channel = Clean(e.Channel), Recipient = Clean(e.Recipient),
                    },
                    Qualifiers = qualifiers,
                    Sources = [.. sources.Select(s => s.Persisted)],
                }));
        }

        return violations.Count == 0
            ? new(true, false, null, [], evidences, relations, items.Count == 0 ? reason : null)
            : new(false, true, EvidenceExtractionValidator.InvalidAgentOutput, violations, [], [], null,
                // Segnale tipizzato del retry guidato (T134): SOLO se ogni violazione è un Other senza motivazione; qualunque altra violazione lo annulla.
                violations.Count == otherWithoutRationale.Count ? otherWithoutRationale : null);
    }

    private sealed record Checked(CandidateSource Source, PersistedSource Persisted, Guid? DocumentVersionId);

    private static List<Checked> ValidateSources(DocumentExtractedEvidence evidence, DocumentExtractionContext context, string where, List<string> violations)
    {
        var accepted = new List<Checked>();
        var sourceIds = new HashSet<Guid>();
        if (evidence.Sources is null || evidence.Sources.Count == 0)
        {
            violations.Add($"{where}: nessuna fonte");
            return accepted;
        }

        foreach (var source in evidence.Sources)
        {
            if (source is null)
            {
                violations.Add($"{where}: fonte nulla");
                continue;
            }

            if (Locate(source, evidence.Key, context, where, violations) is not { } located)
            {
                continue;
            }

            if (!sourceIds.Add(located.Persisted.SourceId))
            {
                violations.Add($"{where}: fonte duplicata (stesso intervallo citato due volte)");
                continue;
            }

            accepted.Add(located);
        }

        return accepted;
    }

    private static Checked? Locate(CandidateSource source, string key, DocumentExtractionContext context, string where, List<string> violations)
    {
        var ok = true;
        if (!Enum.IsDefined(source.Role))
        {
            violations.Add($"{where}: ruolo della fonte non valido");
            ok = false;
        }

        if (source.Part < 0)
        {
            violations.Add($"{where}: part negativa");
            ok = false;
        }

        if (string.IsNullOrWhiteSpace(source.CitationGroupKey))
        {
            violations.Add($"{where}: citationGroupKey vuota");
            ok = false;
        }

        if (source.Occurrence < 1)
        {
            violations.Add($"{where}: occurrence deve essere almeno 1");
            ok = false;
        }

        if (string.IsNullOrWhiteSpace(source.Quote))
        {
            violations.Add($"{where}: citazione vuota");
            return null;
        }

        if (HasLoneSurrogate(source.Quote))
        {
            violations.Add($"{where}: citazione con surrogato UTF-16 isolato (testo non valido, non viene troncata né corretta)");
            return null;
        }

        if (!ok)
        {
            return null;
        }

        var group = string.IsNullOrWhiteSpace(source.CitationGroupKey) ? "" : source.CitationGroupKey;
        var groupId = DeterministicIds.CitationGroupId(context.PreparationId, context.UnitOrdinal, key, group);
        return source switch
        {
            DocumentPassageCandidateSource passage => LocatePassage(passage, groupId, context, where, violations),
            MessageCandidateSource message => LocateMessage(message, groupId, context, where, violations),
            _ => Fail<Checked>($"{where}: tipo di fonte sconosciuto", violations),
        };
    }

    private static Checked? LocatePassage(DocumentPassageCandidateSource source, Guid groupId, DocumentExtractionContext context, string where, List<string> violations)
    {
        if (string.IsNullOrWhiteSpace(source.RangeKey) || !context.Map.TryGet(source.RangeKey, out var range))
        {
            violations.Add($"{where}: rangeKey «{source.RangeKey}» fuori dal manifest dell'unità (intervallo non ammesso)");
            return null;
        }

        if (source.Role == EvidenceSourceRole.Referent)
        {
            violations.Add($"{where}: una fonte documentale è Primary o Context");
            return null;
        }

        if (source.Role == EvidenceSourceRole.Primary && !range.Owned)
        {
            violations.Add($"{where}: fonte Primary su un intervallo non proprietario dell'unità ({source.RangeKey}): solo contesto");
            return null;
        }

        // L'occorrenza si conta nel solo intervallo assegnato, non nel resto del blocco. Corrispondenza esatta: niente normalizzazione.
        var text = range.Text;
        var starts = new List<int>();
        for (var i = text.IndexOf(source.Quote.AsSpan(), StringComparison.Ordinal); i >= 0;)
        {
            starts.Add(i);
            var next = text[(i + 1)..].IndexOf(source.Quote.AsSpan(), StringComparison.Ordinal);
            i = next < 0 ? -1 : i + 1 + next;
        }

        if (starts.Count == 0)
        {
            violations.Add($"{where}: citazione non presente nell'intervallo {source.RangeKey}: «{Shorten(source.Quote)}»");
            return null;
        }

        if (source.Occurrence > starts.Count)
        {
            violations.Add($"{where}: occurrence {source.Occurrence} oltre le {starts.Count} occorrenze nell'intervallo {source.RangeKey}");
            return null;
        }

        var start = range.Start + starts[source.Occurrence - 1];
        var end = start + source.Quote.Length;
        var persisted = new PersistedSource
        {
            SourceId = DeterministicIds.DocumentSourceId(range.ArtifactId, range.BlockId, start, end),
            Kind = EvidenceSourceKind.DocumentPassage, Role = source.Role, CitationGroupId = groupId, Part = source.Part,
            MessageId = null, Sequence = null,
            DocumentId = range.DocumentId, DocumentVersionId = range.DocumentVersionId, PreparationId = range.PreparationId,
            ArtifactId = range.ArtifactId, BlockId = range.BlockId, Start = start, End = end, Quote = range.BlockText[start..end],
        };
        return new Checked(source, persisted, range.DocumentVersionId);
    }

    private static Checked? LocateMessage(MessageCandidateSource source, Guid groupId, DocumentExtractionContext context, string where, List<string> violations)
    {
        var message = context.Messages.FirstOrDefault(m => m.Id == source.MessageId);
        if (message is null)
        {
            violations.Add($"{where}: messaggio non fornito all'unità ({source.MessageId})");
            return null;
        }

        if (message.Role == MessageRole.Assistant && source.Role != EvidenceSourceRole.Referent)
        {
            violations.Add($"{where}: assistant (sequenza {message.Sequence}): solo Referent, mai {source.Role}");
            return null;
        }

        if (message.Role != MessageRole.Assistant && source.Role == EvidenceSourceRole.Referent)
        {
            violations.Add($"{where}: Referent ammesso solo su un messaggio assistant");
            return null;
        }

        if (source.Role == EvidenceSourceRole.Primary && !context.OwnedMessageIds.Contains(message.Id))
        {
            violations.Add($"{where}: fonte Primary su un messaggio non proprietario dell'unità (sequenza {message.Sequence})");
            return null;
        }

        var matches = EvidenceExtractionValidator.ExactMatches(message.Body, source.Quote);
        if (matches.Count == 0)
        {
            matches = EvidenceExtractionValidator.NormalizedMatches(message.Body, source.Quote);
        }

        if (matches.Count == 0)
        {
            violations.Add($"{where}: citazione non presente nel messaggio di sequenza {message.Sequence}: «{Shorten(source.Quote)}»");
            return null;
        }

        if (source.Occurrence > matches.Count)
        {
            violations.Add($"{where}: occurrence {source.Occurrence} oltre le {matches.Count} occorrenze della citazione");
            return null;
        }

        var (start, end) = matches[source.Occurrence - 1];
        var persisted = new PersistedSource
        {
            SourceId = DeterministicIds.MessageSourceId(message.Id, start, end),
            Kind = EvidenceSourceKind.Message, Role = source.Role, CitationGroupId = groupId, Part = source.Part,
            MessageId = message.Id, Sequence = message.Sequence,
            DocumentId = null, DocumentVersionId = null, PreparationId = null, ArtifactId = null, BlockId = null,
            Start = start, End = end, Quote = message.Body[start..end],
        };
        return new Checked(source, persisted, null);
    }

    /// <summary>Gruppi di citazione: part univoche, contigue da zero ed elencate in ordine; un gruppo non mescola origini né versioni.</summary>
    private static void CheckGroups(List<Checked> sources, string where, List<string> violations)
    {
        foreach (var group in sources.GroupBy(s => s.Source.CitationGroupKey, StringComparer.Ordinal))
        {
            var parts = group.Select(s => s.Source.Part).ToList();
            if (parts.Distinct().Count() != parts.Count)
            {
                violations.Add($"{where}: gruppo «{group.Key}»: part duplicata");
            }
            else if (!parts.Order().SequenceEqual(Enumerable.Range(0, parts.Count)))
            {
                violations.Add($"{where}: gruppo «{group.Key}»: le part devono essere contigue da zero (ricevute [{string.Join(',', parts)}], ruoli [{string.Join(',', group.Select(g => g.Source.Role))}]; fonti dell'evidenza: [{string.Join(' ', sources.Select(x => $"{x.Source.Role}/p{x.Source.Part}/g{sources.Select(y => y.Source.CitationGroupKey).Distinct().ToList().IndexOf(x.Source.CitationGroupKey)}"))}])");
            }
            else if (!parts.SequenceEqual(parts.Order()))
            {
                violations.Add($"{where}: gruppo «{group.Key}»: fonti elencate fuori ordine di part (ricevute [{string.Join(',', parts)}])");
            }

            if (group.Select(s => s.Persisted.Kind).Distinct().Count() > 1)
            {
                violations.Add($"{where}: gruppo «{group.Key}» mescola messaggio e documento");
            }

            if (group.Where(s => s.DocumentVersionId is not null).Select(s => s.DocumentVersionId).Distinct().Count() > 1)
            {
                violations.Add($"{where}: gruppo «{group.Key}» compone versioni diverse del documento");
            }
        }
    }

    private static bool HasLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static T? Fail<T>(string violation, List<string> violations)
        where T : class
    {
        violations.Add(violation);
        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Shorten(string value) => value.Length <= 80 ? value : value[..80] + "…";

    private static string Redact(string message) => message.Length <= 200 ? message : message[..200];

    private static DocumentValidationResult Rejected(string violation) =>
        new(false, true, EvidenceExtractionValidator.InvalidAgentOutput, [violation], [], [], null);
}
