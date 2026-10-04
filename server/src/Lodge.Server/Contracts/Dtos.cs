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
    string RunbookRef,
    string Trigger,
    string Label,
    string Policy,
    string Status,
    bool Synthetic,
    IReadOnlyList<PendingPromptDto> PendingPrompts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ExecutionRef = null,
    DateTimeOffset? InvalidatedAt = null);

/// <summary>
/// A slice of the action's latest run log (see <c>GET …/actions/{id}/log?offset=</c>).
/// <see cref="Available"/> is false when the action never ran or its executor keeps no
/// local log (webhooks). <see cref="Running"/> and <see cref="Message"/> are the executor's
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
    string RunbookRef,
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
    IReadOnlyList<SignalDefinitionDto> Signals);

public sealed record SignalDefinitionDto(
    string Path,
    string Kind,
    IReadOnlyList<SignalRuleDto> Rules);

public sealed record SignalRuleDto(
    string Trigger,
    string? WhenJson,
    string? ItemKey,
    IReadOnlyList<ActionTemplateDto> Actions);

public sealed record ActionTemplateDto(
    string Key,
    string Runbook,
    string Label,
    string Policy,
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
