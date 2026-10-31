using System.Net;
using System.Net.Http.Headers;
using bProjects.App.AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace bProjects.IntegrationTests;

/// <summary>Opzioni di esecuzione (contracts/ai-workflow.md §2): valori approvati, backoff e coerenza.</summary>
public sealed class ExecutionOptionsTests
{
    private static readonly ExecutionOptionsValidator Validator = new();

    [Fact]
    public void Defaults_are_the_approved_initial_values()
    {
        var o = new ExecutionOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), o.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), o.RenewInterval);
        Assert.Equal(3, o.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(180), o.RunDeadline);
        Assert.True(Validator.Validate(null, o).Succeeded);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 10)]
    [InlineData(60, 10)]
    public void Backoff_doubles_from_the_base_and_is_capped(int attemptCount, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), new ExecutionOptions().BackoffAfterAttempt(attemptCount));
    }

    [Fact]
    public void Request_timeout_must_stay_below_the_run_deadline_and_overflow_is_rejected()
    {
        var tooSlow = new AiOptions { RequestTimeoutSeconds = 180 };
        Assert.False(new ExecutionOptionsValidator(Options.Create(tooSlow)).Validate(null, new ExecutionOptions()).Succeeded);
        Assert.True(new ExecutionOptionsValidator(Options.Create(new AiOptions())).Validate(null, new ExecutionOptions()).Succeeded);

        Assert.False(Validator.Validate(null, new ExecutionOptions { RenewIntervalSeconds = 1_100_000_000 }).Succeeded);
    }

    [Theory]
    [InlineData(nameof(ExecutionOptions.RenewIntervalSeconds), 15)] // non minore di metà lease
    [InlineData(nameof(ExecutionOptions.RenewIntervalSeconds), 0)]
    [InlineData(nameof(ExecutionOptions.LeaseDurationSeconds), 0)]
    [InlineData(nameof(ExecutionOptions.MaxAttempts), 0)]
    [InlineData(nameof(ExecutionOptions.RetryBaseDelaySeconds), 11)] // oltre il massimo
    [InlineData(nameof(ExecutionOptions.RunDeadlineSeconds), 20)] // sotto il lease
    public void Incoherent_configuration_is_rejected(string property, int value)
    {
        var o = new ExecutionOptions();
        typeof(ExecutionOptions).GetProperty(property)!.SetValue(o, value);

        Assert.False(Validator.Validate(null, o).Succeeded);
    }
}

/// <summary>Validazione di AiOptions: budget, soglia e allowlist dell'endpoint interno.</summary>
public sealed class AiOptionsValidationTests
{
    private static readonly AiOptionsValidator Validator = new();

    private static AiOptions Valid() => new() { Endpoint = "http://192.168.253.18:8080/v1/", ModelId = "m", ApiKey = "k" };

    [Fact]
    public void Valid_configuration_and_budget_are_accepted()
    {
        var o = Valid();

        Assert.True(Validator.Validate(null, o).Succeeded);
        Assert.Equal(32768 - 4096 - 1024, o.InputBudgetTokens);
        Assert.Equal(o.InputBudgetTokens, o.EffectiveCompactionTriggerTokens);
    }

    [Theory]
    [InlineData("http://localhost:8080/v1/")]
    [InlineData("http://10.1.2.3/v1/")]
    [InlineData("http://172.16.0.5/v1/")]
    [InlineData("http://dgx/v1/")]
    [InlineData("https://dgx.internal/v1/")]
    public void Internal_hosts_are_allowed(string endpoint)
    {
        var o = Valid();
        o.Endpoint = endpoint;

        Assert.True(Validator.Validate(null, o).Succeeded);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/")]
    [InlineData("http://8.8.8.8/v1/")]
    [InlineData("http://172.32.0.1/v1/")]
    [InlineData("http://user:pw@dgx/v1/")]
    [InlineData("http://dgx/v1/?key=x")]
    [InlineData("ftp://dgx/v1/")]
    [InlineData("dgx/v1")]
    [InlineData("")]
    public void External_or_malformed_endpoints_are_rejected(string endpoint)
    {
        var o = Valid();
        o.Endpoint = endpoint;

        Assert.False(Validator.Validate(null, o).Succeeded);
    }

    [Fact]
    public void Explicit_allowlist_admits_a_named_host()
    {
        var o = Valid();
        o.Endpoint = "http://dgx.corp.example/v1/";
        Assert.False(Validator.Validate(null, o).Succeeded);

        o.AllowedHosts = ["dgx.corp.example"];
        Assert.True(Validator.Validate(null, o).Succeeded);
    }

    [Theory]
    [InlineData(nameof(AiOptions.ContextWindowTokens), 0)]
    [InlineData(nameof(AiOptions.MaxOutputTokens), 0)]
    [InlineData(nameof(AiOptions.SafetyMarginTokens), -1)]
    [InlineData(nameof(AiOptions.MaxOutputTokens), 32768)] // budget di input non positivo
    [InlineData(nameof(AiOptions.CompactionTriggerTokens), 27649)] // oltre il budget (27648)
    [InlineData(nameof(AiOptions.CompactionTriggerTokens), -5)]
    [InlineData(nameof(AiOptions.RequestTimeoutSeconds), 0)]
    [InlineData(nameof(AiOptions.StructuredOutputMode), 99)]
    public void Invalid_budget_or_settings_are_rejected(string property, int value)
    {
        var o = Valid();
        var p = typeof(AiOptions).GetProperty(property)!;
        p.SetValue(o, p.PropertyType.IsEnum ? Enum.ToObject(p.PropertyType, value) : value);

        Assert.False(Validator.Validate(null, o).Succeeded);
    }

    [Theory]
    [InlineData(nameof(AiOptions.ModelId))]
    [InlineData(nameof(AiOptions.ApiKey))]
    public void Missing_model_or_key_is_rejected(string property)
    {
        var o = Valid();
        typeof(AiOptions).GetProperty(property)!.SetValue(o, " ");

        Assert.False(Validator.Validate(null, o).Succeeded);
    }

    [Fact]
    public void Trigger_at_the_edges_is_accepted()
    {
        var o = Valid();
        o.CompactionTriggerTokens = 1;
        Assert.True(Validator.Validate(null, o).Succeeded);

        o.CompactionTriggerTokens = o.InputBudgetTokens;
        Assert.True(Validator.Validate(null, o).Succeeded);
    }
}

/// <summary>Avvio, Problem Details e log dell'applicazione reale con un DGX irraggiungibile.</summary>
public sealed class ResilientStartupTests
{
    [Fact]
    public async Task App_starts_and_serves_health_while_the_model_is_unreachable()
    {
        await using var app = new TestApp(); // Ai:Endpoint punta a una porta chiusa
        using var client = app.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_ai_configuration_prevents_startup_without_leaking_the_key()
    {
        const string secret = "chiave-segreta-di-prova";
        await using var app = new TestApp().WithWebHostBuilder(b => b.UseSetting("Ai:Endpoint", "https://api.openai.com/v1/").UseSetting("Ai:ApiKey", secret));

        var ex = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        Assert.Contains("Ai:Endpoint", ex.ToString());
        Assert.DoesNotContain(secret, ex.ToString());
    }

    [Fact]
    public async Task Unauthenticated_request_gets_problem_details()
    {
        await using var app = new TestApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/projects", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), HttpStatusCode.ServiceUnavailable, "model_unreachable")]
    [InlineData(typeof(InvalidOperationException), HttpStatusCode.InternalServerError, "internal_error")]
    public async Task Unhandled_errors_become_problem_details_without_message_or_content(Type exceptionType, HttpStatusCode status, string code)
    {
        const string sensitive = "contenuto-integrale-del-progetto";
        var logs = new CapturingLoggerProvider();
        await using var app = new TestApp().WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IStartupFilter>(new ThrowingFilter(() => (Exception)Activator.CreateInstance(exceptionType, sensitive)!));
                s.Configure<AuthorizationOptions>(o => o.FallbackPolicy = null); // il percorso /boom esiste solo nel test
            });
            b.ConfigureLogging(l => l.AddProvider(logs));
        });
        using var client = app.CreateClient();

        var response = await client.GetAsync("/boom", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains($"\"code\":\"{code}\"", body);
        Assert.DoesNotContain(sensitive, body);
        Assert.DoesNotContain(sensitive, logs.Text);
    }

    [Fact]
    public async Task Startup_log_reports_the_model_configuration_but_never_the_key()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = new TestApp().WithWebHostBuilder(b => b
            .UseSetting("Ai:ApiKey", "chiave-segreta-di-prova")
            .ConfigureLogging(l => l.AddProvider(logs)));

        using var client = app.CreateClient();

        Assert.Contains("127.0.0.1", logs.Text);
        Assert.Contains("test-model", logs.Text);
        Assert.DoesNotContain("chiave-segreta-di-prova", logs.Text);
    }

    private sealed class ThrowingFilter(Func<Exception> make) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use((context, n) => context.Request.Path == "/boom" ? throw make() : n());
        };
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public string Text
        {
            get
            {
                lock (_lines)
                {
                    return string.Join('\n', _lines);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines)
                {
                    owner._lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
                }
            }
        }
    }
}
