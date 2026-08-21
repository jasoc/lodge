using System.Security.Claims;
using Lodge.Core.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Lodge.Infrastructure.Auth;

/// <summary>
/// Resolves the current user from the claims <see cref="LodgeBearerAuthenticationHandler"/>
/// placed on <c>HttpContext.User</c> — replaces the old hardcoded local-admin mock with a
/// real, per-request identity backed by an actual minted token.
/// </summary>
public sealed class TokenCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TokenCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
        => _httpContextAccessor = httpContextAccessor;

    public AuthenticatedUser GetCurrentUser()
    {
        var principal = _httpContextAccessor.HttpContext?.User
            ?? throw new InvalidOperationException("No authenticated request in scope.");

        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated principal is missing its identity claim.");
        var displayName = principal.FindFirstValue(ClaimTypes.Name) ?? id;
        var groups = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
        var isAdmin = principal.HasClaim("lodge:admin", "true");
        var scopes = principal.FindAll("lodge:scope").Select(c => c.Value).ToArray();
        var isServiceToken = principal.FindFirstValue("lodge:token_kind") == "Service";

        return new AuthenticatedUser(id, displayName, groups, isAdmin, scopes, isServiceToken);
    }
}
