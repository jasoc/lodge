namespace Lodge.Core.Domain.Entities;

/// <summary>
/// A person Lodge knows about, keyed by the subject id their tokens carry. With OIDC the
/// identity provider owns users and their groups: each login upserts the user and
/// replaces their memberships with the token's group claim, 1:1 by name. Without auth
/// (local), users and memberships are edited in Lodge itself.
/// </summary>
public class User
{
    /// <summary>The subject id — the OIDC <c>sub</c>, or a chosen handle for a local user.</summary>
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Where the user (and their memberships) come from: <c>local</c> or <c>oidc</c>.</summary>
    public string Source { get; set; } = UserSources.Local;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public List<UserGroup> Groups { get; set; } = new();
}

/// <summary>One membership: a user is in a group. A group exists as long as someone is in it.</summary>
public class UserGroup
{
    public string UserId { get; set; } = string.Empty;

    public string GroupName { get; set; } = string.Empty;
}

public static class UserSources
{
    public const string Local = "local";
    public const string Oidc = "oidc";
}
