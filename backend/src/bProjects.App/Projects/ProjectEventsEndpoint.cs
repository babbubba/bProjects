using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using bProjects.App.Persistence;
using Microsoft.EntityFrameworkCore;

namespace bProjects.App.Projects;

/// <summary>
/// Notifiche effimere dello stream UI (contracts/http-api.md): solo in memoria nel processo, nessuna tabella né
/// broker e nessun replay. Ogni sottoscrizione ha un canale limitato che scarta i messaggi più vecchi: un client
/// lento non rallenta né blocca mai il lavoro durabile, e il client riconcilia con i GET.
/// </summary>
public sealed class ProjectEventHub
{
    public const string ChatDelta = "chat.delta";
    public const string ChatState = "chat.state";
    public const string ProcessingState = "processing.state";

    private const int Capacity = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<ProjectEvent>>> _subscribers = new();

    public sealed record ProjectEvent(string Name, string Data);

    public sealed class Subscription(Action dispose, ChannelReader<ProjectEvent> reader) : IDisposable
    {
        public ChannelReader<ProjectEvent> Reader { get; } = reader;

        public void Dispose() => dispose();
    }

    public Subscription Subscribe(Guid projectId)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ProjectEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers.GetOrAdd(projectId, _ => new()).TryAdd(id, channel);
        return new(() =>
        {
            if (_subscribers.TryGetValue(projectId, out var channels))
            {
                channels.TryRemove(id, out _);
            }

            channel.Writer.TryComplete();
        }, channel.Reader);
    }

    /// <summary>Pubblica a chi è connesso in questo momento; senza sottoscrittori non fa nulla.</summary>
    public void Publish(Guid projectId, string name, object payload)
    {
        if (!_subscribers.TryGetValue(projectId, out var channels) || channels.IsEmpty)
        {
            return;
        }

        var evt = new ProjectEvent(name, JsonSerializer.Serialize(payload, Json));
        foreach (var channel in channels.Values)
        {
            channel.Writer.TryWrite(evt);
        }
    }
}

/// <summary>GET /api/projects/{p}/events: stream SSE del progetto. Aprirlo o riaprirlo non avvia mai alcun lavoro AI.</summary>
public static class ProjectEventsEndpoint
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapProjectEventsEndpoint(this IEndpointRouteBuilder app)
    {
        // Stessa autorizzazione (fallback: PM autenticato) e stesso scope degli altri endpoint di progetto.
        app.MapGet("/api/projects/{projectId:guid}/events", Stream);
        return app;
    }

    private static async Task Stream(Guid projectId, HttpContext http, AppDbContext db, ProjectEventHub hub)
    {
        var ct = http.RequestAborted;
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        using var subscription = hub.Subscribe(projectId);
        try
        {
            await Write(response, ": connected\n\n", ct);
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(KeepAlive);
                try
                {
                    if (!await subscription.Reader.WaitToReadAsync(wait.Token))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await Write(response, ": keep-alive\n\n", ct); // commento SSE: tiene aperta la connessione dietro proxy
                    continue;
                }

                while (subscription.Reader.TryRead(out var evt))
                {
                    await Write(response, $"event: {evt.Name}\ndata: {evt.Data}\n\n", ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Il client si è disconnesso: si chiude solo il trasporto, il lavoro durabile non dipende da questa richiesta.
        }
    }

    private static async Task Write(HttpResponse response, string frame, CancellationToken ct)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(frame), ct);
        await response.Body.FlushAsync(ct);
    }
}
