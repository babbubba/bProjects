using System.Text;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Identity;
using bProjects.App.Persistence;
using bProjects.App.Projects;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var authSection = builder.Configuration.GetSection("Auth");
var authOptions = authSection.Get<AuthOptions>() ?? new AuthOptions();
if (authOptions.SigningKey.Length < 32)
{
    throw new InvalidOperationException("Auth:SigningKey è obbligatoria (almeno 32 caratteri) e va fornita fuori dal repository.");
}

builder.Services.Configure<AuthOptions>(authSection);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("AppDb")));

// Configurazione AI ed esecuzione: validata all'avvio (struttura, budget, allowlist), mai la raggiungibilità
// del DGX. Il client è creato al primo uso e non contatta la rete in costruzione.
builder.Services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ExecutionOptions>, ExecutionOptionsValidator>();
builder.Services.AddOptions<AiOptions>().Bind(builder.Configuration.GetSection(AiOptions.SectionName)).ValidateOnStart();
builder.Services.AddOptions<ExecutionOptions>().Bind(builder.Configuration.GetSection(ExecutionOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton(sp => new InternalModelClient(sp.GetRequiredService<IOptions<AiOptions>>().Value));
// Stato verificato del runtime (cache a TTL, sonda lazy alla prima lettura): non blocca mai l'avvio né i progetti con il DGX offline.
builder.Services.AddSingleton(sp => new ModelRuntimeMonitor(
    sp.GetRequiredService<InternalModelClient>(),
    sp.GetRequiredService<IOptions<AiOptions>>().Value,
    sp.GetRequiredService<ILogger<ModelRuntimeMonitor>>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddProblemDetails();

// Dispatch dei ChatTurn (T014) con ConversationalAgent (T017) e commit atomico della risposta (T018).
builder.Services.AddSingleton<ProjectEventHub>(); // notifiche SSE effimere in-process (T021)
builder.Services.AddSingleton<ChatTurnEvents>();
builder.Services.AddSingleton<ProcessingRunEvents>();
builder.Services.AddScoped<ExecutionLeaseStore>();
builder.Services.AddSingleton(sp =>
{
    var model = sp.GetRequiredService<InternalModelClient>();
    return new ConversationalAgent(
        AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>()),
        model.CreateChatOptions(temperature: 0.2f, phase: AiPhase.Chat));
});
builder.Services.AddScoped<IChatTurnCommitter, ChatTurnCompletion>();
builder.Services.AddScoped<bProjects.App.Analysis.IAnalysisRunStore, bProjects.App.Analysis.AnalysisRunCompletion>();
builder.Services.AddScoped<bProjects.App.Analysis.PreviewRevisionService>();
builder.Services.AddSingleton(sp => new AnalysisWorkflow(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ConversationalAgent>(),
    sp.GetRequiredService<InternalModelClient>().CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0, phase: AiPhase.Analysis)));
builder.Services.AddScoped<bProjects.App.Analysis.IDocumentRunStore, bProjects.App.Analysis.DocumentRunCompletion>();
builder.Services.AddSingleton(sp =>
{
    var model = sp.GetRequiredService<InternalModelClient>();
    return new DocumentAgent(
        AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>()),
        model.CreateChatOptions(temperature: 0.2f, phase: AiPhase.Document));
});
builder.Services.AddSingleton(sp => new DocumentWorkflow(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<DocumentAgent>()));
builder.Services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
builder.Services.AddHostedService<AiDispatchService>();

// Il middleware del framework registrerebbe il messaggio dell'eccezione (può contenere contenuti dei progetti):
// il log degli errori non gestiti è quello sicuro dell'handler sotto.
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);

builder.Services
    .AddIdentityCore<IdentityUser<Guid>>(options =>
    {
        options.Password.RequiredLength = 12;
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = authOptions.Issuer,
            ValidAudience = authOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role",
        };
    });

// Ogni endpoint richiede un PM autenticato, salvo quelli esplicitamente anonimi.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(AccountBootstrap.PmRole).Build());

var app = builder.Build();

// Operazioni esplicite e non distruttive, mai eseguite all'avvio del server.
if (args.Contains("migrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    return;
}

if (args.Contains("bootstrap-accounts"))
{
    var created = await AccountBootstrap.RunAsync(app.Services, app.Configuration);
    Console.WriteLine($"Account PM creati: {created}");
    return;
}

// Errori non gestiti: Problem Details senza messaggi o contenuti; il log riporta solo tipo e classe di errore.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (context.RequestAborted.IsCancellationRequested)
    {
        return; // il browser ha chiuso la connessione: non è un guasto del modello né dell'app
    }

    if (error is BadHttpRequestException badRequest)
    {
        // Richiesta malformata (JSON non valido, parametro non convertibile): errore del client, non del servizio.
        context.Response.StatusCode = badRequest.StatusCode;
        await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = badRequest.StatusCode, Title = "Richiesta non valida", Extensions = { ["code"] = "malformed_request" } },
        });
        return;
    }

    var failure = error is null ? default : ModelFailure.Classify(error);
    var transient = error is not null && failure.IsTransient;
    var status = transient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
    var code = transient ? failure.Code : "internal_error";
    app.Logger.LogError("Richiesta fallita: {ExceptionType}, codice {Code}", error?.GetType().Name ?? "unknown", code);

    context.Response.StatusCode = status;
    await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
    {
        HttpContext = context,
        ProblemDetails =
        {
            Status = status,
            Title = transient ? "Servizio temporaneamente non disponibile" : "Errore interno",
            Extensions = { ["code"] = code },
        },
    });
}));
app.UseStatusCodePages();

var ai = app.Services.GetRequiredService<IOptions<AiOptions>>().Value;
app.Logger.LogInformation(
    "Modello interno configurato: host {Host}, modello {ModelId}, finestra {Window}, budget input {Budget}, output strutturato {Mode}",
    new Uri(ai.Endpoint).Host, ai.ModelId, ai.ContextWindowTokens, ai.InputBudgetTokens, ai.StructuredOutputMode);

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapAuthEndpoints();
app.MapProjectEndpoints();
app.MapConversationEndpoints();
app.MapAnalysisEndpoints();
app.MapDocumentEndpoints();
app.MapProjectEventsEndpoint();

app.Run();

public partial class Program;
