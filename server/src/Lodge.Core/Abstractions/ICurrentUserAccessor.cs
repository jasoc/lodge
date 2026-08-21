namespace Lodge.Core.Abstractions;

/// <summary>
/// The identity Lodge acts on behalf of, resolved from whichever bearer token
/// authenticated the request. <c>Scopes</c>/<c>IsServiceToken</c> are only meaningful for
/// service tokens (automation) — a personal token (a human) carries no scopes and is
/// instead gated by <c>IsAdmin</c> plus group-based RBAC via <see cref="IPermissionResolver"/>.
/// </summary>
public sealed record AuthenticatedUser(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Groups,
    bool IsAdmin,
    IReadOnlyList<string>? Scopes = null,
    bool IsServiceToken = false)
{
    public IReadOnlyList<string> Scopes { get; init; } = Scopes ?? Array.Empty<string>();
}

/// <summary>
/// Resolves the current user for a request. The POC ships a mock returning a local
/// admin; an <c>EntraCurrentUserAccessor</c> is a future drop-in that reads the
/// authenticated principal and its group claims. Lodge never invents identities.
/// </summary>
public interface ICurrentUserAccessor
{
    AuthenticatedUser GetCurrentUser();
}
