namespace Lodge.Core.Abstractions;

/// <summary>
/// Optional executor capability: stopping a run that is in flight, on an operator's request.
/// The run then ends like any other, failed, so its outcome lands through the usual path
/// (<see cref="IRunCompletionSource"/>, or the next status read).
/// </summary>
public interface IRunbookCanceller
{
    /// <summary>
    /// Asks the run to stop. True when a live run was told to; false when there was nothing
    /// to stop (already finished, unknown id, or an executor with nothing running).
    /// </summary>
    Task<bool> StopAsync(string runId, CancellationToken cancellationToken = default);
}
