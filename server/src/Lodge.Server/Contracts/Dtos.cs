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
    string RunbookRef,
    string Trigger,
    string Label,
    string Policy,
    string Status,
    bool Synthetic,
    IReadOnlyList<PendingPromptDto> PendingPrompts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

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
