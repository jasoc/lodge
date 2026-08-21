using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;

namespace Lodge.Core.Reconciliation;

/// <summary>
/// The reconciliation identity of an action: which action key confirms which signal (and,
/// for collection signals, which item). At most one live action row exists per identity,
/// and the most recent SUCCEEDED row per identity is the last confirmed state. The
/// underlying runbook is an execution detail, not part of identity — the same runbook may
/// back more than one conceptually distinct action key.
/// </summary>
public sealed record ActionIdentity(string SignalPath, string? ItemKey, string ActionKey);

/// <summary>
/// Projection of one SUCCEEDED action row — the unit of confirmed-state history.
/// <see cref="Id"/> is the underlying row id, needed so the UI invalidation action can
/// target this specific historical record.
/// </summary>
public sealed record SucceededRecord(
    Guid Id,
    ActionIdentity Identity,
    SignalTrigger Trigger,
    string? DesiredValueJson,
    DateTimeOffset CompletedAt,
    bool Synthetic);

/// <summary>Projection of one live (QUEUED/RUNNING/FAILED) action row.</summary>
public sealed record LiveActionRow(
    Guid Id,
    ActionIdentity Identity,
    SignalTrigger Trigger,
    ActionStatus Status,
    string? DesiredValueJson);

/// <summary>
/// One action the current desired state calls for (or, when <see cref="Satisfied"/>,
/// has already confirmed). Policy/label/runbook/inputs always come from the live catalog.
/// <see cref="LiveRowId"/>/<see cref="LiveStatus"/> are filled in by the live-row
/// matching pass when an existing row covers this identity. <see cref="AdoptOnFaith"/>
/// marks an identity that has never succeeded but is explicitly covered by a
/// <c>past_history</c> entry in instance YAML — it is adopted as SUCCEEDED/synthetic instead
/// of firing a real run against something that's already known to have happened outside
/// Lodge's governance.
/// </summary>
public sealed record RequiredAction(
    ActionIdentity Identity,
    SignalTrigger Trigger,
    string CapabilityCode,
    string Label,
    string Runbook,
    ActionPolicy Policy,
    string? DesiredValueJson,
    IReadOnlyDictionary<string, string?> ResolvedInputs,
    IReadOnlyList<PendingPrompt> PendingPrompts,
    bool Satisfied,
    bool AdoptOnFaith = false)
{
    public Guid? LiveRowId { get; set; }

    public ActionStatus? LiveStatus { get; set; }

    /// <summary>
    /// Id of the SUCCEEDED row that satisfies this identity, when <see cref="Satisfied"/>
    /// is true — the target of the UI invalidation action.
    /// </summary>
    public Guid? SucceededActionId { get; set; }

    /// <summary>Drift = something must still happen for the current desired state to be confirmed.</summary>
    public bool IsDrift => Policy != ActionPolicy.OPTIONAL && !Satisfied;
}

/// <summary>Per-signal view for the UI: presence, current value, and the actions that apply right now.</summary>
public sealed record SignalStatusView(
    string Path,
    SignalKind Kind,
    bool Present,
    string? CurrentValueJson,
    IReadOnlyList<RequiredAction> Actions,
    string? ValidationError);

public enum CapabilityState
{
    /// <summary>At least one of the capability's signal paths does not exist in the instance YAML.</summary>
    NotVisible,

    /// <summary>Every non-OPTIONAL STATE/ADD/DELETE action of the matching rules is confirmed.</summary>
    Active,

    /// <summary>Unconfirmed drift is queued or running.</summary>
    Pending,

    /// <summary>All remaining drift is parked on FAILED runs and needs human attention.</summary>
    Drifted,

    /// <summary>Signals are all off/empty and that state is confirmed.</summary>
    Disabled
}

public sealed record CapabilityStatusView(
    string Code,
    string Title,
    string Description,
    string? SourceFile,
    CapabilityState State,
    bool HasModifyDrift,
    IReadOnlyList<SignalStatusView> Signals);

/// <summary>Everything the pure reconciler needs for one instance. All I/O happens before this is built.</summary>
public sealed record ReconciliationInput(
    string KindCode,
    string InstanceCode,
    object? DesiredRoot,
    CapabilityCatalog Catalog,
    IReadOnlyList<SucceededRecord> History,
    IReadOnlyList<LiveActionRow> LiveRows);

/// <summary>
/// The reconciler's verdict for one instance: the UI view plus the row mutations the
/// caller must apply (create QUEUED rows, adopt synthetic SUCCEEDED rows, supersede
/// stale rows, start AUTO rows).
/// </summary>
public sealed record ReconciliationResult(
    IReadOnlyList<CapabilityStatusView> Capabilities,
    IReadOnlyList<RequiredAction> ToCreate,
    IReadOnlyList<RequiredAction> ToAdopt,
    IReadOnlyList<Guid> ToSupersede,
    IReadOnlyList<RequiredAction> ToAutoStart,
    IReadOnlyList<string> ValidationErrors);
