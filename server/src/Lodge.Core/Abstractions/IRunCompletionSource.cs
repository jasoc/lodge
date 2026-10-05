namespace Lodge.Core.Abstractions;

/// <summary>
/// Optional executor capability: telling the world a run has finished, so the supervisor
/// lands its outcome now instead of at the next reconciliation cycle or UI read. The event
/// carries only the run id and fires after the run's status is final (a
/// <see cref="IRunbookExecutor.GetStatusAsync"/> issued from the handler sees it).
/// </summary>
public interface IRunCompletionSource
{
    event Action<string>? RunCompleted;
}
