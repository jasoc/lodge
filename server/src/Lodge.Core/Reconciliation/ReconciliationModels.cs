using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;

namespace Lodge.Core.Reconciliation;

/// <summary>
/// The reconciliation identity of an action: which action key confirms which signal (and,
/// for collection signals, which item). At most one live action row exists per identity,
/// and the most recent SUCCEEDED row per identity is the last confirmed state.
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

/// <summary>
/// Projection of one live (BLOCKED/QUEUED/RUNNING/FAILED) action row: the snapshot it was
/// emitted (and possibly approved) with. <see cref="ExecutorConfigJson"/> is in
/// <see cref="Catalog.ExecutorConfigJson"/>'s canonical encoding; <see cref="Requires"/> the
/// group, <see cref="Policy"/> the policy it was emitted under; the three inputs columns
/// are in <see cref="RequiredAction"/>'s persisted encodings (resolved from/const values,
/// secret references, pending prompts).
/// </summary>
public sealed record LiveActionRow(
    Guid Id,
    ActionIdentity Identity,
    SignalTrigger Trigger,
    ActionStatus Status,
    string? DesiredValueJson,
    string? ExecutorConfigJson = null,
    string? Requires = null,
    ActionPolicy Policy = ActionPolicy.MANUAL_REQUIRED,
    string? ResolvedInputsJson = null,
    string? SecretInputsJson = null,
    string? PendingPromptsJson = null);

/// <summary>
/// One action the current desired state calls for (or, when <see cref="Satisfied"/>,
/// has already confirmed). Policy/label/requires/inputs always come from the live catalog.
/// <see cref="LiveRowId"/>/<see cref="LiveStatus"/> are filled in by the live-row
/// matching pass when an existing row covers this identity. <see cref="AdoptOnFaith"/>
/// marks an identity that has never succeeded but is explicitly covered by a
/// <c>past_history</c> entry in instance YAML — it is adopted as SUCCEEDED/synthetic instead
/// of firing a real run against something that's already known to have happened outside
/// Lodge's governance. <see cref="DependsOn"/> are its <c>depends_on</c> targets resolved
/// to identities (the same item's, or its parent item's); <see cref="BlockedBy"/> the
/// subset not satisfied yet — non-empty means the action is emitted BLOCKED.
/// </summary>
public sealed record RequiredAction(
    ActionIdentity Identity,
    SignalTrigger Trigger,
    string CapabilityCode,
    string Label,
    string? Requires,
    ActionPolicy Policy,
    string? DesiredValueJson,
    IReadOnlyDictionary<string, string?> ResolvedInputs,
    IReadOnlyList<PendingPrompt> PendingPrompts,
    IReadOnlyList<SecretInputRef> SecretInputs,
    ExecutorKind ExecutorKind,
    ContainerExecutorConfig? ContainerConfig,
    HttpExecutorConfig? HttpConfig,
    bool Satisfied,
    bool AdoptOnFaith = false)
{
    public IReadOnlyList<ActionIdentity> DependsOn { get; init; } = Array.Empty<ActionIdentity>();

    public IReadOnlyList<ActionIdentity> BlockedBy { get; init; } = Array.Empty<ActionIdentity>();

    public bool Blocked => BlockedBy.Count > 0;

    /// <summary>The executor config snapshot in its canonical, persisted encoding.</summary>
    public string? ExecutorConfigJson => Catalog.ExecutorConfigJson.Serialize(ContainerConfig, HttpConfig);

    /// <summary>The resolved from/const inputs in their persisted encoding.</summary>
    public string ResolvedInputsJson => System.Text.Json.JsonSerializer.Serialize(ResolvedInputs);

    /// <summary>The unresolved secret references in their persisted encoding.</summary>
    public string SecretInputsJson => System.Text.Json.JsonSerializer.Serialize(SecretInputs);

    /// <summary>The prompts still to answer at confirm time in their persisted encoding.</summary>
    public string PendingPromptsJson => System.Text.Json.JsonSerializer.Serialize(PendingPrompts);

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
/// caller must apply (create QUEUED/BLOCKED rows, adopt synthetic SUCCEEDED rows,
/// supersede stale rows, block or unblock live rows as their dependencies change, start
/// AUTO rows — including ones unblocked this cycle).
/// </summary>
public sealed record ReconciliationResult(
    IReadOnlyList<CapabilityStatusView> Capabilities,
    IReadOnlyList<RequiredAction> ToCreate,
    IReadOnlyList<RequiredAction> ToAdopt,
    IReadOnlyList<Guid> ToSupersede,
    IReadOnlyList<RequiredAction> ToAutoStart,
    IReadOnlyList<string> ValidationErrors)
{
    /// <summary>Live QUEUED rows whose dependencies stopped being satisfied: back to BLOCKED.</summary>
    public IReadOnlyList<Guid> ToBlock { get; init; } = Array.Empty<Guid>();

    /// <summary>Live BLOCKED rows whose dependencies are now all satisfied: to QUEUED (AUTO ones are also in <see cref="ToAutoStart"/>).</summary>
    public IReadOnlyList<Guid> ToUnblock { get; init; } = Array.Empty<Guid>();
}
