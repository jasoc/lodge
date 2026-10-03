using Lodge.Core.Domain.Enums;

namespace Lodge.Core.Domain.Entities;

/// <summary>
/// One runbook invocation, produced by the reconciler. An action's identity is
/// (instance, signal path, item key, action key): the reconciler keeps at most one live
/// (QUEUED/RUNNING/FAILED) row per identity, and the most recent SUCCEEDED row per
/// identity is the record of the last state confirmed to have actually happened. The
/// runbook is an execution detail, not part of identity — the same runbook may back more
/// than one conceptually distinct action key. Lodge decides and supervises; Octopus executes.
/// </summary>
public class Action
{
    public Guid Id { get; set; }

    public Guid InstanceId { get; set; }

    /// <summary>Code of the capability that owns the rule which produced this action.</summary>
    public string CapabilityCode { get; set; } = string.Empty;

    /// <summary>Dotted path of the signal this action reconciles, e.g. <c>features.sso_login</c> or <c>virtual_machines</c>.</summary>
    public string SignalPath { get; set; } = string.Empty;

    /// <summary>Stable key of the collection item this action targets (e.g. <c>vm-alpha-01</c>); null for scalar signals.</summary>
    public string? ItemKey { get; set; }

    /// <summary>Explicit, catalog-declared action key — part of this action's identity.</summary>
    public string ActionKey { get; set; } = string.Empty;

    /// <summary>Direct Octopus runbook reference, e.g. <c>acme-instance-ops/configure-sso</c> — an execution detail, not part of identity.</summary>
    public string RunbookRef { get; set; } = string.Empty;

    /// <summary>What reconciliation event this action responds to.</summary>
    public SignalTrigger Trigger { get; set; }

    /// <summary>Human-facing label shown on the capability card.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Historical record of the policy this row was created/ran under. The live catalog decides current behavior.</summary>
    public ActionPolicy Policy { get; set; }

    public ActionStatus Status { get; set; }

    /// <summary>
    /// Canonical JSON snapshot of the desired state this action targets: the scalar value
    /// for STATE, the item body for ADD/MODIFY, <c>null</c> (JSON) for DELETE. On a
    /// SUCCEEDED row this is what the runbook confirmed; satisfaction is decided by
    /// comparing it to the current desired value.
    /// </summary>
    public string? DesiredValueJson { get; set; }

    /// <summary>JSON-encoded map of runbook inputs resolved by the reconciler (from/const).</summary>
    public string? ResolvedInputsJson { get; set; }

    /// <summary>JSON-encoded list of prompts still to be filled by a human at confirm time.</summary>
    public string? PendingPromptsJson { get; set; }

    /// <summary>Historical record of which executor this row ran under. The live catalog decides current behavior.</summary>
    public ExecutorKind ExecutorKind { get; set; } = ExecutorKind.Shell;

    /// <summary>JSON-encoded executor-specific config (currently only <c>DockerExecutorConfig</c>, when <see cref="ExecutorKind"/> is Docker); null otherwise.</summary>
    public string? ExecutorConfigJson { get; set; }

    /// <summary>JSON-encoded list of secret references (name -&gt; ref) still to be resolved to plaintext at execution time — never resolved here, never persisted resolved.</summary>
    public string? SecretInputsJson { get; set; }

    /// <summary>Opaque handle of the runbook run started for this action, if any.</summary>
    public string? ExecutionRef { get; set; }

    /// <summary>True when this SUCCEEDED row was synthesized by bootstrap-on-faith adoption rather than an observed run.</summary>
    public bool Synthetic { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Set when the action reaches a terminal state (SUCCEEDED/FAILED/SUPERSEDED).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Set only by the UI invalidation action (never by YAML): when non-null, this
    /// SUCCEEDED row no longer counts as confirmed state — the identity reverts to
    /// "never succeeded" on the next reconciliation cycle.
    /// </summary>
    public DateTimeOffset? InvalidatedAt { get; set; }

    /// <summary>Who invalidated this row, set together with <see cref="InvalidatedAt"/>.</summary>
    public string? InvalidatedBy { get; set; }

    public Instance? Instance { get; set; }
}
