namespace Lodge.Infrastructure.Auth;

/// <summary>
/// Selects which auth plane's login flow the server exposes. <c>NoAuth</c> (the homelab
/// default) mints a personal token unconditionally, bound to an implicit local-admin
/// identity. <c>Oidc</c> is a later drop-in that mints the same kind of token from a real
/// SSO redirect flow instead — the token *validation* path never changes.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary><c>NoAuth</c> or <c>Oidc</c>.</summary>
    public string Mode { get; set; } = "NoAuth";
}
