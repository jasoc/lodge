using System.Text.Json;
using Lodge.Core.Abstractions;
using Lodge.Core.Domain.Entities;
using Lodge.Infrastructure.Git;
using Lodge.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>What started a reconciliation cycle — shown on the Sync page and used for throttling.</summary>
public enum SyncTrigger
{
    /// <summary>A human clicked "Run now" in the UI.</summary>
    Manual,

    /// <summary>The background loop's own timer tick.</summary>
    Timer,

    /// <summary>An explicit call to the reconcile API/CLI (e.g. from a script or CI job).</summary>
    Api
}

/// <summary>
/// Single-flight wrapper around <see cref="ReconciliationRunner"/>. Concurrent callers
/// (operators mashing "Run now", or the timer firing during a manual run) coalesce onto
/// one in-flight cycle. Manual callers within
/// <see cref="GitOptions.MinManualSyncIntervalSeconds"/> of the last completed cycle get
/// that cycle's result instead of starting another; the timer is never throttled.
/// Every real cycle invalidates the file-backed catalog/permission caches (so edits to
/// <c>mapping/</c> apply live) and records a <see cref="SyncCycle"/> row for the Sync page.
/// </summary>
public sealed class ReconciliationCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GitOptions _options;
    private readonly object _gate = new();
    private Task<CycleSummary>? _inFlight;
    private CycleSummary? _lastSummary;
    private DateTimeOffset? _lastCompletedAt;

    public ReconciliationCoordinator(IServiceScopeFactory scopeFactory, IOptions<GitOptions> options)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
    }

    /// <summary>Runs a reconciliation cycle, coalescing with any already in progress.</summary>
    public Task<CycleSummary> TriggerAsync(SyncTrigger trigger = SyncTrigger.Manual, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false })
            {
                return _inFlight;
            }

            if (trigger != SyncTrigger.Timer && _lastSummary is not null && _lastCompletedAt is not null)
            {
                var minGap = TimeSpan.FromSeconds(Math.Max(0, _options.MinManualSyncIntervalSeconds));
                if (DateTimeOffset.UtcNow - _lastCompletedAt.Value < minGap)
                {
                    return Task.FromResult(_lastSummary);
                }
            }

            // Not cancellable per-caller: a shared cycle must not be aborted by one
            // caller's token. The cycle uses CancellationToken.None and completes for all.
            return _inFlight = RunAsync(trigger);
        }
    }

    private async Task<CycleSummary> RunAsync(SyncTrigger trigger)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LodgeDbContext>();

        var cycle = new SyncCycle
        {
            Id = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            TriggeredBy = trigger.ToString()
        };
        db.SyncCycles.Add(cycle);
        await db.SaveChangesAsync();

        try
        {
            // Mapping files (capabilities, instance overrides) are reloaded every cycle,
            // not just at startup — policy is never frozen.
            if (scope.ServiceProvider.GetRequiredService<ICapabilityCatalogProvider>() is ICacheInvalidatable catalogs)
            {
                catalogs.Invalidate();
            }

            var runner = scope.ServiceProvider.GetRequiredService<ReconciliationRunner>();
            var summary = await runner.RunCycleAsync(CancellationToken.None);

            cycle.CompletedAt = DateTimeOffset.UtcNow;
            cycle.Success = true;
            cycle.KindsChecked = summary.KindsChecked;
            cycle.InstancesReconciled = summary.InstancesReconciled;
            cycle.DriftCount = summary.DriftCount;
            cycle.MessagesJson = JsonSerializer.Serialize(summary.Messages);
            cycle.ValidationErrorsJson = JsonSerializer.Serialize(summary.ValidationErrors);
            await db.SaveChangesAsync();

            lock (_gate)
            {
                _lastSummary = summary;
                _lastCompletedAt = cycle.CompletedAt;
            }

            return summary;
        }
        catch (Exception ex)
        {
            cycle.CompletedAt = DateTimeOffset.UtcNow;
            cycle.Success = false;
            cycle.Error = ex.Message;
            await db.SaveChangesAsync();
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = null;
            }
        }
    }
}
