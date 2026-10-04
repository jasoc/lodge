using System.Text.Json;
using Lodge.Infrastructure.Persistence;
using Lodge.Infrastructure.Reconciliation;
using Lodge.Server.Auth;
using Lodge.Server.Contracts;
using Microsoft.EntityFrameworkCore;

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

        // The latest recorded cycles — when Lodge last looked, what it did, and every
        // inventory/catalog validation error it hit — for the Reconciliation page.
        app.MapGet("/api/v1/reconcile/cycles", async (
            int? limit, LodgeDbContext db, SettingsService settings, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 20, 1, 200);
            var rows = await db.SyncCycles
                .OrderByDescending(c => c.StartedAt)
                .Take(take)
                .ToListAsync(ct);
            var loop = await settings.GetReconciliationSettingsAsync(ct);

            return Results.Ok(new SyncCyclesDto(
                loop.Enabled,
                loop.IntervalSeconds,
                rows.Select(c => new SyncCycleDto(
                    c.Id, c.StartedAt, c.CompletedAt, c.TriggeredBy, c.Success,
                    c.KindsChecked, c.InstancesReconciled, c.DriftCount, c.Error,
                    ReadList(c.MessagesJson), ReadList(c.ValidationErrorsJson))).ToList()));
        }).RequireAuthorization();

        return app;
    }

    private static IReadOnlyList<string> ReadList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }
}
