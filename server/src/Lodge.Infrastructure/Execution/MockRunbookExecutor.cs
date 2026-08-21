using System.Collections.Concurrent;
using Lodge.Core.Abstractions;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// POC-only runbook executor. Simulates an Octopus runbook run: a started run reports
/// <see cref="RunbookRunState.Running"/> for a short delay, then transitions to
/// <see cref="RunbookRunState.Succeeded"/>. This lets the confirm → running → succeeded
/// loop (and the UI's realtime status polling) be demonstrated without Octopus. It is
/// explicitly temporary: an <c>OctopusRunbookExecutor</c> replaces it with no domain
/// changes. No real operations are ever executed.
/// </summary>
public sealed class MockRunbookExecutor : IRunbookExecutor
{
    private static readonly TimeSpan SimulatedDuration = TimeSpan.FromSeconds(6);

    private readonly ConcurrentDictionary<string, RunRecord> _runs = new(StringComparer.Ordinal);

    public Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var runId = $"mock-run-{Guid.NewGuid():N}";
        _runs[runId] = new RunRecord(request.RunbookRef, DateTimeOffset.UtcNow);
        return Task.FromResult(new RunbookRunHandle(runId, RunbookRunState.Running));
    }

    public Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var record))
        {
            return Task.FromResult(new RunbookRunStatus(runId, RunbookRunState.Failed, "Unknown run.", DateTimeOffset.UtcNow));
        }

        var elapsed = DateTimeOffset.UtcNow - record.StartedAt;
        var state = elapsed >= SimulatedDuration ? RunbookRunState.Succeeded : RunbookRunState.Running;
        var message = state == RunbookRunState.Succeeded
            ? $"Runbook '{record.RunbookRef}' completed (simulated)."
            : $"Runbook '{record.RunbookRef}' in progress (simulated).";

        return Task.FromResult(new RunbookRunStatus(runId, state, message, DateTimeOffset.UtcNow));
    }

    private sealed record RunRecord(string RunbookRef, DateTimeOffset StartedAt);
}
