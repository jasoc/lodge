namespace Lodge.Cli;

// Mirrors Lodge.Server.Contracts.Dtos — kept as plain records here rather than shared
// across a project reference, since the CLI is deliberately just an HTTP client with no
// dependency on the server's own assemblies (it could be a separate binary entirely).

public sealed record KindDto(string Code, string Name, bool Enabled);

public sealed record InstanceDto(
    Guid Id, string KindCode, string InstanceCode, string DisplayName,
    int Generation, string Region, DateTimeOffset CreatedAt, DateTimeOffset? LastRevisionAt);

public sealed record InstanceDetailDto(InstanceDto Instance, string? LatestYaml);

public sealed record PendingPromptDto(string Name, string Prompt, bool Required);

public sealed record ActionDto(
    Guid Id, string CapabilityCode, string SignalPath, string? ItemKey, string? Requires,
    string Trigger, string Label, string Policy, string Status, bool Synthetic,
    IReadOnlyList<PendingPromptDto> PendingPrompts, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record ActionExecutionResultDto(Guid ActionId, string Status, string? ExecutionRef, string? Message, bool Denied);

public sealed record CycleSummaryDto(
    int KindsChecked, int InstancesReconciled, int DriftCount,
    IReadOnlyList<string> Messages, IReadOnlyList<string> ValidationErrors);

public sealed record LoginResponseDto(string Token, string SubjectId, DateTimeOffset? ExpiresAt);

public sealed record AuditEventDto(Guid Id, string EventType, string Actor, string? PayloadJson, DateTimeOffset CreatedAt);

public sealed record AuthConfigDto(string Mode);

public sealed record ServiceTokenDto(Guid Id, string Token, string SubjectId, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt);

public sealed record ApiTokenSummaryDto(
    Guid Id, string Kind, string DisplayName, string SubjectId,
    IReadOnlyList<string> Groups, IReadOnlyList<string> Scopes, bool IsAdmin,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt);
