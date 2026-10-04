namespace Lodge.Core.Domain.Enums;

/// <summary>
/// Lifecycle of one action row (one execution). A row is <em>live</em> while BLOCKED,
/// QUEUED, RUNNING, or FAILED — the reconciler keeps at most one live row per
/// (instance, signal, item key, action key) and supersedes rows whose desired-state
/// snapshot no longer matches what the current cycle requires.
/// </summary>
public enum ActionStatus
{
    /// <summary>Required (or available, for OPTIONAL) and waiting: AUTO rows start on the next cycle step, others wait for a human.</summary>
    QUEUED,

    /// <summary>
    /// Required, but waiting for its <c>depends_on</c> actions to succeed: neither started
    /// nor confirmable. The reconciler turns it QUEUED (and starts it, if AUTO) on the
    /// first cycle that finds every dependency satisfied — and back to BLOCKED if one stops
    /// being satisfied before it ran.
    /// </summary>
    BLOCKED,

    /// <summary>The runbook run was started and has not reported a terminal state yet.</summary>
    RUNNING,

    /// <summary>The runbook run completed successfully — this row is now part of the confirmed-state history.</summary>
    SUCCEEDED,

    /// <summary>The runbook run failed. The row stays live (parked drift): never auto-retried, re-runnable by a human.</summary>
    FAILED,

    /// <summary>No longer required by the current desired state (or its snapshot went stale) before ever succeeding.</summary>
    SUPERSEDED
}
