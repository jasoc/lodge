using System.Text.Json;
using Lodge.Infrastructure.Events;

namespace Lodge.Server.Endpoints;

/// <summary>
/// A Server-Sent Events stream of change invalidations, so an open UI refreshes the moment
/// something changes instead of polling. Events carry no data to render (type, kind,
/// instance id): the client re-reads through the normal endpoints, which keeps every
/// permission check where it already is. A comment line is sent every
/// <see cref="HeartbeatSeconds"/> seconds so proxies don't drop an idle stream and a dead
/// client is noticed.
/// </summary>
public static class EventEndpoints
{
    private const int HeartbeatSeconds = 20;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        // Not /api/v1/events: that is the audit-event list (ReadEndpoints).
        app.MapGet("/api/v1/live", async (
            HttpContext http, LodgeEventBus bus, IHostApplicationLifetime lifetime, CancellationToken aborted) =>
        {
            // A stream never ends on its own, so it must end when the server stops: Kestrel
            // waits for in-flight requests (30 s by default) before it lets the process exit.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, lifetime.ApplicationStopping);
            var ct = linked.Token;

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no"; // nginx: don't buffer the stream
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

            using var subscription = bus.Subscribe();
            await http.Response.WriteAsync(": connected\n\n", ct);
            await http.Response.Body.FlushAsync(ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(TimeSpan.FromSeconds(HeartbeatSeconds));
                    try
                    {
                        var e = await subscription.Reader.ReadAsync(wait.Token);
                        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(e, Json)}\n\n", ct);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await http.Response.WriteAsync(": keep-alive\n\n", ct);
                    }
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // The client went away.
            }
        }).RequireAuthorization();

        return app;
    }
}
