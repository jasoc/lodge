namespace Lodge.Core.Domain.Entities;

/// <summary>Discriminates the two auth planes a token can belong to.</summary>
public enum ApiTokenKind
{
    /// <summary>A human's own token, minted via `lodge login` (auto-approved in the
    /// no-auth profile, OIDC-derived otherwise).</summary>
    Personal,

    /// <summary>A scoped token for automation (CI, cron) — not a user identity.</summary>
    Service
}

/// <summary>
/// A bearer token minted by Lodge itself. Only the hash is stored; the raw value is
/// shown to the caller once, at issuance. Authenticating a request is always "look up
/// this table by token hash" regardless of which auth plane issued the token.
/// </summary>
public class ApiToken
{
    public Guid Id { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public ApiTokenKind Kind { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The identity this token authenticates as.</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>Serialized group list, mirrors <c>AuthenticatedUser.Groups</c>.</summary>
    public string GroupsJson { get; set; } = "[]";

    public bool IsAdmin { get; set; }

    /// <summary>Serialized scope list; meaningful for <see cref="ApiTokenKind.Service"/> tokens only.</summary>
    public string ScopesJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
