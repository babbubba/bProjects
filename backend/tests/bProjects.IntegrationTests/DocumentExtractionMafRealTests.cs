using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bProjects.App.AI;
using bProjects.App.Documents;
using bProjects.App.Evidence;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>
/// T055 (DGX) — estrazione documentale reale: parser Docling reale + PostgreSQL reale + SDK MAF + modello interno reale (DGX), una chiamata alla
/// volta (slot unico), attraverso il dispatcher, l'agente di unità e l'aggregatore di produzione. Le attese sono quelle del gold T002
/// (<c>tests/fixtures/documents/manifest.json</c>): non basta la copertura degli ID, si confrontano fonti (citazione esatta sul blocco),
/// polarità, quantità, qualificatori ed eventuali fonti multi-blocco. Un errore di modello gestito (unità Failed, preparazione non Ready,
/// output troncato) NON è mai un PASS. Servono BPROJECTS_TEST_POSTGRES, BPROJECTS_TEST_PARSER_ENDPOINT/_SECRET, BPROJECTS_TEST_DGX_*; senza,
/// i test risultano Skipped. Nessun segreto né endpoint è scritto: il report contiene solo numeri e testi sintetici del corpus.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
[Collection(RealParser.CollectionName)]
public sealed class DocumentExtractionMafRealTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ prove

    [Fact]
    public async Task Runtime_profile_tokenizer_and_template_are_verified_and_the_preflight_matches_the_real_usage()
    {
        await using var pipe = await Pipeline.CreateAsync();
        var v = new Verdicts("runtime");
        var snapshot = await pipe.Monitor.GetSnapshotAsync(pipe.Ai.ForPhase(AiPhase.Extraction).ModelId, Ct);
        v.Note($"sonda runtime: stato={snapshot.Status} finestra={snapshot.ContextWindowTokens?.ToString() ?? "n/d"} npredict={snapshot.ServerDefaultNPredict?.ToString() ?? "n/d"} slot={snapshot.SlotCount?.ToString() ?? "n/d"} " +
            $"statoModello={snapshot.ModelStatus ?? "n/d"} modelliElencati={snapshot.AvailableModels?.Count.ToString() ?? "n/d"} modelloElencato={snapshot.AvailableModels?.Contains(pipe.Ai.ForPhase(AiPhase.Extraction).ModelId) ?? false}");
        DocumentRuntimeProfile profile;
        try
        {
            profile = await pipe.Factory.Preflight.ResolveProfileAsync(AiPhase.Extraction, Ct);
        }
        catch (RuntimeUnverifiedException ex)
        {
            v.Check("profilo del runtime verificato", false, ex.Message);
            v.Flush();
            return;
        }

        var caps = await pipe.Monitor.GetCountingCapabilitiesAsync(profile.ModelId, Ct);
        v.Note($"profilo: W={profile.WindowTokens} O={profile.OutputTokens} M={profile.MarginTokens} B={profile.InputBudgetTokens} thinking={profile.Thinking} " +
            $"templateEsposto={profile.TemplateExposed} tokenizerVerificato={caps.TokenizerVerified} calibrazione={profile.CalibrationId ?? "nessuna"}");
        v.Check("template esposto e tokenizer verificato (nessuna calibrazione necessaria)", profile.TemplateExposed && caps.TokenizerVerified);

        // Payload reale di un'unità piccola: conteggio del preflight contro l'usage restituito dal server per la STESSA richiesta.
        var text = "Il portale non deve inviare mai email senza consenso.\nNon e' richiesta la gestione dei resi.";
        var range = new DocumentSourceRange("r0", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), text, 0, text.Length, Owned: true);
        var messages = DocumentEvidenceAgent.BuildMessages([range]);
        var schema = DocumentEvidenceSchema.ForUnit(["r0"]);
        var measured = await pipe.Factory.Preflight.MeasureAsync(
            new DocumentPayload(AiPhase.Extraction, DocumentEvidenceAgent.Instructions, messages, schema.GetRawText()), Ct, profile.Fingerprint);
        v.Note($"preflight: tokens={measured.Tokens} qualita={measured.Quality}");
        v.Check("conteggio misurato (non stimato)", measured.Quality == TokenCountQuality.Measured);

        var options = pipe.Model.CreateChatOptions(schema, DocumentEvidenceSchema.Name, temperature: 0, phase: AiPhase.Extraction);
        var response = await pipe.Factory.CreateBatchAgent("probe", "probe", DocumentEvidenceAgent.Instructions, options, AiPhase.Extraction, profile.Fingerprint)
            .RunAsync(messages, null, null, Ct);
        var usage = response.Usage;
        v.Note($"usage reale: prompt={usage?.InputTokenCount} completion={usage?.OutputTokenCount}");
        v.Check("usage restituito dal server", usage?.InputTokenCount is > 0 && usage.OutputTokenCount is > 0);
        if (usage?.InputTokenCount is { } prompt)
        {
            var delta = Math.Abs(prompt - measured.Tokens);
            // Il preflight e un limite superiore: conta anche lo schema della risposta, che llama.cpp traduce in grammatica e NON mette nel prompt.
            var schemaTokens = await pipe.Model.CountTokensAsync(schema.GetRawText(), Ct);
            var withoutSchema = measured.Tokens - schemaTokens;
            v.Note($"preflight={measured.Tokens} usage.prompt={prompt} schema={schemaTokens} token => preflight-schema={withoutSchema} (scarto {Math.Abs(withoutSchema - prompt)} token, {(double)Math.Abs(withoutSchema - prompt) / prompt:P1}); sovrastima totale={measured.Tokens - prompt} token ({(double)(measured.Tokens - prompt) / prompt:P1})");
            v.Check("il preflight non sottostima mai l'usage reale (limite superiore)", measured.Tokens >= prompt, $"preflight={measured.Tokens} usage={prompt}");
            v.Check("template+tokenizer riproducono l'usage reale entro il 3% (preflight senza schema)", Math.Abs(withoutSchema - prompt) <= prompt * 0.03, $"preflight-schema={withoutSchema} usage={prompt}");
        }

        v.Flush();
    }

    [Fact]
    public async Task Thinking_tokens_count_inside_max_tokens_hypothesis_is_observed_on_the_real_server()
    {
        await using var pipe = await Pipeline.CreateAsync(o =>
        {
            o.Phases.Extraction.EnableThinking = true;
            o.Phases.Extraction.MaxOutputTokens = 256;   // volutamente basso: se il ragionamento è dentro max_tokens, esaurisce il budget
        });
        var v = new Verdicts("thinking");
        var options = pipe.Model.CreateChatOptions(null, "probe", temperature: 0, phase: AiPhase.Extraction);
        var response = await pipe.Model.ChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Elenca in modo dettagliato e ragionato, passo per passo, tutte le possibili conseguenze di una conferma ordine inviata via email senza consenso del cliente. Ragiona prima di rispondere.")],
            options, Ct);
        var completion = response.Usage?.OutputTokenCount;
        var finish = response.FinishReason?.Value;
        v.Note($"thinking=ON max_tokens=256: completion_tokens={completion} finish_reason={finish} testoVisibile={response.Text.Length} caratteri");
        v.Check("usage disponibile", completion is > 0);
        v.Check("il server rispetta il tetto: completion_tokens <= max_tokens", completion <= 256, $"completion={completion}");
        var reasoningInsideMax = finish == "length";
        v.Note(reasoningInsideMax
            ? "OSSERVATO: con ragionamento ON la risposta e' stata troncata al tetto (finish=length) => i token di ragionamento consumano max_tokens (ipotesi CONFERMATA su questo server)."
            : "OSSERVATO: nessun troncamento al tetto con ragionamento ON: ipotesi NON confermata da questa sonda (o il ragionamento e' stato breve).");
        v.Flush();
    }

    [Fact]
    public async Task A_pdf_with_unprocessed_information_is_Incomplete_and_never_reaches_the_model()
    {
        await using var pipe = await Pipeline.CreateAsync();
        var v = new Verdicts("pdf-mixed");
        var result = await pipe.RunFixtureAsync("pdf-mixed.pdf");

        v.Check("preparazione Incomplete (mai Ready)", result.Preparation.Status == PreparationStatus.Incomplete, result.Preparation.Status.ToString());
        v.Check("nessuna chiamata al modello", result.Calls.Count == 0, $"chiamate={result.Calls.Count}");
        v.Check("nessuna versione Evidence pubblicata", result.Version is null);
        v.Check("issue bloccante di completezza presente", result.Issues.Any(i => i.AffectsCompleteness));
        v.Flush();
    }

    public static TheoryData<string> Formats => new() { "pdf-text.pdf", "docx-basic.docx", "md-basic.md", "txt-negations.txt" };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Each_format_is_extracted_into_a_Ready_preparation_whose_evidences_match_the_T002_gold(string fixture)
    {
        await using var pipe = await Pipeline.CreateAsync();
        var result = await pipe.RunFixtureAsync(fixture);
        var v = new Verdicts(fixture);
        v.Describe(result);
        await v.AssertPipelineAsync(result, pipe);

        var gold = Gold.For(fixture);
        foreach (var fact in gold)
        {
            v.Fact(result, fact);
        }

        v.Flush();
    }

    [Fact]
    public async Task Multipage_table_row_keeps_its_exclusion_and_cites_the_shared_header_as_a_multi_block_source()
    {
        await using var pipe = await Pipeline.CreateAsync();
        var result = await pipe.RunFixtureAsync("pdf-table-multipage.pdf");
        var v = new Verdicts("pdf-table-multipage");
        v.Describe(result);
        await v.AssertPipelineAsync(result, pipe);
        foreach (var fact in Gold.For("pdf-table-multipage.pdf"))
        {
            v.Fact(result, fact);
        }

        v.Flush();
    }

    [Fact]
    public async Task A_non_BMP_text_keeps_exact_utf16_quotes_and_the_gold_facts()
    {
        await using var pipe = await Pipeline.CreateAsync();
        var result = await pipe.RunFixtureAsync("md-non-bmp.md");
        var v = new Verdicts("md-non-bmp");
        v.Describe(result);
        await v.AssertPipelineAsync(result, pipe);
        foreach (var fact in Gold.For("md-non-bmp.md"))
        {
            v.Fact(result, fact);
        }

        v.Flush();
    }

    [Fact]
    public async Task A_long_document_over_many_units_preserves_the_14_distant_facts_conflict_and_qualifier_separately()
    {
        await using var pipe = await Pipeline.CreateAsync();
        var result = await pipe.RunFixtureAsync("txt-long.txt");
        var v = new Verdicts("txt-long");
        v.Describe(result);
        await v.AssertPipelineAsync(result, pipe);

        foreach (var fact in Gold.For("txt-long.txt"))
        {
            v.Fact(result, fact);
        }

        // Conflitto distante F01/F13 e qualificatore F06/F14: le evidenze restano SEPARATE e con i propri valori (nessuna fusione, nessuna perdita).
        var f01 = result.Match(Gold.Quote("txt-long-F01"));
        var f13 = result.Match(Gold.Quote("txt-long-F13"));
        v.Check("conflitto 500/2000: due evidenze distinte con i rispettivi valori", f01.Count > 0 && f13.Count > 0 && f01.All(e => !f13.Contains(e)));
        var f06 = result.Match(Gold.Quote("txt-long-F06"));
        var f14 = result.Match(Gold.Quote("txt-long-F14"));
        v.Check("qualificatore: F06 conserva l'eccezione (contratto quadro) e F14 la propria condizione (12 mesi)",
            f06.Any(e => e.Has("contratto quadro")) && f14.Any(e => e.Has("12 mesi")));

        // Relazioni: l'estrazione per unita non puo collegare fatti di unita diverse (la riconciliazione globale e dell'incremento I5).
        var related = result.Relations.Count(r => result.Evidences.Any(e => e.Item.Id == r.FromEvidenceId));
        v.Note($"relazioni persistite in estrazione: {related} (SOLO intra-unita; relazione F01/F13 e F06/F14 fra unita: NOT VERIFIED qui, compito dell'I5)");
        v.Flush();
    }

    [Fact]
    public async Task A_text_without_facts_completes_Ready_with_a_reasoned_zero_distinct_from_a_loss()
    {
        await using var pipe = await Pipeline.CreateAsync();
        var result = await pipe.RunBytesAsync("Grazie e a presto.\n\nCi sentiamo domani mattina.\n"u8.ToArray(), "text/plain", "saluti.txt");
        var v = new Verdicts("zero-evidenze");
        v.Describe(result);
        v.Check("preparazione Ready (non Failed/Incomplete)", result.Preparation.Status == PreparationStatus.Ready, result.Preparation.Status.ToString());
        v.Check("nessuna evidenza", result.Evidences.Count == 0, $"evidenze={result.Evidences.Count}");
        v.Check("motivo noEvidence sulla versione", !string.IsNullOrWhiteSpace(result.Version?.NoEvidenceReason), result.Version?.NoEvidenceReason ?? "(assente)");
        v.Check("tutte le porzioni Completed con motivo (zero motivato != perdita)",
            result.Portions.Count > 0 && result.Portions.All(p => p.Status == PortionStatus.Completed && !string.IsNullOrWhiteSpace(p.NoEvidenceReason)),
            string.Join(",", result.Portions.Select(p => $"{p.Status}/{(string.IsNullOrWhiteSpace(p.NoEvidenceReason) ? "senza-motivo" : "motivo")}")));
        v.Check("nessuna unita Failed", result.Units.All(u => u.Status == DocumentJobStatus.Completed));
        v.Note("NOT VERIFIED con modello reale: la perdita (unita Failed / coverage incompleta) e provata solo con modello finto in T054; qui non si induce un errore del modello reale.");
        v.Flush();
    }

    // ------------------------------------------------------------------ pipeline reale

    private sealed record Ev(EvidenceItem Item, DocumentEvidenceContent Content)
    {
        public string Blob { get; } = Gold.Fold(string.Join(" ", new[]
        {
            Item.Statement, Content.Structure.Subject, Content.Structure.Action, Content.Structure.Object, Content.Structure.Scope, Content.Structure.Purpose,
            Content.Structure.Instrument, Content.Structure.Channel, Content.Structure.Recipient,
        }.Concat(Content.Qualifiers.Select(q => q.Text)).Where(s => s is not null)));

        public bool Has(params string[] words) => words.All(w => Blob.Contains(Gold.Fold(w), StringComparison.Ordinal));

        public bool Negative => Item.Polarity is EvidencePolarity.Negated or EvidencePolarity.Excluded or EvidencePolarity.Prohibited || Item.Kind == EvidenceKind.Exclusion;

        /// <summary>La riga di tabella e identificata dalla fonte Primary citata (testo esatto del blocco), non dalla riformulazione dello statement.</summary>
        public bool CitesRow(string rowId) => Content.Sources.Any(s => s.Role == EvidenceSourceRole.Primary && Gold.Fold(s.Quote).Contains(Gold.Fold(rowId), StringComparison.Ordinal));

        public bool HasQualifier(params QualifierType[] types) => Content.Qualifiers.Any(q => types.Contains(q.Type));

        public override string ToString() =>
            $"[{Item.Kind}/{Item.Polarity}/{Item.Certainty}] {Item.Statement} | q={string.Join(";", Content.Qualifiers.Select(q => $"{q.Type}:{q.Text}"))} | fonti={string.Join(" + ", Content.Sources.Select(s => $"{s.Role}«{s.Quote}»"))}";
    }

    private sealed record UnitCall(Guid JobId, int Attempt, int? Prompt, int? Completion, string? Finish, long? DurationMs, InferenceCallState State);

    private sealed class RunResult
    {
        public required string Label { get; init; }
        public required ParseRunOutcome? ParseOutcome { get; init; }
        public required DocumentPreparation Preparation { get; init; }
        public required List<DocumentJob> Units { get; init; }
        public required List<DocumentPortion> Portions { get; init; }
        public required List<UnitCall> Calls { get; init; }
        public required EvidenceExtractionVersion? Version { get; init; }
        public required List<Ev> Evidences { get; init; }
        public required List<EvidenceRelation> Relations { get; init; }
        public required List<PreparationIssue> Issues { get; init; }
        public required Dictionary<Guid, string> Blocks { get; init; }
        public required TimeSpan Elapsed { get; init; }
        public required long OwnedTokens { get; init; }
        public required int OwnedChars { get; init; }

        /// <summary>Evidenze le cui citazioni coprono (token per token) la citazione gold.</summary>
        public List<Ev> Match(string goldQuote) => [.. Evidences.Where(e => Gold.Covers(goldQuote, string.Join(" ", e.Content.Sources.Select(s => s.Quote))))];
    }

    private sealed class Pipeline : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly AiDispatchService _dispatcher;

        private Pipeline(ParseRig parse, InternalModelClient model, ModelRuntimeMonitor monitor, AgentFactory factory, AiOptions options, ServiceProvider services, AiDispatchService dispatcher)
        {
            Parse = parse;
            Model = model;
            Monitor = monitor;
            Factory = factory;
            Ai = options;
            _services = services;
            _dispatcher = dispatcher;
        }

        public ParseRig Parse { get; }
        public InternalModelClient Model { get; }
        public ModelRuntimeMonitor Monitor { get; }
        public AgentFactory Factory { get; }
        public AiOptions Ai { get; }

        public static async Task<Pipeline> CreateAsync(Action<AiOptions>? tune = null)
        {
            var options = TestEnvironment.RequireProductionLikeOptions();
            tune?.Invoke(options);
            TestEnvironment.RequirePostgres();
            var parse = await ParseRig.CreateRealAsync();
            var execution = new ExecutionOptions();
            var notifications = new NullEvents();
            var services = new ServiceCollection().AddLogging().AddDbContext<AppDbContext>(o => o.UseNpgsql(parse.Db.ConnectionString))
                .AddSingleton<IOptions<ExecutionOptions>>(Options.Create(execution)).AddScoped<ExecutionLeaseStore>()
                .AddSingleton<IDocumentPreparationEvents>(notifications).AddScoped<DocumentBundleStore>().AddScoped<DocumentAggregationRunner>().BuildServiceProvider();
            var model = new InternalModelClient(options, MafDiagnostics.Wrap());

            // Il server e in router mode: un modello elencato ma "unloaded" non espone ancora la finestra. Una richiesta minima lo carica
            // (stesso modello interno, nessun fallback); solo dopo la sonda puo verificare finestra, tokenizer e template.
            var warm = model.CreateChatOptions(temperature: 0, phase: AiPhase.Extraction);
            warm.MaxOutputTokens = 16;
            await model.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Rispondi solo: ok")], warm, Ct);
            var monitor = new ModelRuntimeMonitor(model, options, NullLogger<ModelRuntimeMonitor>.Instance);
            var factory = AgentFactory.Create(options, model, monitor);
            var agent = new DocumentEvidenceAgent(services.GetRequiredService<IServiceScopeFactory>(), factory, model, Options.Create(new DocumentOptions()));
            var dispatcher = new AiDispatchService(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(execution), NullLogger<AiDispatchService>.Instance, unitExecutor: agent);
            return new Pipeline(parse, model, monitor, factory, options, services, dispatcher);
        }

        public Task<RunResult> RunFixtureAsync(string fixture) =>
            RunAsync(fixture, () => Parse.SeedFixtureAsync(fixture));

        public Task<RunResult> RunBytesAsync(byte[] content, string mediaType, string name) =>
            RunAsync(name, () => Parse.Db.SeedParseAsync(Parse.Store, content, null, 3, null, mediaType, name));

        private async Task<RunResult> RunAsync(string label, Func<Task<SeededParse>> seeder)
        {
            var seed = await seeder();
            var clock = Stopwatch.StartNew();
            var parseOutcome = await Parse.RunAsync();
            var deadline = DateTime.UtcNow.AddMinutes(25);
            while (DateTime.UtcNow < deadline)
            {
                if (await _dispatcher.RunOnceAsync(Ct))
                {
                    continue;
                }

                // Nulla di ammissibile: o tutto concluso, oppure un retry transitorio è in attesa del proprio backoff.
                var status = await Parse.Db.ScalarAsync<string>($"""SELECT "Status" FROM "DocumentPreparations" WHERE "Id" = '{seed.Preparation}'""");
                var waiting = await Parse.Db.ScalarAsync<long>($"""SELECT count(*) FROM "DocumentJobs" WHERE "PreparationId" = '{seed.Preparation}' AND "Kind" = 'ExtractUnit' AND "Status" = 'Queued'""");
                if (status != "Extracting" || waiting == 0)
                {
                    break;
                }

                await Task.Delay(2_000, Ct);
            }

            clock.Stop();
            return await CollectAsync(label, seed, parseOutcome, clock.Elapsed);
        }

        private async Task<RunResult> CollectAsync(string label, SeededParse seed, ParseRunOutcome? parseOutcome, TimeSpan elapsed)
        {
            await using var db = Parse.Db.NewContext();
            var prep = await db.DocumentPreparations.AsNoTracking().SingleAsync(p => p.Id == seed.Preparation, Ct);
            var units = await db.DocumentJobs.AsNoTracking().Where(j => j.PreparationId == prep.Id && j.Kind == DocumentJobKind.ExtractUnit).OrderBy(j => j.RequestedAt).ToListAsync(Ct);
            var portions = await db.DocumentPortions.AsNoTracking().Where(p => p.PreparationId == prep.Id).OrderBy(p => p.Ordinal).ToListAsync(Ct);
            var unitIds = units.Select(u => u.Id).ToList();
            var calls = (await db.InferenceCalls.AsNoTracking().Where(c => c.DocumentJobId != null && unitIds.Contains(c.DocumentJobId!.Value)).OrderBy(c => c.StartedAt).ToListAsync(Ct))
                .Select(c => new UnitCall(c.DocumentJobId!.Value, c.Attempt, c.PromptTokens, c.CompletionTokens, c.FinishReason, c.DurationMs, c.State)).ToList();
            var version = await db.EvidenceExtractionVersions.AsNoTracking().Where(x => x.PreparationId == prep.Id).OrderByDescending(x => x.Version).FirstOrDefaultAsync(Ct);
            var items = await db.Evidences.AsNoTracking().Where(e => e.PreparationId == prep.Id).OrderBy(e => e.Ordinal).ToListAsync(Ct);
            var evidences = items.Select(i => new Ev(i, DocumentEvidenceCodec.DecodeContent(i.Content))).ToList();
            var versionIds = items.Select(i => i.VersionId).Distinct().ToList();
            var relations = await db.EvidenceRelations.AsNoTracking().Where(r => versionIds.Contains(r.VersionId)).ToListAsync(Ct);
            var issues = await db.PreparationIssues.AsNoTracking().Where(i => i.PreparationId == prep.Id).ToListAsync(Ct);
            var artifactIds = await db.ParsedArtifacts.AsNoTracking().Where(a => a.PreparationId == prep.Id).Select(a => a.Id).ToListAsync(Ct);
            var blocks = await db.DocumentBlocks.AsNoTracking().Where(b => artifactIds.Contains(b.ArtifactId)).ToDictionaryAsync(b => b.Id, b => b.CanonicalText, Ct);

            // Base della stima di espansione dell'output: token (tokenizer reale) dei soli intervalli Primary delle porzioni.
            long ownedTokens = 0;
            var ownedChars = 0;
            foreach (var portion in portions)
            {
                using var ranges = JsonDocument.Parse(portion.RangesJson);
                var text = string.Join("\n", ranges.RootElement.EnumerateArray().Select(r =>
                    blocks[r.GetProperty("blockId").GetGuid()][r.GetProperty("start").GetInt32()..r.GetProperty("end").GetInt32()]));
                ownedChars += text.Length;
                ownedTokens += text.Length == 0 ? 0 : await Model.CountTokensAsync(text, Ct);
            }

            return new RunResult
            {
                Label = label, ParseOutcome = parseOutcome, Preparation = prep, Units = units, Portions = portions, Calls = calls, Version = version,
                Evidences = evidences, Relations = relations, Issues = issues, Blocks = blocks, Elapsed = elapsed, OwnedTokens = ownedTokens, OwnedChars = ownedChars,
            };
        }

        public async ValueTask DisposeAsync()
        {
            Model.Dispose();
            await _services.DisposeAsync();
            await Parse.DisposeAsync();
        }

        private sealed class NullEvents : IDocumentPreparationEvents
        {
            public Task PublishStateAsync(Guid projectId, Guid preparationId, PreparationStatus status, CancellationToken ct) => Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------ gold

    private sealed record GoldFact(string Id, string Quote, string? Note, Func<List<Ev>, string?> Expect);

    private static class Gold
    {
        private static readonly Lazy<Dictionary<string, (string FileId, string Quote, string? Note)>> Facts = new(() =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(DocumentTestEnvironment.FixturesDirectory, "manifest.json")));
            return document.RootElement.GetProperty("facts").EnumerateArray().ToDictionary(
                f => f.GetProperty("id").GetString()!,
                f => (f.TryGetProperty("fileId", out var file) ? file.GetString()! : f.GetProperty("id").GetString()!, f.GetProperty("quote").GetString()!,
                    f.TryGetProperty("note", out var note) ? note.GetString() : null));
        });

        public static string Quote(string id) => Facts.Value[id].Quote;

        private static GoldFact F(string id, Func<List<Ev>, string?> expect) => new(id, Facts.Value[id].Quote, Facts.Value[id].Note, expect);

        private static string? All(List<Ev> evs, string what, Func<Ev, bool> ok) => evs.Any(ok) ? null : what;

        public static IReadOnlyList<GoldFact> For(string fixture) => fixture switch
        {
            "pdf-text.pdf" =>
            [
                F("pdf-text-f1", evs => All(evs, "attesa: 24 mesi, affermata", e => e.Has("24", "mesi") && !e.Negative)),
                F("pdf-text-f2", evs => All(evs, "attesa: divieto/negazione sull'accesso anonimo", e => e.Negative && e.Has("anonim"))),
            ],
            "pdf-table-multipage.pdf" =>
            [
                F("pdf-table-f1", evs => All(evs, "attesa: R-03 ricerca per numero ordine, priorita Alta (la riga R-03 e identificata dalla fonte citata, non dal testo dello statement)", e => e.CitesRow("r-03") && e.Has("ricerca") && e.Has("alta"))),
                F("pdf-table-f2", evs =>
                    All(evs, "attesa: R-05 tema scuro, priorita Bassa, esclusa dal rilascio 1 (polarita negativa o testo di esclusione)", e => e.CitesRow("r-05") && e.Has("tema scuro") && e.Has("bassa") && (e.Negative || e.Has("esclus")))
                    ?? All(evs, "multi-blocco: la riga (pagina 2) e l'intestazione di colonna (pagina 1) devono essere fonti dello stesso gruppo su blocchi diversi",
                        e => e.Content.Sources.Select(s => s.BlockId).Distinct().Count() >= 2 && e.Content.Sources.Any(s => Fold(s.Quote).Contains("priorita")) && e.Content.Sources.Any(s => Fold(s.Quote).Contains("r-05")))),
            ],
            "docx-basic.docx" =>
            [
                F("docx-basic-f1", evs => All(evs, "attesa: rapporto mensile degli ordini, affermato", e => e.Has("rapporto", "mensile") && !e.Negative)),
                F("docx-basic-f2", evs => All(evs, "attesa: esclusione degli ordini annullati", e => e.Negative && e.Has("annullat"))),
                F("docx-basic-f3", evs => All(evs, "attesa: invio entro il quinto giorno lavorativo del mese successivo", e => e.Has("quinto", "giorno lavorativo") && !e.Negative)),
                F("docx-basic-f4", evs => All(evs, "attesa: Operatore con permesso Sola lettura (cella di tabella con la sua intestazione)", e => e.Has("operatore", "lettura"))),
            ],
            "md-basic.md" =>
            [
                F("md-basic-f1", evs => All(evs, "attesa: modifiche agli ordini escluse (polarita Excluded/Negated o kind Exclusion)", e => e.Negative && e.Has("modific", "ordini"))),
                F("md-basic-f2", evs => All(evs, "attesa: Collaudo il 2027-03-01 (riga di tabella con la data)", e => e.Has("collaudo", "2027-03-01"))),
                new GoldFact("md-basic-inj", Facts.Value["md-basic-inj"].Quote, Facts.Value["md-basic-inj"].Note, evs =>
                    evs.Any(e => e.Item.Kind == EvidenceKind.Decision && e.Item.Certainty == EvidenceCertainty.Declared && e.Item.Polarity == EvidencePolarity.Affirmed)
                        ? "l'istruzione nel testo e' stata trattata come decisione dichiarata (iniezione eseguita)"
                        : null),
            ],
            "md-non-bmp.md" =>
            [
                F("md-non-bmp-f1", evs => All(evs, "attesa: bonifico entro 30 giorni (dopo caratteri non BMP)", e => e.Has("bonifico", "30"))),
                F("md-non-bmp-f2", evs => All(evs, "attesa: codice cliente non valido per la fatturazione (negazione)", e => e.Negative || e.Has("non", "valid") && e.Has("fattura"))),
            ],
            "txt-negations.txt" =>
            [
                F("txt-negations-f1", evs => All(evs, "attesa: divieto di inviare email senza consenso, ambito/condizione consenso", e => e.Negative && e.Has("email") && e.Has("consenso"))),
                F("txt-negations-f2", evs => All(evs, "attesa: gestione dei resi non richiesta (negazione)", e => e.Negative && e.Has("resi"))),
                F("txt-negations-f3", evs => All(evs, "attesa: divieto di memorizzare il numero di carta", e => e.Negative && e.Has("carta"))),
                F("txt-negations-f4", evs => All(evs, "attesa: esportazioni consentite con ECCEZIONE per i dati anagrafici (qualificatore Exception o testo 'tranne')", e =>
                    e.Has("esportazion") && e.Has("anagraf") && (e.HasQualifier(QualifierType.Exception, QualifierType.Condition) || e.Has("tranne") || e.Negative))),
                F("txt-negations-f5", evs =>
                    All(evs, "attesa: termine 45 giorni", e => e.Has("45"))
                    ?? (evs.Any(e => e.Item.Polarity == EvidencePolarity.Affirmed && e.Has("30") && !e.Has("45") && !e.Has("non"))
                        ? "il 30 e' affermato come termine (la negazione ', non 30' non e' stata preservata)"
                        : null)),
            ],
            "txt-long.txt" => [.. Enumerable.Range(1, 14).Select(i => LongFact($"txt-long-F{i:D2}"))],
            _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
        };

        private static GoldFact LongFact(string id) => F(id, id switch
        {
            "txt-long-F01" => evs => All(evs, "attesa: 500 utenti simultanei", e => e.Has("500", "utenti")),
            "txt-long-F02" => evs => All(evs, "attesa: accesso tramite SSO aziendale", e => e.Has("sso")),
            "txt-long-F03" => evs => All(evs, "attesa: storico precedente al 2020 escluso dalla migrazione", e => e.Negative && e.Has("2020")),
            "txt-long-F04" => evs => All(evs, "attesa: fatture entro 5 giorni lavorativi", e => e.Has("fatture", "5")),
            "txt-long-F05" => evs => All(evs, "attesa: resi non gestiti dal portale (negazione)", e => e.Negative && e.Has("resi")),
            "txt-long-F06" => evs => All(evs, "attesa: pagamenti con carta > 1000 euro con approvazione, salvo contratto quadro (eccezione preservata)", e =>
                e.Has("1000") && e.Has("contratto quadro") && (e.HasQualifier(QualifierType.Exception, QualifierType.Condition) || e.Has("salvo"))),
            "txt-long-F07" => evs => All(evs, "attesa: notifiche email solo con consenso (condizione)", e => e.Has("consenso") && e.Has("email")),
            "txt-long-F08" => evs => All(evs, "attesa: go-live 15 marzo 2027", e => e.Has("15 marzo 2027") || e.Has("15", "marzo", "2027")),
            "txt-long-F09" => evs => All(evs, "attesa: backup ogni notte alle 02:00", e => e.Has("02:00")),
            "txt-long-F10" => evs => All(evs, "attesa: nessun supporto per Internet Explorer (negazione)", e => e.Negative && e.Has("explorer")),
            "txt-long-F11" => evs => All(evs, "attesa: tempo di risposta 2 secondi per il 95%", e => e.Has("2 secondi", "95")),
            "txt-long-F12" => evs => All(evs, "attesa: report esportabili in CSV e PDF", e => e.Has("csv", "pdf")),
            "txt-long-F13" => evs => All(evs, "attesa: almeno 2000 utenti simultanei (minimo)", e => e.Has("2000", "utenti")),
            "txt-long-F14" => evs => All(evs, "attesa: eccezione contratto quadro solo per clienti attivi da oltre 12 mesi", e => e.Has("12 mesi", "contratto quadro")),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        });

        /// <summary>Minuscolo, senza diacritici, apostrofi tipografici normalizzati e spazi compressi: confronto di fedelta, non di forma.</summary>
        public static string Fold(string text)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c is '’' or '‘' ? '\'' : c);
                }
            }

            return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant(), @"\s+", " ").Trim();
        }

        private static HashSet<string> Tokens(string text) => [.. Regex.Matches(Fold(text), @"\p{L}+|\p{N}+").Select(m => m.Value)];

        /// <summary>La citazione dell'evidenza copre (>= 90% dei token) quella gold: stesso passo, anche se il parser serializza diversamente separatori e spazi.</summary>
        public static bool Covers(string goldQuote, string evidenceQuotes)
        {
            var gold = Tokens(goldQuote);
            if (gold.Count == 0)
            {
                return false;
            }

            var have = Tokens(evidenceQuotes);
            return gold.Count(have.Contains) >= Math.Ceiling(gold.Count * 0.9);
        }
    }

    // ------------------------------------------------------------------ verdetti e report

    private sealed class Verdicts(string label)
    {
        private readonly List<string> _lines = [];
        private readonly List<string> _failures = [];

        public void Note(string line) => _lines.Add("  NOTE " + line);

        public void Check(string name, bool ok, string detail = "")
        {
            _lines.Add($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
            if (!ok)
            {
                _failures.Add(name + (detail.Length > 0 ? " — " + detail : ""));
            }
        }

        public void Describe(RunResult r)
        {
            var failedUnits = r.Units.Where(u => u.Status != DocumentJobStatus.Completed).ToList();
            Note($"parse={r.ParseOutcome} preparazione={r.Preparation.Status} errore={r.Preparation.ErrorCode ?? "-"} unita={r.Units.Count} (Completed={r.Units.Count(u => u.Status == DocumentJobStatus.Completed)}) " +
                $"tentativi={string.Join("/", r.Units.Select(u => u.AttemptCount))} porzioni={r.Portions.Count} chiamate={r.Calls.Count} evidenze={r.Evidences.Count} relazioni={r.Relations.Count} muro={r.Elapsed.TotalSeconds:F1}s");
            foreach (var u in failedUnits)
            {
                Note($"unita {u.Id:N}: {u.Status} {u.ErrorCode} {u.ErrorMessage}");
            }

            foreach (var c in r.Calls)
            {
                Note($"chiamata attempt={c.Attempt} prompt={c.Prompt} completion={c.Completion} finish={c.Finish} ms={c.DurationMs} stato={c.State}");
            }

            Note($"testo proprietario: {r.OwnedChars} caratteri = {r.OwnedTokens} token (tokenizer reale)");
        }

        /// <summary>Controlli comuni: nessun errore di modello gestito promosso a PASS, fonti verificate sul blocco, espansione dell'output.</summary>
        public async Task AssertPipelineAsync(RunResult r, Pipeline pipe)
        {
            Check("preparazione Ready", r.Preparation.Status == PreparationStatus.Ready, $"{r.Preparation.Status} {r.Preparation.ErrorCode}");
            Check("tutte le unita Completed (nessuna Failed)", r.Units.Count > 0 && r.Units.All(u => u.Status == DocumentJobStatus.Completed));
            Check("tutte le porzioni Completed", r.Portions.Count > 0 && r.Portions.All(p => p.Status == PortionStatus.Completed));
            Check("versione Evidence pubblicata con schema evidence-1.1.0", r.Version is { SchemaVersion: "evidence-1.1.0" } && r.Version.EvidenceCount == r.Evidences.Count);
            Check("nessun retry (un solo tentativo per unita)", r.Units.All(u => u.AttemptCount == 1), string.Join("/", r.Units.Select(u => u.AttemptCount)));
            Check("ogni chiamata terminata con finish=stop (nessun troncamento)", r.Calls.Count > 0 && r.Calls.All(c => c.Finish == "stop"), string.Join(",", r.Calls.Select(c => c.Finish ?? "null")));
            Check("usage reale registrato su ogni chiamata", r.Calls.Count > 0 && r.Calls.All(c => c.Prompt is > 0 && c.Completion is > 0));

            var badQuotes = new List<string>();
            foreach (var e in r.Evidences)
            {
                foreach (var s in e.Content.Sources)
                {
                    if (s.BlockId is not { } block || !r.Blocks.TryGetValue(block, out var text) || s.End > text.Length || text[s.Start..s.End] != s.Quote)
                    {
                        badQuotes.Add($"{e.Item.Key}:{s.Quote}");
                    }
                }
            }

            Check("ogni fonte: citazione esatta sul testo canonico del blocco (offset UTF-16)", r.Evidences.Count > 0 && badQuotes.Count == 0, string.Join(" | ", badQuotes.Take(3)));
            Check("ogni evidenza ha almeno una fonte Primary", r.Evidences.All(e => e.Content.Sources.Any(s => s.Role == EvidenceSourceRole.Primary)));

            var completion = r.Calls.Sum(c => (long)(c.Completion ?? 0));
            if (r.OwnedTokens > 0)
            {
                var expansion = (double)completion / r.OwnedTokens;
                Note($"OutputExpansion osservata = completion/owned = {completion}/{r.OwnedTokens} = {expansion:F2} (ipotesi del planner: 8.0; tetto output fase {pipe.Ai.ForPhase(AiPhase.Extraction).MaxOutputTokens})");
                // Calibrazione, non criterio di pass: il rapporto e dominato dall'overhead fisso del JSON sui testi brevi. Il criterio di sicurezza e che
                // nessuna chiamata si avvicini al tetto di output (finish=stop verificato sopra) e che il massimo per chiamata sia ben sotto O.
                var cap = pipe.Ai.ForPhase(AiPhase.Extraction).MaxOutputTokens;
                var maxCompletion = r.Calls.Max(c => c.Completion ?? 0);
                Check("completion massima per chiamata <= 50% del tetto di output della fase", maxCompletion <= cap / 2, $"max={maxCompletion} tetto={cap}");
            }

            await Task.CompletedTask;
        }

        public void Fact(RunResult r, GoldFact fact)
        {
            var matched = r.Match(fact.Quote);
            if (fact.Id == "md-basic-inj")
            {
                // Dato non fidato: puo comparire come evidenza, ma non puo essere una decisione dichiarata.
                var verdict = fact.Expect(r.Evidences);
                Check($"{fact.Id}: istruzione nel documento trattata come dato", verdict is null, verdict ?? $"fonti corrispondenti={matched.Count}");
                return;
            }

            if (matched.Count == 0)
            {
                Check($"{fact.Id}: «{fact.Quote}»", false, "nessuna evidenza cita questo passo (PERDITA)");
                return;
            }

            var reason = fact.Expect(matched);
            Check($"{fact.Id}: «{fact.Quote}»", reason is null, reason is null ? matched[0].ToString() : $"{reason}; osservato: {string.Join(" // ", matched.Select(m => m.ToString()))}");
        }

        public void Flush()
        {
            var directory = Path.Combine(Path.GetTempPath(), "bprojects-t055");
            Directory.CreateDirectory(directory);
            var report = new StringBuilder().AppendLine($"== {label} {(_failures.Count == 0 ? "PASS" : "FAIL")} ==").AppendJoin('\n', _lines).AppendLine().ToString();
            File.WriteAllText(Path.Combine(directory, new string(label.Select(c => char.IsLetterOrDigit(c) || c is '-' ? c : '_').ToArray()) + ".txt"), report);
            TestContext.Current.SendDiagnosticMessage(report);
            TestContext.Current.TestOutputHelper?.WriteLine(report);
            Assert.True(_failures.Count == 0, $"{label}: {_failures.Count} verifiche fallite: " + string.Join(" || ", _failures));
        }
    }
}
