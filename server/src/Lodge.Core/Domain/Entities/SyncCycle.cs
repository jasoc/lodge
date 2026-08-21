namespace Lodge.Core.Domain.Entities;

/// <summary>
/// A record of one reconciliation cycle (timer tick or manual "Run now"), so the Sync
/// page can show when Lodge last checked the inventory source and what it found.
/// Purely a log row — no relations to instances/kinds.
/// </summary>
public class SyncCycle
{
    public Guid Id { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>What started this cycle: "Timer" (background loop) or "Manual" (UI button/API call).</summary>
    public string TriggeredBy { get; set; } = string.Empty;

    public bool Success { get; set; }

    public int KindsChecked { get; set; }

    public int InstancesReconciled { get; set; }

    /// <summary>Unresolved drift (required-but-unconfirmed non-OPTIONAL actions) counted across all instances this cycle.</summary>
    public int DriftCount { get; set; }

    public string? Error { get; set; }

    /// <summary>JSON-encoded list of per-kind/instance messages from the cycle.</summary>
    public string MessagesJson { get; set; } = "[]";

    /// <summary>JSON-encoded list of instance-YAML / rule-catalog validation errors found this cycle.</summary>
    public string ValidationErrorsJson { get; set; } = "[]";
}
