using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Evidence;

/// <summary>Evidenza persistita letta dal database, con il contenuto deserializzato.</summary>
public sealed record EvidenceSnapshot(
    Guid Id,
    Guid VersionId,
    Guid MessageId,
    long MessageSequence,
    int Ordinal,
    string Key,
    EvidenceKind Kind,
    EvidencePolarity Polarity,
    EvidenceCertainty Certainty,
    string Statement,
    EvidenceContent Content)
{
    public static EvidenceSnapshot From(EvidenceItem item, long sequence) => new(
        item.Id, item.VersionId,
        item.MessageId ?? throw new InvalidOperationException("Snapshot legacy: l'evidenza non ha un messaggio di origine (ramo documentale: viste 1.1)."), sequence, item.Ordinal, item.Key, item.Kind, item.Polarity, item.Certainty, item.Statement,
        EvidenceJson.Deserialize<EvidenceContent>(item.Content));
}

public enum DiffStatus
{
    Added,
    Removed,
    Unchanged,
    Modified,
    Ambiguous,
}

public sealed record FieldChange(string Field, string? From, string? To);

public sealed record DiffEntry(DiffStatus Status, EvidenceSnapshot? From, EvidenceSnapshot? To, IReadOnlyList<FieldChange> Changes, IReadOnlyList<Guid> Candidates, double Overlap);

public sealed record VersionDiff(IReadOnlyList<DiffEntry> Entries)
{
    public int Count(DiffStatus status) => Entries.Count(e => e.Status == status);
}

/// <summary>
/// Confronto fra due versioni dello stesso messaggio (contracts/http-api.md §Registro). L'identità di due evidenze è ancorata alla
/// sovrapposizione delle loro citazioni primarie sullo stesso originale (IoU dei caratteri ≥ <see cref="MinOverlap"/>, migliore
/// reciproco e unico), mai a parole comuni dell'enunciato. Gli abbinamenti non univoci sono esposti come ambigui: nessun automatismo
/// li risolve né approva modifiche per conto del PM.
/// </summary>
public static class EvidenceVersionComparer
{
    public const double MinOverlap = 0.5;

    /// <summary>Abbinamenti univoci from → to; gli altri restano fuori (aggiunte, rimozioni o ambiguità).</summary>
    public static IReadOnlyDictionary<Guid, Guid> Match(IReadOnlyList<EvidenceSnapshot> from, IReadOnlyList<EvidenceSnapshot> to)
    {
        var scores = Scores(from, to);
        var result = new Dictionary<Guid, Guid>();
        foreach (var a in from)
        {
            var best = Best(scores, a.Id, to.Select(b => b.Id), (x, y) => (x, y));
            if (best.Count == 1)
            {
                var b = best[0];
                var back = Best(scores, b, from.Select(x => x.Id), (x, y) => (y, x));
                if (back.Count == 1 && back[0] == a.Id)
                {
                    result[a.Id] = b;
                }
            }
        }

        return result;
    }

    public static VersionDiff Compare(
        IReadOnlyList<EvidenceSnapshot> from,
        IReadOnlyList<EvidenceSnapshot> to,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>>? relations = null)
    {
        var scores = Scores(from, to);
        var matches = Match(from, to);
        var matchedTo = matches.Values.ToHashSet();
        var involved = new HashSet<Guid>();
        var entries = new List<DiffEntry>();
        foreach (var a in from.OrderBy(e => e.Ordinal))
        {
            if (matches.TryGetValue(a.Id, out var bId))
            {
                var b = to.Single(e => e.Id == bId);
                var changes = Changes(a, b, relations);
                entries.Add(new(changes.Count == 0 ? DiffStatus.Unchanged : DiffStatus.Modified, a, b, changes, [], Round(scores[(a.Id, b.Id)])));
                continue;
            }

            var candidates = to.Where(b => !matchedTo.Contains(b.Id) && scores.GetValueOrDefault((a.Id, b.Id)) >= MinOverlap)
                .Select(b => b.Id).ToList();
            if (candidates.Count > 0)
            {
                involved.UnionWith(candidates);
                entries.Add(new(DiffStatus.Ambiguous, a, null, [], candidates, Round(candidates.Max(c => scores[(a.Id, c)]))));
            }
            else
            {
                entries.Add(new(DiffStatus.Removed, a, null, [], [], 0));
            }
        }

        foreach (var b in to.OrderBy(e => e.Ordinal).Where(b => !matchedTo.Contains(b.Id) && !involved.Contains(b.Id)))
        {
            entries.Add(new(DiffStatus.Added, null, b, [], [], 0));
        }

        return new(entries);
    }

    private static Dictionary<(Guid, Guid), double> Scores(IReadOnlyList<EvidenceSnapshot> from, IReadOnlyList<EvidenceSnapshot> to)
    {
        var scores = new Dictionary<(Guid, Guid), double>();
        foreach (var a in from)
        {
            var spansA = Primary(a);
            foreach (var b in to)
            {
                var overlap = Overlap(spansA, Primary(b));
                if (overlap >= MinOverlap)
                {
                    // A parità di sovrapposizione preferisce lo stesso tipo: entrambe proprietà strutturali, non lessicali.
                    scores[(a.Id, b.Id)] = overlap + (a.Kind == b.Kind ? 0.001 : 0);
                }
            }
        }

        return scores;
    }

    private static List<Guid> Best(
        Dictionary<(Guid, Guid), double> scores, Guid id, IEnumerable<Guid> others, Func<Guid, Guid, (Guid, Guid)> key)
    {
        var list = others.Select(o => (o, s: scores.GetValueOrDefault(key(id, o)))).Where(x => x.s >= MinOverlap).ToList();
        if (list.Count == 0)
        {
            return [];
        }

        var max = list.Max(x => x.s);
        return [.. list.Where(x => Math.Abs(x.s - max) < 1e-9).Select(x => x.o)];
    }

    private static List<(int Start, int End)> Primary(EvidenceSnapshot e) =>
        [.. e.Content.Sources.Where(s => s.Role == EvidenceSourceRole.Primary && s.MessageId == e.MessageId).Select(s => (s.Start, s.End))];

    /// <summary>Intersection over union dei caratteri coperti dalle citazioni primarie (intervalli semiaperti).</summary>
    public static double Overlap(IReadOnlyList<(int Start, int End)> a, IReadOnlyList<(int Start, int End)> b)
    {
        var ua = Union(a);
        var ub = Union(b);
        var intersection = 0;
        foreach (var x in ua)
        {
            foreach (var y in ub)
            {
                intersection += Math.Max(0, Math.Min(x.End, y.End) - Math.Max(x.Start, y.Start));
            }
        }

        var union = ua.Sum(x => x.End - x.Start) + ub.Sum(y => y.End - y.Start) - intersection;
        return union <= 0 ? 0 : (double)intersection / union;
    }

    private static List<(int Start, int End)> Union(IReadOnlyList<(int Start, int End)> spans)
    {
        var result = new List<(int Start, int End)>();
        foreach (var s in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (result.Count > 0 && s.Start <= result[^1].End)
            {
                result[^1] = (result[^1].Start, Math.Max(result[^1].End, s.End));
            }
            else
            {
                result.Add(s);
            }
        }

        return result;
    }

    private static List<FieldChange> Changes(EvidenceSnapshot a, EvidenceSnapshot b, IReadOnlyDictionary<Guid, IReadOnlyList<string>>? relations)
    {
        var changes = new List<FieldChange>();
        void Check(string field, string? x, string? y)
        {
            if (!string.Equals(x, y, StringComparison.Ordinal))
            {
                changes.Add(new(field, x, y));
            }
        }

        Check("kind", a.Kind.ToString(), b.Kind.ToString());
        Check("polarity", a.Polarity.ToString(), b.Polarity.ToString());
        Check("certainty", a.Certainty.ToString(), b.Certainty.ToString());
        Check("statement", a.Statement, b.Statement);
        var (sa, sb) = (a.Content.Structure, b.Content.Structure);
        Check("subject", sa.Subject, sb.Subject);
        Check("action", sa.Action, sb.Action);
        Check("object", sa.Object, sb.Object);
        Check("scope", sa.Scope, sb.Scope);
        Check("purpose", sa.Purpose, sb.Purpose);
        Check("instrument", sa.Instrument, sb.Instrument);
        Check("channel", sa.Channel, sb.Channel);
        Check("recipient", sa.Recipient, sb.Recipient);
        Check("qualifiers", Qualifiers(a), Qualifiers(b));
        Check("sources", Sources(a), Sources(b));
        if (relations is not null)
        {
            Check("relations", Join(relations.GetValueOrDefault(a.Id)), Join(relations.GetValueOrDefault(b.Id)));
        }

        return changes;
    }

    private static string? Join(IReadOnlyList<string>? values) => values is null || values.Count == 0 ? null : string.Join("; ", values.Order(StringComparer.Ordinal));

    private static string? Qualifiers(EvidenceSnapshot e) =>
        e.Content.Qualifiers.Count == 0 ? null : string.Join("; ", e.Content.Qualifiers.Select(q => $"{q.Type}: {q.Text}").Order(StringComparer.Ordinal));

    private static string Sources(EvidenceSnapshot e) =>
        string.Join("; ", e.Content.Sources.Select(s => $"{s.Role} seq {s.Sequence} [{s.Start},{s.End}) «{s.Quote}»"));

    private static double Round(double value) => Math.Round(Math.Min(1, value), 3);
}

/// <summary>Versione selezionata per un messaggio PM entro il cutoff.</summary>
public sealed record SelectionItem(Guid MessageId, long Sequence, Guid VersionId, int Version, int EvidenceCount, string? NoEvidenceReason);

public sealed record ViewEvidence(
    string Alias,
    Guid EvidenceId,
    Guid MessageId,
    long Sequence,
    int Ordinal,
    EvidenceKind Kind,
    EvidencePolarity Polarity,
    EvidenceCertainty Certainty,
    string Statement,
    EvidenceContent Content);

public sealed record ViewRelation(string FromAlias, string ToAlias, EvidenceRelationType Type, EvidenceRelationBasis Basis, string? Rationale, bool Open, bool Remapped);

public sealed record StaleRelation(Guid RelationId, Guid FromEvidenceId, Guid ToEvidenceId, EvidenceRelationType Type, string Reason);

/// <summary>Relazione persistita con il messaggio e la versione del bersaglio.</summary>
public sealed record RelationRecord(
    Guid Id, Guid VersionId, Guid FromEvidenceId, Guid ToEvidenceId, Guid ToVersionId, Guid ToMessageId,
    EvidenceRelationType Type, EvidenceRelationBasis Basis, string? Rationale);

/// <summary>
/// Vista deterministica delle evidenze selezionate per un run (contracts/ai-workflow.md §10.6): alias E1…En in ordine di sequenza e
/// ordinale, relazioni asserite dalle sole versioni selezionate, bersagli in versioni non selezionate rimappati per abbinamento univoco
/// oppure dichiarati stale. Serializzata e congelata nel run; il suo hash identifica l'input del consolidamento.
/// </summary>
public sealed record EvidenceSelectionView(
    IReadOnlyList<SelectionItem> Items,
    IReadOnlyList<ViewEvidence> Evidences,
    IReadOnlyList<ViewRelation> Relations,
    IReadOnlyList<StaleRelation> StaleRelations)
{
    public const string SchemaVersion = "evidence-view-1";

    public static EvidenceSelectionView Build(
        IReadOnlyList<SelectionItem> items,
        IReadOnlyList<EvidenceSnapshot> selected,
        IReadOnlyList<RelationRecord> relations,
        IReadOnlyDictionary<Guid, IReadOnlyList<EvidenceSnapshot>> otherVersions)
    {
        var ordered = selected.OrderBy(e => e.MessageSequence).ThenBy(e => e.Ordinal).ToList();
        var aliases = new Dictionary<Guid, string>();
        var evidences = new List<ViewEvidence>();
        foreach (var e in ordered)
        {
            var alias = $"E{evidences.Count + 1}";
            aliases[e.Id] = alias;
            evidences.Add(new(alias, e.Id, e.MessageId, e.MessageSequence, e.Ordinal, e.Kind, e.Polarity, e.Certainty, e.Statement, e.Content));
        }

        var selectedVersions = items.Select(i => i.VersionId).ToHashSet();
        var selectedByMessage = ordered.GroupBy(e => e.MessageId).ToDictionary(g => g.Key, g => (IReadOnlyList<EvidenceSnapshot>)[.. g]);
        var resolved = new List<(RelationRecord Relation, string From, string To, bool Remapped)>();
        var stale = new List<StaleRelation>();
        foreach (var r in relations.Where(r => selectedVersions.Contains(r.VersionId)).OrderBy(r => aliases.GetValueOrDefault(r.FromEvidenceId)).ThenBy(r => r.Id))
        {
            if (!aliases.TryGetValue(r.FromEvidenceId, out var from))
            {
                continue;
            }

            if (aliases.TryGetValue(r.ToEvidenceId, out var to))
            {
                resolved.Add((r, from, to, false));
                continue;
            }

            if (!selectedByMessage.TryGetValue(r.ToMessageId, out var current) || !otherVersions.TryGetValue(r.ToVersionId, out var old))
            {
                stale.Add(new(r.Id, r.FromEvidenceId, r.ToEvidenceId, r.Type, "il messaggio del bersaglio non ha evidenze nella selezione"));
                continue;
            }

            if (EvidenceVersionComparer.Match(old, current).TryGetValue(r.ToEvidenceId, out var mapped) && aliases.TryGetValue(mapped, out var remapped))
            {
                resolved.Add((r, from, remapped, true));
            }
            else
            {
                stale.Add(new(r.Id, r.FromEvidenceId, r.ToEvidenceId, r.Type, "bersaglio in una versione non selezionata senza abbinamento univoco"));
            }
        }

        // Un'alternativa o una contraddizione resta aperta finché un'evidenza Decision dichiarata dal PM non la rettifica o sostituisce.
        var byAlias = evidences.ToDictionary(e => e.Alias);
        var settled = resolved
            .Where(x => x.Relation.Basis == EvidenceRelationBasis.Declared
                && x.Relation.Type is EvidenceRelationType.Corrects or EvidenceRelationType.Supersedes
                && byAlias[x.From].Kind == EvidenceKind.Decision && byAlias[x.From].Certainty == EvidenceCertainty.Declared)
            .Select(x => x.To)
            .ToHashSet();
        var view = resolved
            .Select(x => new ViewRelation(
                x.From, x.To, x.Relation.Type, x.Relation.Basis, x.Relation.Rationale,
                Open: x.Relation.Type is EvidenceRelationType.AlternativeTo or EvidenceRelationType.Contradicts
                    && !settled.Contains(x.From) && !settled.Contains(x.To),
                x.Remapped))
            .ToList();

        return new([.. items.OrderBy(i => i.Sequence)], evidences, view, stale);
    }

    public string Serialize() => EvidenceJson.Serialize(this);

    public static EvidenceSelectionView Deserialize(string json) => EvidenceJson.Deserialize<EvidenceSelectionView>(json);

    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize())));

    /// <summary>Alias delle evidenze ammessi in <c>evidenceKeys</c> dello schema del run.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Aliases => [.. Evidences.Select(e => e.Alias)];

    /// <summary>Testo per il consolidamento: compatto, stabile, senza id interni delle evidenze (gli alias bastano al modello).</summary>
    public string ToPromptText() => ToPromptText(null, null);

    /// <summary>
    /// Come <see cref="ToPromptText()"/> per una parte del consolidamento incrementale: le sole evidenze di <paramref name="aliases"/>,
    /// le relazioni la cui evidenza più recente è fra queste (l'altra è già stata vista in un passo precedente o in questo) e i
    /// messaggi senza evidenze di <paramref name="sequences"/>. Null = tutto. Ogni evidenza resta intera: nessun testo è troncato.
    /// </summary>
    public string ToPromptText(IReadOnlyCollection<string>? aliases, IReadOnlyCollection<long>? sequences)
    {
        var text = new StringBuilder()
            .AppendLine("Registro delle evidenze selezionate per questa analisi (versioni congelate). Ogni evidenza è un'interpretazione")
            .AppendLine("strutturata della frase PM citata, che resta l'autorità; usala per non perdere fatti, qualificatori, negazioni e legami.");
        foreach (var e in Evidences.Where(e => aliases is null || aliases.Contains(e.Alias)))
        {
            text.Append(EvidenceBlock(e));
        }

        var relations = Relations.Where(r => aliases is null || aliases.Contains(RelationOwner(r))).ToList();
        if (relations.Count > 0)
        {
            text.AppendLine("Relazioni fra evidenze:");
            foreach (var r in relations)
            {
                text.Append(RelationLine(r));
            }
        }

        var empty = Items.Where(i => i.EvidenceCount == 0 && (sequences is null || sequences.Contains(i.Sequence))).ToList();
        if (empty.Count > 0)
        {
            text.AppendLine("Messaggi PM senza evidenze (esito validato): " + string.Join("; ",
                empty.Select(i => $"sequenza {i.Sequence.ToString(CultureInfo.InvariantCulture)} ({i.NoEvidenceReason})")));
        }

        return text.ToString();
    }

    /// <summary>Blocco di testo di una evidenza nella vista (intestazione, enunciato, struttura, qualificatori, citazioni).</summary>
    public string EvidenceBlock(ViewEvidence e)
    {
        var text = new StringBuilder();
        var message = Items.First(i => i.MessageId == e.MessageId);
        text.Append(e.Alias).Append(" · messaggio sequenza ").Append(e.Sequence.ToString(CultureInfo.InvariantCulture))
            .Append(" (id ").Append(message.MessageId).Append(") · ").Append(e.Kind).Append(" · ").Append(e.Polarity)
            .Append(" · ").Append(e.Certainty).AppendLine();
        text.Append("  enunciato: ").AppendLine(e.Statement);
        var s = e.Content.Structure;
        var parts = new[]
        {
            ("soggetto", s.Subject), ("azione", s.Action), ("oggetto", s.Object), ("ambito", s.Scope), ("scopo", s.Purpose),
            ("strumento", s.Instrument), ("canale", s.Channel), ("destinatario", s.Recipient),
        }.Where(p => p.Item2 is not null).Select(p => $"{p.Item1}: {p.Item2}").ToList();
        if (parts.Count > 0)
        {
            text.Append("  ").AppendLine(string.Join("; ", parts));
        }

        if (e.Content.Qualifiers.Count > 0)
        {
            text.Append("  qualificatori: ").AppendLine(string.Join("; ", e.Content.Qualifiers.Select(q => $"{q.Type} «{q.Text}»")));
        }

        foreach (var span in e.Content.Sources)
        {
            text.Append("  citazione ").Append(span.Role == EvidenceSourceRole.Primary ? "" : $"({span.Role}, sequenza {span.Sequence}) ")
                .Append('«').Append(span.Quote).AppendLine("»");
        }

        return text.ToString();
    }

    /// <summary>Riga di una relazione nella vista.</summary>
    public static string RelationLine(ViewRelation r) =>
        $"- {r.FromAlias} {r.Type} {r.ToAlias}"
        + (r.Basis == EvidenceRelationBasis.Declared ? " (dichiarata dal PM)" : " (inferita, da verificare)")
        + (r.Open ? " — conflitto aperto: le posizioni restano alternative" : "") + Environment.NewLine;

    /// <summary>L'evidenza più recente (ordine degli alias) di una relazione: la relazione si mostra con lei, quando l'altra è già nota.</summary>
    public string RelationOwner(ViewRelation r)
    {
        var from = IndexOf(r.FromAlias);
        var to = IndexOf(r.ToAlias);
        return from >= to ? r.FromAlias : r.ToAlias;
    }

    private int IndexOf(string alias)
    {
        for (var i = 0; i < Evidences.Count; i++)
        {
            if (Evidences[i].Alias == alias)
            {
                return i;
            }
        }

        return -1;
    }
}

// ============================================================ Viste 1.1 (origine Document) — contracts/http-api.md §Bundle corrente e payload Evidence

public sealed record DocumentOriginView(string Kind, Guid DocumentId, Guid DocumentVersionId, Guid PreparationId);

public sealed record DocumentQualifierView(string Type, string Text);

/// <summary>Fonte persistita presentata con enum testuali; le parti di una citazione multi-blocco restano separate (citationGroupId, part).</summary>
public sealed record DocumentSourceView(
    Guid SourceId, string Kind, string Role, Guid CitationGroupId, int Part, Guid DocumentId, Guid DocumentVersionId, Guid PreparationId,
    Guid ArtifactId, Guid BlockId, int Start, int End, string Quote);

public sealed record DocumentEvidenceContentView(
    string SchemaVersion, string? KindRationale, DocumentEvidenceStructure Structure, IReadOnlyList<DocumentQualifierView> Qualifiers,
    IReadOnlyList<DocumentSourceView> Sources);

public sealed record DocumentEvidenceView(
    Guid Id, Guid VersionId, int Version, bool IsCurrent, Guid ProjectId, int Ordinal, string Key, string Kind, string Polarity, string Certainty,
    string Statement, DocumentOriginView Origin, DocumentEvidenceContentView Content);

public sealed record LocatorBboxView(double[] Coordinates, string? CoordinateSystem);

public sealed record LocatorLinesView(int Start, int End);

/// <summary>Locator risolto dal server sul blocco originale: null esplicito quando il dato non esiste, mai una pagina presunta.</summary>
public sealed record LocatorView(int? Page, IReadOnlyList<string> HeadingPath, LocatorBboxView? Bbox, LocatorLinesView? SourceLines, string? DoclingRef);

public sealed record SourceDocumentView(
    Guid SourceId, Guid DocumentId, Guid DocumentVersionId, int DocumentVersion, string OriginalName, Guid PreparationId, int Generation,
    Guid ArtifactId, Guid BlockId, string BlockRef, LocatorView Locator, string PassageUrl, string DownloadUrl);

public sealed record DocumentRelationView(Guid Id, string Type, string Basis, string? Rationale, Guid FromEvidenceId, Guid ToEvidenceId);

public sealed record DocumentRelationDetailView(Guid Id, string Type, string Basis, string? Rationale, string Direction, Guid OtherEvidenceId, string OtherStatement);

public sealed record DocumentVersionSummaryView(
    Guid Id, int Version, Guid PreparationId, int Generation, DateTimeOffset CreatedAt, int EvidenceCount, string? NoEvidenceReason, string PromptVersion,
    string SchemaVersion, string ModelId, Guid JobId, string? ManifestHash, bool IsCurrent);

public sealed record DocumentEvidenceDetailView(
    string EvidenceViewVersion, object Evidence, IReadOnlyList<object> Relations, IReadOnlyList<SourceDocumentView> SourceDocuments,
    IReadOnlyList<SourceMessageView> SourceMessages);

public sealed record DocumentExtractionVersionView(
    string EvidenceViewVersion, DocumentVersionSummaryView Version, Guid DocumentId, Guid DocumentVersionId, JsonElement Parameters,
    IReadOnlyList<DocumentEvidenceView> Evidences, IReadOnlyList<DocumentRelationView> Relations, IReadOnlyList<SourceDocumentView> SourceDocuments);

public sealed record DocumentDiffEntryView(string Status, DocumentEvidenceView? From, DocumentEvidenceView? To, IReadOnlyList<FieldChangeView> Changes);

public sealed record DocumentVersionDiffView(
    string EvidenceViewVersion, Guid FromVersionId, Guid ToVersionId, Guid DocumentVersionId, DiffSummaryView Summary, IReadOnlyList<DocumentDiffEntryView> Entries);

public sealed record DocumentBundleVersionsView(
    string EvidenceViewVersion, Guid DocumentId, Guid DocumentVersionId, int BundleRevision, Guid? CurrentPreparationId, Guid? CurrentEvidenceVersionId,
    IReadOnlyList<DocumentVersionSummaryView> Versions);

/// <summary>Origine documentale risolta di una preparazione.</summary>
public sealed record DocumentPreparationInfo(Guid PreparationId, int Generation, Guid DocumentId, Guid DocumentVersionId, int DocumentVersion, string OriginalName);

/// <summary>
/// Costruzione delle viste 1.1 a partire dalle righe del ledger e dagli artefatti di parsing persistiti: documento, preparazione, blocco e
/// locator sono risolti dal server (mai dal modello). Nessuna scrittura e nessuna chiamata AI.
/// </summary>
public static class DocumentEvidenceViews
{
    public const string ViewVersion = "1.1.0";
    public const string LegacyViewVersion = "1.0.0";

    public static async Task<Dictionary<Guid, DocumentPreparationInfo>> PreparationsAsync(AppDbContext db, Guid projectId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        return await (from p in db.DocumentPreparations.AsNoTracking()
                      join v in db.DocumentVersions.AsNoTracking() on p.DocumentVersionId equals v.Id
                      where p.ProjectId == projectId && wanted.Contains(p.Id)
                      select new DocumentPreparationInfo(p.Id, p.Generation, v.DocumentId, v.Id, v.Version, v.OriginalName))
            .ToDictionaryAsync(x => x.PreparationId, ct);
    }

    public static DocumentEvidenceView ToView(EvidenceItem e, int version, bool isCurrent, IReadOnlyDictionary<Guid, DocumentPreparationInfo> preparations)
    {
        var preparationId = e.PreparationId ?? throw new InvalidOperationException("Evidenza documentale senza preparazione.");
        var info = preparations[preparationId];
        var content = DocumentEvidenceCodec.DecodeContent(e.Content);
        return new(
            e.Id, e.VersionId, version, isCurrent, e.ProjectId, e.Ordinal, e.Key, e.Kind.ToString(), e.Polarity.ToString(), e.Certainty.ToString(), e.Statement,
            new("Document", info.DocumentId, info.DocumentVersionId, info.PreparationId),
            new(content.SchemaVersion, content.KindRationale, content.Structure,
                [.. content.Qualifiers.Select(q => new DocumentQualifierView(q.Type.ToString(), q.Text))],
                [.. content.Sources.OrderBy(s => s.CitationGroupId).ThenBy(s => s.Part).Select(s => new DocumentSourceView(
                    s.SourceId, s.Kind.ToString(), s.Role.ToString(), s.CitationGroupId, s.Part, s.DocumentId!.Value, s.DocumentVersionId!.Value, s.PreparationId!.Value,
                    s.ArtifactId!.Value, s.BlockId!.Value, s.Start, s.End, s.Quote))]));
    }

    /// <summary>Risolve ogni sourceId distinto in versione, preparazione, artefatto, blocco e locator; le versioni storiche restano risolvibili.</summary>
    public static async Task<IReadOnlyList<SourceDocumentView>> SourcesAsync(
        AppDbContext db, Guid projectId, IEnumerable<DocumentEvidenceView> evidences, IReadOnlyDictionary<Guid, DocumentPreparationInfo> preparations, CancellationToken ct)
    {
        var sources = evidences.SelectMany(e => e.Content.Sources).GroupBy(s => s.SourceId).Select(g => g.First()).OrderBy(s => s.SourceId).ToList();
        var wanted = sources.Select(s => s.PreparationId).Where(id => !preparations.ContainsKey(id)).ToList();
        if (wanted.Count > 0)
        {
            // Le fonti possono stare su una preparazione diversa da quella della versione (artefatti storici restano risolvibili).
            preparations = preparations.Concat(await PreparationsAsync(db, projectId, wanted, ct)).ToDictionary(x => x.Key, x => x.Value);
        }

        var blockIds = sources.Select(s => s.BlockId).Distinct().ToList();
        var blocks = await (from b in db.DocumentBlocks.AsNoTracking()
                            join a in db.ParsedArtifacts.AsNoTracking() on b.ArtifactId equals a.Id
                            where b.ProjectId == projectId && blockIds.Contains(b.Id)
                            select new { Block = b, a.PreparationId }).ToDictionaryAsync(x => x.Block.Id, ct);
        var result = new List<SourceDocumentView>();
        foreach (var s in sources)
        {
            if (!blocks.TryGetValue(s.BlockId, out var found) || found.PreparationId != s.PreparationId || found.Block.ArtifactId != s.ArtifactId
                || !preparations.TryGetValue(s.PreparationId, out var info))
            {
                continue;   // fonte non risolvibile nel progetto: omessa, mai inventata
            }

            var b = found.Block;
            var root = $"/api/projects/{projectId}/documents/{info.DocumentId}/versions/{info.DocumentVersion}";
            result.Add(new(
                s.SourceId, info.DocumentId, info.DocumentVersionId, info.DocumentVersion, info.OriginalName, info.PreparationId, info.Generation, b.ArtifactId, b.Id, b.Ref,
                new(b.Page, JsonSerializer.Deserialize<string[]>(b.HeadingPathJson) ?? [],
                    b.BboxJson is null ? null : new LocatorBboxView(JsonSerializer.Deserialize<double[]>(b.BboxJson)!, b.CoordinateSystem),
                    b.LineStart is { } ls && b.LineEnd is { } le ? new LocatorLinesView(ls, le) : null, b.DoclingRef),
                $"{root}/preparations/{info.PreparationId}/passages/{b.Id}?start={s.Start}&end={s.End}", $"{root}/download"));
        }

        return result;
    }

    public static DocumentVersionSummaryView Summary(EvidenceExtractionVersion v, int generation, bool isCurrent) => new(
        v.Id, v.Version, v.PreparationId!.Value, generation, v.CreatedAt, v.EvidenceCount, v.NoEvidenceReason, v.PromptVersion, v.SchemaVersion, v.ModelId, v.JobId,
        v.ManifestHash, isCurrent);

    /// <summary>Confronto per chiave fra due versioni documentali già dichiarate comparabili: nessun abbinamento semantico.</summary>
    public static (DiffSummaryView Summary, IReadOnlyList<DocumentDiffEntryView> Entries) Compare(IReadOnlyList<DocumentEvidenceView> from, IReadOnlyList<DocumentEvidenceView> to)
    {
        var entries = new List<DocumentDiffEntryView>();
        var toByKey = to.ToDictionary(e => e.Key);
        var fromKeys = from.Select(e => e.Key).ToHashSet();
        foreach (var f in from.OrderBy(e => e.Ordinal))
        {
            if (!toByKey.TryGetValue(f.Key, out var t))
            {
                entries.Add(new("Removed", f, null, []));
                continue;
            }

            var changes = new List<FieldChangeView>();
            void Field(string name, string? a, string? b)
            {
                if (a != b)
                {
                    changes.Add(new(name, a, b));
                }
            }

            string Locations(DocumentEvidenceView e) => string.Join(",", e.Content.Sources.Select(s => $"{s.BlockId}:{s.Start}-{s.End}"));
            Field("statement", f.Statement, t.Statement);
            Field("kind", f.Kind, t.Kind);
            Field("polarity", f.Polarity, t.Polarity);
            Field("certainty", f.Certainty, t.Certainty);
            Field("sources", Locations(f), Locations(t));
            entries.Add(new(changes.Count == 0 ? "Unchanged" : "Modified", f, t, changes));
        }

        entries.AddRange(to.Where(e => !fromKeys.Contains(e.Key)).OrderBy(e => e.Ordinal).Select(e => new DocumentDiffEntryView("Added", null, e, [])));
        int Count(string s) => entries.Count(e => e.Status == s);
        return (new(Count("Added"), Count("Removed"), Count("Unchanged"), Count("Modified"), 0), entries);
    }
}
