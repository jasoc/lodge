using Lodge.Infrastructure.Reconciliation;
using Lodge.Server.Auth;

namespace Lodge.Server.Endpoints;

/// <summary>
/// Reconciliation as an explicitly invocable operation — the `terraform apply`-shaped
/// entry point. The UI's "Run now" button, the CLI's `lodge reconcile`, and a CI job all
/// call this same endpoint; the coordinator coalesces concurrent callers into one
/// in-flight cycle. The background loop (if enabled) is just one more caller of it.
/// </summary>
public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/reconcile", async (
            ReconciliationCoordinator coordinator, CancellationToken ct) =>
        {
            var summary = await coordinator.TriggerAsync(SyncTrigger.Api, ct);
            return Results.Ok(summary);
        }).RequireAuthorization().RequireScope("reconcile");

        return app;
    }
}
