using Lodge.Core.Abstractions;
using Lodge.Core.Domain.Enums;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// Runs nothing: an <c>executor: none</c> action exists only to be confirmed. It is the
/// human gate of a chain ("approve", then everything after it AUTO), succeeds the moment it
/// starts and has no log. Stateless — every <c>none-</c> run id reads as succeeded, before
/// and after a restart — so there is nothing to persist or reattach.
/// </summary>
public sealed class NoneRunbookExecutor : IRunbookExecutor, IRunCompletionSource
{
    public const string RunIdPrefix = "none-";

    public event Action<string>? RunCompleted;

    public string AllocateRunId(ExecutorKind kind) => $"{RunIdPrefix}{Guid.NewGuid():N}";

    public Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var runId = request.RunId ?? AllocateRunId(ExecutorKind.None);

        // Off the caller's stack: the run is already committed as RUNNING by the time
        // StartAsync returns, and listeners land it from their own thread.
        _ = Task.Run(() =>
        {
            try
            {
                RunCompleted?.Invoke(runId);
            }
            catch
            {
                // A listener's failure must never surface as an unobserved task exception.
            }
        }, CancellationToken.None);

        return Task.FromResult(new RunbookRunHandle(runId, RunbookRunState.Running));
    }

    public Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
        => Task.FromResult(new RunbookRunStatus(runId, RunbookRunState.Succeeded,
            "Confirmed; nothing to run (executor: none).", DateTimeOffset.UtcNow));
}
