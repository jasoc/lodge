namespace Lodge.Infrastructure.Auth;

/// <summary>
/// Configures the OIDC profile (<c>Auth:Mode = Oidc</c>). Works against any standard
/// OIDC provider (Keycloak, Entra ID, Auth0, Google Workspace, ...) via its discovery
/// document — nothing here is provider-specific. Only used when <see cref="AuthOptions.Mode"/>
/// is <c>Oidc</c>; ignored entirely in the <c>NoAuth</c> homelab default.
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Auth:Oidc";

    /// <summary>The issuer URL; <c>{Authority}/.well-known/openid-configuration</c> must
    /// resolve to the provider's discovery document.</summary>
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Space-separated scopes requested at the IdP. Must include a scope that
    /// causes the provider to emit <see cref="GroupsClaim"/> in the ID token (provider-specific —
    /// e.g. a custom scope/mapper in Keycloak, or an app-manifest group claim in Entra ID).</summary>
    public string Scopes { get; set; } = "openid profile email groups";

    /// <summary>Which ID token claim carries the caller's group memberships, mapped onto
    /// <c>AuthenticatedUser.Groups</c> — the same shape <see cref="FilePermissionResolver"/>
    /// already expects from any auth plane.</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>A group name that grants <c>AuthenticatedUser.IsAdmin</c>. Empty disables
    /// admin auto-grant via SSO groups entirely (no one is admin until granted some other way).</summary>
    public string AdminGroup { get; set; } = string.Empty;
}
