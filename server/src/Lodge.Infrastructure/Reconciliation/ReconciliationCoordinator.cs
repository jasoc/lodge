using System.Text.Json;
using Lodge.Core.Abstractions;
using Lodge.Core.Domain.Entities;
using Lodge.Infrastructure.Git;
using Lodge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
/// The in-memory single-flight only covers one process; across replicas a Postgres
/// advisory lock (<see cref="AdvisoryLockKey"/>, held on the cycle's own connection) lets
/// exactly one cycle run at a time — a replica that doesn't get it skips the tick.
/// </summary>
public sealed class ReconciliationCoordinator
{
    /// <summary>Arbitrary constant ("LODGE" in ASCII) naming the cluster-wide reconciliation lock.</summary>
    public const long AdvisoryLockKey = 0x4C4F44474500L;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GitOptions _options;
    private readonly object _gate = new();

    // Orders everything that mutates action rows in this process: a full cycle, an
    // event-driven single-instance pass and the landing of a finished run. One at a time,
    // so none of them ever races another on the same rows.
    private readonly SemaphoreSlim _runLock = new(1, 1);
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

    /// <summary>
    /// Runs <paramref name="work"/> with exclusive access to the action rows of this process
    /// (see <see cref="_runLock"/>). For short jobs that must not interleave with a cycle.
    /// </summary>
    public async Task RunExclusiveAsync(Func<Task> work, CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            await work();
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>
    /// Reconciles one instance now — what an event (a run finished, so its dependents may
    /// unblock) asks for instead of waiting for the next timer tick. Same engine and same
    /// cluster-wide lock as a full cycle, but no inventory sync, no kind discovery and no
    /// other instance. False when another replica holds the lock: the caller retries.
    /// </summary>
    public async Task<bool> RunInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LodgeDbContext>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            try
            {
                if (!await TryLockAsync(connection))
                {
                    return false;
                }

                try
                {
                    await scope.ServiceProvider.GetRequiredService<ReconciliationRunner>()
                        .RunInstanceCycleAsync(instanceId, cancellationToken);
                    return true;
                }
                finally
                {
                    await UnlockAsync(connection);
                }
            }
            finally
            {
                await connection.CloseAsync();
            }
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<CycleSummary> RunAsync(SyncTrigger trigger)
    {
        await _runLock.WaitAsync();
        try
        {
            return await RunCoreAsync(trigger);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<CycleSummary> RunCoreAsync(SyncTrigger trigger)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LodgeDbContext>();

        // Session-level lock on this scope's own connection, which the runner shares: it
        // lives exactly as long as the cycle and is released if the process dies.
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            if (!await TryLockAsync(connection))
            {
                lock (_gate)
                {
                    _inFlight = null;
                }
                return new CycleSummary(0, 0, 0,
                    new[] { "another replica is running a reconciliation cycle; skipped" }, Array.Empty<string>());
            }

            try
            {
                return await RunLockedAsync(scope, db, trigger);
            }
            finally
            {
                await UnlockAsync(connection);
            }
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task<bool> TryLockAsync(System.Data.Common.DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_try_advisory_lock({AdvisoryLockKey})";
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task UnlockAsync(System.Data.Common.DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_advisory_unlock({AdvisoryLockKey})";
        await command.ExecuteScalarAsync();
    }

    private async Task<CycleSummary> RunLockedAsync(IServiceScope scope, LodgeDbContext db, SyncTrigger trigger)
    {
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
