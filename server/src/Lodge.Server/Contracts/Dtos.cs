namespace Lodge.Server.Contracts;

public sealed record KindDto(string Code, string Name, bool Enabled);

public sealed record InstanceDto(
    Guid Id,
    string KindCode,
    string InstanceCode,
    string DisplayName,
    int Generation,
    string Region,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRevisionAt);

public sealed record InstanceDetailDto(
    InstanceDto Instance,
    string? LatestYaml);

public sealed record ActionDto(
    Guid Id,
    string CapabilityCode,
    string SignalPath,
    string? ItemKey,
    string ActionKey,
    string? Requires,
    string Trigger,
    string Label,
    string Policy,
    string Status,
    bool Synthetic,
    IReadOnlyList<PendingPromptDto> PendingPrompts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ExecutionRef = null,
    DateTimeOffset? InvalidatedAt = null,
    IReadOnlyList<ActionIdentityDto>? DependsOn = null);

/// <summary>One action's identity — an edge target of the action graph (see <see cref="ActionDto.DependsOn"/>).</summary>
public sealed record ActionIdentityDto(string SignalPath, string? ItemKey, string ActionKey);

/// <summary>
/// A slice of the action's latest run log (see <c>GET …/actions/{id}/log?offset=</c>).
/// <see cref="Available"/> is false when the action never ran or its executor kept no log. <see cref="Running"/> and <see cref="Message"/> are the executor's
/// live view of the run — fresher than the action row, which the loop updates per cycle.
/// </summary>
public sealed record ActionLogDto(
    Guid ActionId,
    string? RunId,
    string ActionStatus,
    bool Available,
    bool Running,
    string? Message,
    string Text,
    long NextOffset);

public sealed record PendingPromptDto(
    string Name,
    string Prompt,
    bool Required);

public sealed record AuditEventDto(
    Guid Id,
    string EventType,
    string Actor,
    string? PayloadJson,
    DateTimeOffset CreatedAt);

/// <summary>An <see cref="ActionDto"/> plus the instance it belongs to — the row shape for
/// the cross-instance Drift/Actions pages, which have no single kind/instance in scope.</summary>
public sealed record GlobalActionDto(
    Guid Id,
    string CapabilityCode,
    string SignalPath,
    string? ItemKey,
    string ActionKey,
    string? Requires,
    string Trigger,
    string Label,
    string Policy,
    string Status,
    bool Synthetic,
    IReadOnlyList<PendingPromptDto> PendingPrompts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string KindCode,
    Guid InstanceId,
    string InstanceCode,
    string InstanceDisplayName);

/// <summary>An <see cref="AuditEventDto"/> plus the instance it belongs to (when any) — the
/// row shape for the cross-instance Audit Log page.</summary>
public sealed record GlobalAuditEventDto(
    Guid Id,
    string KindCode,
    string EventType,
    string Actor,
    string? PayloadJson,
    DateTimeOffset CreatedAt,
    Guid? InstanceId,
    string? InstanceCode,
    string? InstanceDisplayName);

/// <summary>Read-only view of a kind's parsed capability catalog — no instance context, no
/// DB query, just what the YAML declares. Backs the Kinds/Capabilities browser.</summary>
public sealed record CapabilityCatalogDto(
    string KindCode,
    IReadOnlyList<CapabilityDefinitionDto> Capabilities);

public sealed record CapabilityDefinitionDto(
    string Code,
    string Title,
    string Description,
    bool IsView,
    IReadOnlyList<SignalDefinitionDto> Signals);

public sealed record SignalDefinitionDto(
    string Path,
    string Kind,
    string? Label,
    IReadOnlyList<SignalRuleDto> Rules);

public sealed record SignalRuleDto(
    string Trigger,
    string? WhenJson,
    string? ItemKey,
    IReadOnlyList<ActionTemplateDto> Actions);

public sealed record ActionTemplateDto(
    string Key,
    string Label,
    string Policy,
    string? Requires,
    string Executor,
    IReadOnlyList<RuleInputDto> Inputs,
    IReadOnlyList<string> DependsOn);

public sealed record RuleInputDto(string Name, string Kind, string? Value, bool Required);

/// <summary>Response to a successful `POST /api/v1/auth/login` — the raw token is shown once.</summary>
public sealed record LoginResponseDto(string Token, string SubjectId, DateTimeOffset? ExpiresAt);

/// <summary>Tells the CLI/SPA which login flow to use before either has a token.</summary>
public sealed record AuthConfigDto(string Mode);

/// <summary>Body for `POST /api/v1/tokens` — always mints a Service-kind token; personal
/// tokens are only ever minted by a login flow, never by this admin endpoint.</summary>
public sealed record CreateServiceTokenRequest(
    string DisplayName,
    IReadOnlyList<string>? Scopes,
    int? TtlMinutes);

/// <summary>Response to a successful service-token creation — the raw token is shown once.</summary>
public sealed record ServiceTokenDto(
    Guid Id,
    string Token,
    string SubjectId,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt);

/// <summary>A token's metadata, listed for admins — never the raw value, which only ever
/// appears once, at issuance.</summary>
public sealed record ApiTokenSummaryDto(
    Guid Id,
    string Kind,
    string DisplayName,
    string SubjectId,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Scopes,
    bool IsAdmin,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt);

/// <summary>The caller, as Lodge sees them: who they are, their groups, and whether
/// they're an admin (the no-auth local admin, or a member of the OIDC AdminGroup) — admins
/// may run every action whatever it `requires`.</summary>
public sealed record MeDto(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Groups,
    bool IsAdmin,
    bool IsServiceToken);

public sealed record UserDto(
    string Id,
    string DisplayName,
    string Source,
    IReadOnlyList<string> Groups,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>An action template that names a group in `requires`.</summary>
public sealed record GroupRequirementDto(
    string KindCode,
    string CapabilityCode,
    string ActionKey,
    string Label);

/// <summary>A group: its members, and which catalog actions require it. A group required
/// by an action but with no members yet is listed too — only admins can run those actions.</summary>
public sealed record GroupDto(
    string Name,
    IReadOnlyList<string> Members,
    IReadOnlyList<GroupRequirementDto> RequiredBy);

/// <summary>One reconciliation cycle as recorded in `sync_cycles`, for the Reconciliation page.</summary>
public sealed record SyncCycleDto(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string TriggeredBy,
    bool Success,
    int KindsChecked,
    int InstancesReconciled,
    int DriftCount,
    string? Error,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> ValidationErrors);

/// <summary>The latest cycles, newest first, plus the loop's own schedule.</summary>
public sealed record SyncCyclesDto(
    bool LoopEnabled,
    int IntervalSeconds,
    IReadOnlyList<SyncCycleDto> Cycles);
