using System.Text;
using bProjects.App.AI;
using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
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
builder.Services.AddSingleton<IValidateOptions<DocumentOptions>, DocumentOptionsValidator>();
builder.Services.AddOptions<DocumentOptions>().Bind(builder.Configuration.GetSection(DocumentOptions.SectionName)).ValidateOnStart();
builder.Services.AddScoped<DocumentPolicyService>();
builder.Services.AddScoped<DocumentUploadService>();
builder.Services.AddScoped<DocumentPreparationService>();
builder.Services.AddScoped<SourceSelectionService>();
builder.Services.AddScoped<SourceSnapshotStore>();
builder.Services.AddScoped<DocumentCatalog>();
builder.Services.AddSingleton<DocumentFileStore>();   // originali su volume privato (T011); la radice si valida al primo uso
builder.Services.AddDocumentParsing(builder.Configuration);   // worker CPU del parsing (T029): claim SQL e lease propri, nessuno slot DGX; inattivo senza Parser:BaseUrl
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
builder.Services.AddSingleton<bProjects.App.AI.DispatchWakeup>();
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
// Registro atomico delle evidenze: estrattore MAF della fase Extraction nello stesso dispatcher (priorità inferiore alla chat).
builder.Services.AddSingleton<bProjects.App.Evidence.EvidenceEvents>();
builder.Services.AddSingleton<IEvidenceExtractionExecutor>(sp =>
{
    var model = sp.GetRequiredService<InternalModelClient>();
    return new EvidenceExtractor(
        sp.GetRequiredService<IServiceScopeFactory>(),
        AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>()),
        model.CreateChatOptions(bProjects.App.Evidence.EvidenceExtractionSchema.Base, bProjects.App.Evidence.EvidenceExtractionSchema.Name, temperature: 0, phase: AiPhase.Extraction),
        sp.GetRequiredService<bProjects.App.Evidence.EvidenceEvents>());
});
// Estrazione delle evidenze documentali per unità (T049/T050): esecutore delle unità nello stesso dispatcher e nello stesso slot AI (nessun secondo
// scheduler). Il conteggio del payload documentale usa la calibrazione identificata di Documents:Calibration; senza, il ramo resta runtime_unverified.
builder.Services.AddSingleton<IDocumentUnitExecutor>(sp =>
{
    var model = sp.GetRequiredService<InternalModelClient>();
    var documentOptions = sp.GetRequiredService<IOptions<DocumentOptions>>();
    return new DocumentEvidenceAgent(
        sp.GetRequiredService<IServiceScopeFactory>(),
        AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>(), documentOptions.Value.Calibration.ToCalibration()),
        model,
        documentOptions);
});
builder.Services.AddHostedService<AiDispatchService>();
builder.Services.AddHostedService<DocumentOrphanSweepService>();   // pulizia periodica degli originali orfani, indipendente dagli upload

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

// Ogni endpoint richiede un PM autenticato, salvo quelli esplicitamente anonimi; i limiti dei documenti sono riservati al ruolo Admin.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(AccountBootstrap.PmRole).Build())
    .AddPolicy(AccountBootstrap.AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(AccountBootstrap.AdminRole));

var app = builder.Build();
_ = app.Services.GetRequiredService<DocumentFileStore>();   // radice fuori webroot: un errore di configurazione emerge all'avvio, non al primo upload

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
            ProblemDetails =
            {
                Status = badRequest.StatusCode,
                Title = badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge ? "Corpo della richiesta troppo grande" : "Richiesta non valida",
                Extensions = { ["code"] = badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge ? "payload_too_large" : "malformed_request" },
            },
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
    "Modello interno configurato: host {Host}, modello {ModelId}, finestra {Window}, budget input chat/analisi/documento/compattazione {Chat}/{Analysis}/{Document}/{Compaction}, output strutturato {Mode}",
    new Uri(ai.Endpoint).Host, ai.ModelId, ai.ContextWindowTokens, ai.ForPhase(AiPhase.Chat).InputBudgetTokens, ai.ForPhase(AiPhase.Analysis).InputBudgetTokens,
    ai.ForPhase(AiPhase.Document).InputBudgetTokens, ai.ForPhase(AiPhase.Compaction).InputBudgetTokens, ai.StructuredOutputMode);
// Una riga per fase: modello, ragionamento, output e timeout effettivi (solo parametri, nessun contenuto né credenziale).
foreach (var phase in Enum.GetValues<AiPhase>())
{
    var settings = ai.ForPhase(phase);
    app.Logger.LogInformation(
        "Fase AI {Phase}: modello {ModelId}, enable_thinking {EnableThinking}, max_completion_tokens {MaxOutputTokens}, timeout richiesta {TimeoutSeconds} s",
        phase, settings.ModelId, settings.EnableThinking, settings.MaxOutputTokens, settings.RequestTimeoutSeconds);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapAuthEndpoints();
app.MapProjectEndpoints();
app.MapConversationEndpoints();
app.MapAnalysisEndpoints();
app.MapDocumentEndpoints();
app.MapDocumentLimitEndpoints();
app.MapProjectDocumentEndpoints();
app.MapSourceSelectionEndpoints();
app.MapProjectEventsEndpoint();
app.MapContextUsageEndpoint();
bProjects.App.Evidence.EvidenceEndpoints.MapEvidenceEndpoints(app);

app.Run();

public partial class Program;
