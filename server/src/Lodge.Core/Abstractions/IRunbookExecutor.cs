using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;

namespace Lodge.Core.Abstractions;

/// <summary>
/// Lifecycle state of a runbook run as reported by the executor.
/// </summary>
public enum RunbookRunState
{
    Queued,
    Running,
    Succeeded,
    Failed
}

/// <summary>
/// A request to execute the runbook bound to an operational action. Lodge decides and
/// supervises; the executor (Octopus) actually runs the operation.
/// </summary>
public sealed record RunbookExecutionRequest(
    string KindCode,
    string InstanceCode,
    string RunbookRef,
    Guid ActionId,
    IReadOnlyDictionary<string, string?> Parameters,
    ExecutorKind ExecutorKind = ExecutorKind.Shell,
    DockerExecutorConfig? DockerConfig = null);

/// <summary>Handle returned when a runbook run is started.</summary>
public sealed record RunbookRunHandle(string RunId, RunbookRunState State);

/// <summary>Point-in-time status of a runbook run.</summary>
public sealed record RunbookRunStatus(string RunId, RunbookRunState State, string? Message, DateTimeOffset UpdatedAt);

/// <summary>
/// The single, abstracted path to runbook execution. Octopus-ready: the POC ships a
/// mock implementation that simulates a run; an <c>OctopusRunbookExecutor</c> is a
/// future drop-in with no domain logic changes. Lodge never executes operations
/// itself — it starts and supervises them.
/// </summary>
public interface IRunbookExecutor
{
    /// <summary>Start the runbook for an action and return a handle to poll.</summary>
    Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Return the current status of a previously started run.</summary>
    Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One slice of a run's combined stdout/stderr, starting at a byte offset. Callers tail a
/// live run by asking again from <see cref="NextOffset"/>; <see cref="NextOffset"/> never
/// splits a UTF-8 character, so concatenated slices always decode cleanly.
/// </summary>
public sealed record RunbookLogChunk(string Text, long NextOffset);

/// <summary>
/// Optional executor capability: reading back what a run printed. Executors that keep a
/// local log (shell, docker) implement it; ones whose output lives elsewhere (a webhook
/// target) don't, and their runs simply have no log to show.
/// </summary>
public interface IRunbookLogReader
{
    /// <summary>
    /// Up to <paramref name="maxBytes"/> of the run's log from <paramref name="offset"/>,
    /// or null when this executor has no log for <paramref name="runId"/>.
    /// </summary>
    Task<RunbookLogChunk?> ReadLogAsync(string runId, long offset, int maxBytes, CancellationToken cancellationToken = default);
}
