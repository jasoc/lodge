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
/// A request to run one action. <see cref="ActionRef"/> (<c>capability/action_key</c>) only
/// names it in logs and status messages. <see cref="SecretNames"/> lists which
/// <see cref="Parameters"/> hold resolved secrets, so an executor that echoes what it sends
/// can mask them. <see cref="RunId"/> is the id from <see cref="IRunbookExecutor.AllocateRunId"/>
/// the caller already persisted; null lets the executor allocate one itself.
/// </summary>
public sealed record RunbookExecutionRequest(
    string KindCode,
    string InstanceCode,
    string ActionRef,
    Guid ActionId,
    IReadOnlyDictionary<string, string?> Parameters,
    ExecutorKind ExecutorKind,
    ContainerExecutorConfig? ContainerConfig = null,
    HttpExecutorConfig? HttpConfig = null,
    IReadOnlyCollection<string>? SecretNames = null,
    string? RunId = null);

/// <summary>Handle returned when a runbook run is started.</summary>
public sealed record RunbookRunHandle(string RunId, RunbookRunState State);

/// <summary>Point-in-time status of a runbook run.</summary>
public sealed record RunbookRunStatus(string RunId, RunbookRunState State, string? Message, DateTimeOffset UpdatedAt);

/// <summary>
/// The single, abstracted path to running an action. Lodge never executes operations
/// itself — it starts and supervises them (a container, an HTTP call).
/// </summary>
public interface IRunbookExecutor
{
    /// <summary>
    /// The id the next run of an action of <paramref name="kind"/> will have. Nothing is
    /// started: the caller persists the id first and only then calls <see cref="StartAsync"/>
    /// with it, so a crash in between can never lose track of a run that did start.
    /// </summary>
    string AllocateRunId(ExecutorKind kind);

    /// <summary>
    /// Start the runbook for an action and return a handle to poll. Idempotent per
    /// <see cref="RunbookExecutionRequest.RunId"/>: a run id this executor has already
    /// started is never launched twice.
    /// </summary>
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
/// local log implement it; a run without one simply has no log to show.
/// </summary>
public interface IRunbookLogReader
{
    /// <summary>
    /// Up to <paramref name="maxBytes"/> of the run's log from <paramref name="offset"/>,
    /// or null when this executor has no log for <paramref name="runId"/>.
    /// </summary>
    Task<RunbookLogChunk?> ReadLogAsync(string runId, long offset, int maxBytes, CancellationToken cancellationToken = default);
}
