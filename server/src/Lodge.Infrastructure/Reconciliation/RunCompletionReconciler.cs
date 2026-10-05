using System.Threading.Channels;
using Lodge.Core.Abstractions;
using Lodge.Core.Domain.Enums;
using Lodge.Infrastructure.Execution;
using Lodge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>
/// The event-driven half of reconciliation: when a run finishes, its outcome is landed
/// immediately and that one instance is reconciled right away, so the actions it unblocks
/// (and the AUTO ones among them) start now instead of at the next timer tick. The timer
/// loop stays as the safety net for anything this misses (a restart, another replica's run).
///
/// One consumer, so landing and reconciling never interleave with each other; the
/// coordinator's lock orders them against full cycles. Cost is bounded three ways: a short
/// debounce folds a burst of completions into one pass, each instance is reconciled once
/// per burst however many of its runs ended, and an instance is never reconciled more than
/// once per <see cref="MinGap"/>. A chain (A unblocks B unblocks C) ends by itself: only a
/// run that actually reached a final state asks for a pass.
/// </summary>
public sealed class RunCompletionReconciler : BackgroundService
{
    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    internal static readonly TimeSpan MinGap = TimeSpan.FromSeconds(1);
    private const int MaxLockAttempts = 5;

    private readonly Channel<string> _completed = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ReconciliationCoordinator _coordinator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RunCompletionReconciler> _logger;
    private readonly Dictionary<Guid, DateTimeOffset> _lastPass = new();

    public RunCompletionReconciler(
        IRunbookExecutor executor,
        ReconciliationCoordinator coordinator,
        IServiceScopeFactory scopeFactory,
        ILogger<RunCompletionReconciler> logger)
    {
        _coordinator = coordinator;
        _scopeFactory = scopeFactory;
        _logger = logger;
        if (executor is IRunCompletionSource source)
        {
            source.RunCompleted += runId => _completed.Writer.TryWrite(runId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _completed.Reader.WaitToReadAsync(stoppingToken))
            {
                // Let the rest of a burst arrive, then handle it as one batch.
                await Task.Delay(Debounce, stoppingToken);
                var runIds = new HashSet<string>(StringComparer.Ordinal);
                while (_completed.Reader.TryRead(out var runId))
                {
                    runIds.Add(runId);
                }

                var instances = new HashSet<Guid>();
                foreach (var runId in runIds)
                {
                    try
                    {
                        if (await LandAsync(runId, stoppingToken) is { } instanceId)
                        {
                            instances.Add(instanceId);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Could not land the outcome of run {RunId}; the next cycle will", runId);
                    }
                }

                foreach (var instanceId in instances)
                {
                    try
                    {
                        await ReconcileAsync(instanceId, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Event-driven reconciliation of instance {Instance} failed; the next cycle retries", instanceId);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Moves the RUNNING action of <paramref name="runId"/> to its final state. Returns its
    /// instance when that happened, null when there was nothing to land (already landed by a
    /// cycle or a UI read, or a run no action points at).
    /// </summary>
    private async Task<Guid?> LandAsync(string runId, CancellationToken ct)
    {
        Guid? landed = null;
        await _coordinator.RunExclusiveAsync(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LodgeDbContext>();
            var action = await db.Actions.FirstOrDefaultAsync(
                a => a.ExecutionRef == runId && a.Status == ActionStatus.RUNNING, ct);
            if (action is null)
            {
                return;
            }

            var kindCode = await db.Instances
                .Where(i => i.Id == action.InstanceId)
                .Select(i => i.KindCode)
                .FirstAsync(ct);
            await scope.ServiceProvider.GetRequiredService<ActionExecutionService>()
                .ApplyExecutorStatusAsync(action, kindCode, ct);
            await db.SaveChangesAsync(ct);

            if (action.Status != ActionStatus.RUNNING)
            {
                landed = action.InstanceId;
            }
        }, ct);
        return landed;
    }

    private async Task ReconcileAsync(Guid instanceId, CancellationToken ct)
    {
        if (_lastPass.TryGetValue(instanceId, out var last) && DateTimeOffset.UtcNow - last < MinGap)
        {
            await Task.Delay(MinGap - (DateTimeOffset.UtcNow - last), ct);
        }

        for (var attempt = 1; attempt <= MaxLockAttempts; attempt++)
        {
            if (await _coordinator.RunInstanceAsync(instanceId, ct))
            {
                _lastPass[instanceId] = DateTimeOffset.UtcNow;
                return;
            }

            // Another replica holds the cluster lock: it is mid-cycle, try again shortly.
            await Task.Delay(MinGap, ct);
        }

        _logger.LogWarning("Instance {Instance} not reconciled after a run finished: the cycle lock stayed busy; the next tick will", instanceId);
    }
}
