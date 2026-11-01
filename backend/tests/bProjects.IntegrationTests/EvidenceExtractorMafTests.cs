using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bProjects.App.AI;
using bProjects.App.Evidence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>
/// T071 — l'estrattore reale (MAF, fase Extraction come infra/compose.yaml) contro il DGX Spark, attraverso l'app, il dispatcher e il
/// registro veri, una inferenza alla volta (parallel=1: nessun'altra coda). Gli esempi N1–N4 del mandato §3.2 sono attese generiche sul
/// registro (tipo, polarità, struttura, relazioni), mai regole nel codice; più il caso «nessuna evidenza» motivato. Le citazioni sono
/// verificate dal validatore del server. Registra solo numeri e durate. Servono BPROJECTS_TEST_POSTGRES e BPROJECTS_TEST_DGX_*.
/// </summary>
[Trait("Category", TestCategories.MafReal)]
public sealed class EvidenceExtractorMafTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _database.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _model?.Dispose();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task N1_two_functions_in_one_sentence_become_separately_verifiable_evidences()
    {
        var (items, _) = await ExtractAsync("N1", "Vogliamo una conferma via email dopo la prenotazione e un promemoria il giorno prima.");

        Assert.True(items.Count >= 2, $"attese almeno due evidenze, trovate {items.Count}");
        var confirmation = items.Where(e => Text(e).Contains("conferm", StringComparison.OrdinalIgnoreCase)).ToList();
        var reminder = items.Where(e => Text(e).Contains("promemoria", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(confirmation);
        Assert.NotEmpty(reminder);
        Assert.Contains(reminder, e => Text(e).Contains("giorno prima", StringComparison.OrdinalIgnoreCase));   // il tempo resta col promemoria
        Assert.DoesNotContain(confirmation, e => reminder.Contains(e));                                         // due evidenze distinte
    }

    [Fact]
    public async Task N2_a_negated_channel_keeps_its_scope_and_the_allowed_channel_is_a_separate_constraint()
    {
        var (items, _) = await ExtractAsync("N2", "Per le notifiche ai clienti: niente notifiche push, solo email.");

        var push = items.Where(e => Text(e).Contains("push", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(push, e => e.GetProperty("polarity").GetString() is "Negated" or "Excluded" or "Prohibited"
            || e.GetProperty("kind").GetString() == "Exclusion");
        Assert.Contains(push, e => Text(e).Contains("notific", StringComparison.OrdinalIgnoreCase));            // ambito: le notifiche, non tutto
        Assert.Contains(items, e => Text(e).Contains("email", StringComparison.OrdinalIgnoreCase) && e.GetProperty("polarity").GetString() == "Affirmed");
    }

    [Fact]
    public async Task N3_the_instrument_stays_attached_to_its_action_and_actor()
    {
        var (items, _) = await ExtractAsync("N3", "Gli addetti registrano carichi e scarichi usando il lettore di codici a barre.");

        Assert.Contains(items, e =>
            Field(e, "instrument").Contains("lettore", StringComparison.OrdinalIgnoreCase)
            && (Field(e, "action") + " " + e.GetProperty("statement").GetString()).Contains("registr", StringComparison.OrdinalIgnoreCase)
            && (Field(e, "subject") + " " + e.GetProperty("statement").GetString()).Contains("addett", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task N4_positions_from_different_people_stay_alternatives_with_distinct_sources_and_no_declared_decision()
    {
        var (items, client) = await ExtractAsync("N4",
            "Il responsabile IT chiede l'autenticazione con SSO aziendale entro marzo.",
            "Il commerciale invece propone login con email e password già dal primo rilascio.");

        var sequences = items.Select(e => e.GetProperty("messageSequence").GetInt64()).Distinct().ToList();
        Assert.True(sequences.Count >= 2, "ogni posizione ha la propria fonte");
        Assert.DoesNotContain(items, e => e.GetProperty("kind").GetString() == "Decision" && e.GetProperty("certainty").GetString() == "Declared");
        var relations = new List<JsonElement>();
        foreach (var e in items.Where(e => e.GetProperty("messageSequence").GetInt64() == sequences.Max()))
        {
            var detail = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{_project}/evidence/{e.GetProperty("id").GetGuid()}", Ct);
            relations.AddRange(detail.GetProperty("relations").EnumerateArray().Select(r => r.Clone()));
        }

        Assert.Contains(relations, r => r.GetProperty("type").GetString() is "AlternativeTo" or "Contradicts");
        Assert.DoesNotContain(relations, r => r.GetProperty("type").GetString() is "Supersedes" or "Corrects");   // nessuna sostituzione silenziosa
    }

    [Fact]
    public async Task An_explicit_correction_is_a_new_evidence_that_declares_Corrects_towards_the_corrected_one()
    {
        var (items, client) = await ExtractAsync("rettifica",
            "Le richieste di ferie vanno presentate con 5 giorni di anticipo.",
            "Correggo: intendevo 10 giorni di anticipo, non 5.");

        var later = items.Where(e => e.GetProperty("messageSequence").GetInt64() > 1).ToList();
        Assert.Contains(later, e => Text(e).Contains("10", StringComparison.Ordinal));
        var relations = new List<JsonElement>();
        foreach (var e in later)
        {
            var detail = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{_project}/evidence/{e.GetProperty("id").GetGuid()}", Ct);
            relations.AddRange(detail.GetProperty("relations").EnumerateArray().Where(r => r.GetProperty("direction").GetString() == "outgoing").Select(r => r.Clone()));
        }

        Assert.Contains(relations, r => r.GetProperty("type").GetString() is "Corrects" or "Supersedes"
            && r.GetProperty("basis").GetString() == "Declared" && r.GetProperty("otherMessageSequence").GetInt64() == 1);
        Assert.Contains(items, e => e.GetProperty("messageSequence").GetInt64() == 1);   // il fatto corretto resta nel registro, non è riscritto
    }

    [Fact]
    public async Task A_message_without_facts_is_completed_with_a_reasoned_zero_and_not_as_a_failure()
    {
        var (items, client) = await ExtractAsync("vuoto", "Grazie, ci sentiamo domani.");

        Assert.Empty(items);
        var status = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{_project}/evidence/status", Ct);
        var message = status.GetProperty("messages")[0];
        Assert.Equal("empty", message.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("currentVersion").GetProperty("noEvidenceReason").GetString()));
    }

    // ---------------------------------------------------------------- supporto

    private Guid _project;
    private ApiHarness? _app;
    private InternalModelClient? _model;

    /// <summary>Progetto nuovo, messaggi PM in ordine, attesa dei lavori (estrattore reale); ritorna le evidenze correnti del progetto.</summary>
    private async Task<(List<JsonElement> Items, HttpClient Client)> ExtractAsync(string label, params string[] messages)
    {
        var options = TestEnvironment.RequireProductionLikeOptions();
        TestEnvironment.RequirePostgres();
        var model = _model = new InternalModelClient(options, MafDiagnostics.Wrap());
        var factory = AgentFactory.Create(options, model);
        var app = _app = await ApiHarness.StartAsync(_database.ConnectionString, configure: services =>
            services.AddSingleton<IEvidenceExtractionExecutor>(sp => new EvidenceExtractor(
                sp.GetRequiredService<IServiceScopeFactory>(), factory,
                model.CreateChatOptions(EvidenceExtractionSchema.Base, EvidenceExtractionSchema.Name, temperature: 0, phase: AiPhase.Extraction),
                sp.GetRequiredService<EvidenceEvents>())));
        var client = await app.LoginAsync();
        var created = await client.PostAsJsonAsync("/api/projects", new { name = $"Estrazione reale {label} {Guid.NewGuid():N}"[..40] }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _project = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var clock = Stopwatch.StartNew();
        foreach (var text in messages)
        {
            var post = await client.PostAsJsonAsync($"/api/projects/{_project}/messages", new { clientMessageId = Guid.NewGuid(), text }, Ct);
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        }

        var deadline = DateTime.UtcNow.AddSeconds(options.ForPhase(AiPhase.Extraction).RequestTimeoutSeconds * messages.Length + 60);
        while (await Scalar<long>("""SELECT count(*) FROM "EvidenceExtractionJobs" WHERE "Status" IN ('Queued','Running')""") > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "estrazioni non concluse entro il tempo previsto");
            await Task.Delay(500, Ct);
        }

        clock.Stop();
        var failed = await Scalar<string?>("""SELECT string_agg("ErrorCode" || ': ' || coalesce("ErrorMessage", ''), ' | ') FROM "EvidenceExtractionJobs" WHERE "Status" = 'Failed'""");
        Assert.True(failed is null, $"estrazione fallita: {failed}");
        Report(label, clock.Elapsed);

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{_project}/evidence", Ct);
        return ([.. list.GetProperty("items").EnumerateArray().Select(e => e.Clone())], client);
    }

    private static string Text(JsonElement e) =>
        e.GetProperty("statement").GetString() + " " + string.Join(" ", e.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("quote").GetString()));

    private static string Field(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    /// <summary>Solo numeri: durata a muro, durata e token per chiamata, evidenze per messaggio.</summary>
    private void Report(string label, TimeSpan elapsed)
    {
        using var connection = new NpgsqlConnection(_database.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""
            SELECT j."MessageSequence", j."AttemptCount", c."DurationMs", c."PromptTokens", c."CompletionTokens", c."FinishReason", v."EvidenceCount"
            FROM "EvidenceExtractionJobs" j
            LEFT JOIN "InferenceCalls" c ON c."EvidenceExtractionJobId" = j."Id" AND c."IsFinal"
            LEFT JOIN "EvidenceExtractionVersions" v ON v."Id" = j."ResultVersionId"
            ORDER BY j."MessageSequence"
            """, connection);
        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            string Col(int i) => reader.IsDBNull(i) ? "n/d" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            lines.Add($"seq={Col(0)} tentativi={Col(1)} ms={Col(2)} prompt={Col(3)} completion={Col(4)} finish={Col(5)} evidenze={Col(6)}");
        }

        var line = $"T071 estrazione {label}: muro={elapsed.TotalSeconds:F1}s " + string.Join(" ; ", lines);
        TestContext.Current.SendDiagnosticMessage(line);
        TestContext.Current.TestOutputHelper?.WriteLine(line);
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)result;
    }
}
