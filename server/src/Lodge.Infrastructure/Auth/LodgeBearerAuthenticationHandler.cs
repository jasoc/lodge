using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Auth;

/// <summary>
/// Validates the <c>Authorization: Bearer &lt;token&gt;</c> header against
/// <see cref="ApiTokenService"/> and, on success, places the resolved identity on
/// <c>HttpContext.User</c> as claims for <see cref="TokenCurrentUserAccessor"/> to read.
/// The same scheme authenticates both auth planes (personal and service tokens) — they
/// differ only in how the token was minted, never in how a request is authenticated.
/// </summary>
public sealed class LodgeBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "LodgeBearer";
    private const string BearerPrefix = "Bearer ";

    private readonly ApiTokenService _tokens;

    public LodgeBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiTokenService tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
        {
            return AuthenticateResult.NoResult();
        }

        var value = header.ToString();
        if (!value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var rawToken = value[BearerPrefix.Length..].Trim();
        if (rawToken.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        var user = await _tokens.ValidateAsync(rawToken, Context.RequestAborted);
        if (user is null)
        {
            return AuthenticateResult.Fail("Invalid, revoked, or expired token.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.DisplayName)
        };
        claims.AddRange(user.Groups.Select(g => new Claim(ClaimTypes.Role, g)));
        claims.AddRange(user.Scopes.Select(s => new Claim("lodge:scope", s)));
        claims.Add(new Claim("lodge:token_kind", user.IsServiceToken ? "Service" : "Personal"));
        if (user.IsAdmin)
        {
            claims.Add(new Claim("lodge:admin", "true"));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
