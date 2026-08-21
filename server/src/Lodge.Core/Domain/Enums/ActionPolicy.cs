namespace Lodge.Core.Domain.Enums;

/// <summary>
/// Governs whether an action's runbook may execute automatically when the reconciler
/// detects drift or requires an explicit human confirmation before it runs. Policy is
/// always read from the live rule catalog — the value stored on an action row is only
/// a historical record of what the row ran under.
/// </summary>
public enum ActionPolicy
{
    /// <summary>Lodge starts the runbook by itself as soon as the drift is detected (no human click).</summary>
    AUTO,

    /// <summary>The runbook counts as drift and waits for a human to confirm it in the UI before running.</summary>
    MANUAL_REQUIRED,

    /// <summary>
    /// Never auto-runs, never counts as drift, and never blocks a capability from being
    /// active: the action stays available and re-invocable on demand for as long as its
    /// rule matches the current state (e.g. a "rotate certificate" button).
    /// </summary>
    OPTIONAL
}
