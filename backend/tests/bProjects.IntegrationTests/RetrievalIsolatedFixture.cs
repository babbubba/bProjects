using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.Evidence;

namespace bProjects.IntegrationTests;

/// <summary>Un blocco documentale del ledger controllato: testo canonico e percorso dei titoli.</summary>
internal sealed record IsolatedBlock(int Ordinal, string Text, IReadOnlyList<string> Heading);

/// <summary>Un'evidenza del ledger controllato: un enunciato (una frase del blocco) con la sua fonte (blocco, intervallo, citazione letterale).</summary>
internal sealed record IsolatedEvidence(EvidenceKind Kind, EvidencePolarity Polarity, string Statement, int BlockOrdinal, int Start, int End);

/// <summary>Un documento del ledger controllato. <see cref="ExcludedByPm"/>: escluso dallo snapshot dal PM (resta nell'indice).</summary>
internal sealed record IsolatedDocument(string Key, string Name, bool ExcludedByPm, IReadOnlyList<IsolatedBlock> Blocks, IReadOnlyList<IsolatedEvidence> Evidences);

/// <summary>Ledger canonico di uno scenario: documenti con blocchi ed evidenze, in ordine fisso. Identità = <see cref="Sha256"/>.</summary>
internal sealed record IsolatedLedger(CampaignScenario Scenario, IReadOnlyList<IsolatedDocument> Documents)
{
    public int BlockCount => Documents.Sum(d => d.Blocks.Count);

    public int EvidenceCount => Documents.Sum(d => d.Evidences.Count);

    /// <summary>Evidenze del ledger che entrano nello snapshot e non sono Gap (i candidati possibili del recupero).</summary>
    public int IncludedNonGapEvidenceCount => Documents.Where(d => !d.ExcludedByPm).Sum(d => d.Evidences.Count(e => e.Kind != EvidenceKind.Gap));

    /// <summary>SHA-256 dell'elenco canonico (documenti, blocchi, evidenze, ordine): qualunque variazione del ledger la cambia.</summary>
    public string Sha256
    {
        get
        {
            var text = new StringBuilder();
            foreach (var d in Documents)
            {
                text.Append("doc|").Append(d.Key).Append('|').Append(d.ExcludedByPm ? 1 : 0).Append('\n');
                foreach (var b in d.Blocks)
                {
                    text.Append("b|").Append(b.Ordinal).Append('|').Append(string.Join('>', b.Heading)).Append('|').Append(RetrievalIsolatedFixture.Sha(b.Text)).Append('\n');
                }

                foreach (var e in d.Evidences)
                {
                    text.Append("e|").Append(e.Kind).Append('|').Append(e.Polarity).Append('|').Append(e.BlockOrdinal).Append('|').Append(e.Start).Append('|').Append(e.End).Append('|').Append(RetrievalIsolatedFixture.Sha(e.Statement)).Append('\n');
                }
            }

            return RetrievalIsolatedFixture.Sha(text.ToString());
        }
    }
}

/// <summary>
/// Ledger canonico CONTROLLATO per la verifica isolata Retrieval → Gap Stage (003, piano <c>validation/isolated-retrieval-gap-plan.md</c>). Deriva SOLO da corpus, gold, domande ed estensioni già congelati
/// per T040 (<see cref="RetrievalGapCampaign"/>): nessuna attesa nuova, nessun gold modificato. Il ledger è IDEALIZZATO (una evidenza per frase dei paragrafi, come produrrebbe un estrattore ideale): non misura la
/// qualità dell'estrazione e non equivale al ledger estratto dal modello nel T040.
/// </summary>
internal static class RetrievalIsolatedFixture
{
    public const string Id = "isolated-retrieval-gap-1";

    /// <summary>Chiave del documento che contiene le domande del PM come evidenze Gap (una per attesa).</summary>
    public const string QuestionsKey = "pm-domande";

    public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static IsolatedLedger Build(CampaignScenario scenario)
    {
        var corpus = RetrievalGapCampaign.BuildCorpus(scenario);
        var negatives = corpus.Gold.Where(g => g.Negative).Select(g => g.Sentence.Trim()).ToHashSet(StringComparer.Ordinal);
        var documents = new List<IsolatedDocument>();
        foreach (var d in corpus.Documents)
        {
            documents.Add(FromText(d.Key, d.FileName, d.PlainText, false, negatives));
        }

        foreach (var e in RetrievalGapCampaign.ExtensionDocuments)
        {
            documents.Add(FromText(e.Key, e.FileName, e.Text, e.ExcludedByPm, negatives));
        }

        var blocks = scenario.Expectations.Select((e, i) => new IsolatedBlock(i, e.Question, [])).ToList();
        var gaps = scenario.Expectations.Select((e, i) => new IsolatedEvidence(EvidenceKind.Gap, EvidencePolarity.Affirmed, e.Question, i, 0, e.Question.Length)).ToList();
        documents.Add(new IsolatedDocument(QuestionsKey, QuestionsKey + ".txt", false, blocks, gaps));
        return new IsolatedLedger(scenario, documents);
    }

    private static IsolatedDocument FromText(string key, string name, string plainText, bool excluded, ISet<string> negatives)
    {
        var paragraphs = plainText.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var blocks = new List<IsolatedBlock>();
        var evidences = new List<IsolatedEvidence>();
        var heading = new List<string>();
        for (var i = 0; i < paragraphs.Length; i++)
        {
            var text = paragraphs[i];
            if (IsHeading(text))
            {
                heading = [text];
                blocks.Add(new IsolatedBlock(i, text, []));
                continue;
            }

            blocks.Add(new IsolatedBlock(i, text, [.. heading]));
            var cursor = 0;
            foreach (var sentence in Regex.Split(text, @"(?<=\.)\s+").Where(s => s.Length > 0))
            {
                var start = text.IndexOf(sentence, cursor, StringComparison.Ordinal);
                cursor = start + sentence.Length;
                evidences.Add(new IsolatedEvidence(EvidenceKind.Requirement, negatives.Contains(sentence.Trim()) ? EvidencePolarity.Negated : EvidencePolarity.Affirmed, sentence, i, start, cursor));
            }
        }

        return new IsolatedDocument(key, name, excluded, blocks, evidences);
    }

    /// <summary>Titolo: paragrafo breve senza punto finale (come i titoli del corpus generato).</summary>
    private static bool IsHeading(string text) => text.Length <= 80 && !text.EndsWith('.') && !text.EndsWith('?') && !text.Contains(". ", StringComparison.Ordinal);

    // ================================================================ manifesto

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string GoldSha256(string label)
    {
        var manifest = JsonNode.Parse(RetrievalGapCampaign.BuildManifestJson())!;
        string? found = null;
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["Label"]?.GetValue<string>() == label && o["goldSha256"] is { } g)
                    {
                        found = g.GetValue<string>();
                    }

                    foreach (var child in o)
                    {
                        Walk(child.Value);
                    }

                    break;
                case JsonArray a:
                    foreach (var child in a)
                    {
                        Walk(child);
                    }

                    break;
            }
        }

        Walk(manifest);
        return found ?? throw new InvalidOperationException($"goldSha256 non trovato nel manifesto T040 per {label}.");
    }

    /// <summary>Manifesto dell'identità del ledger isolato: NON è una versione del manifesto T040 e non lo modifica; ne riferisce hash e costanti.</summary>
    public static string BuildManifestJson()
    {
        object Scenario(CampaignScenario s)
        {
            var ledger = Build(s);
            return new
            {
                label = s.Label,
                origin = s.Origin,
                goldSha256FromT040Manifest = GoldSha256(s.Label),
                ledgerSha256 = ledger.Sha256,
                blocks = ledger.BlockCount,
                evidences = ledger.EvidenceCount,
                includedNonGapEvidences = ledger.IncludedNonGapEvidenceCount,
                documents = ledger.Documents.Select(d => new { d.Key, d.Name, d.ExcludedByPm, blocks = d.Blocks.Count, evidences = d.Evidences.Count, gaps = d.Evidences.Count(e => e.Kind == EvidenceKind.Gap) }).ToArray(),
                questions = s.Expectations.Select(e => e.Question).ToArray(),
            };
        }

        var manifest = new
        {
            manifest = Id,
            notEquivalentToT040 = "Verifica complementare: un esito favorevole NON e un PASS di T040, non chiude M3 e non sostituisce la pipeline end-to-end.",
            plan = "specs/003-retrieval-evidence-search/validation/isolated-retrieval-gap-plan.md",
            hierarchy = "SIMULATA: risposte deterministiche (stub) della gerarchia di riconciliazione; nessuna chiamata generativa al DGX per l'estrazione o la riconciliazione.",
            realComponents = new[] { "PostgreSQL + pgvector", "endpoint di analisi", "workflow v10 e checkpoint", "indicizzazione e embedding", "ricerca ibrida", "riordino", "registro dei candidati", "messaggio e schema dello stadio Gap", "validatore", "coverage-3 e API", "LLM dello stadio Gap (una sola chiamata)" },
            ledger = "idealizzato: una evidenza per frase di ogni paragrafo (blocco), titoli senza evidenza; le domande del PM come evidenze Gap; documento d'estensione parziale incluso, documento d'estensione escluso dal PM (nell'indice, fuori dallo snapshot)",
            t040ManifestSha256 = RetrievalGapCampaign.ManifestSha256(),
            definition = RetrievalGapCampaign.DefinitionVersion,
            codeBaseline = RetrievalGapCampaign.CodeBaseline,
            retrievalPolicy = RetrievalGapCampaign.PolicyVersion,
            models = new { llm = RetrievalGapCampaign.LlmModelId, embedding = RetrievalGapCampaign.EmbeddingModelId, reranker = RetrievalGapCampaign.RerankerModelId },
            window = new { tokens = RetrievalGapCampaign.WindowTokens, output = RetrievalGapCampaign.OutputTokens, margin = RetrievalGapCampaign.MarginTokens, inputBudget = RetrievalGapCampaign.BudgetTokens },
            contract = "una sola chiamata LLM reale per scenario (lo stadio Gap); routing fail-closed",
            notVerifiable = new[]
            {
                "estrazione LLM dei Gap e dei fatti",
                "gerarchia di riconciliazione reale (conflitti Open e openConflict: NotVerified con la gerarchia deterministica)",
                "espansione v10, rischio Cross e limite di 512 chiamate",
                "SC-001 nel suo insieme",
            },
            scenarios = new[] { Scenario(RetrievalGapCampaign.Single), Scenario(RetrievalGapCampaign.Multi) },
        };
        return JsonSerializer.Serialize(manifest, Indented);
    }

    public static string ManifestSha256() => Sha(BuildManifestJson().Replace("\r\n", "\n", StringComparison.Ordinal));
}
