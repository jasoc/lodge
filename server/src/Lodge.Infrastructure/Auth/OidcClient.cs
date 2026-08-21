using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Lodge.Infrastructure.Auth;

/// <summary>The identity extracted from a validated ID token, ready to mint a Lodge
/// personal token from — the same shape <see cref="ApiTokenService.IssueAsync"/> already
/// expects for the no-auth profile's implicit local-admin.</summary>
public sealed record OidcIdentity(
    string Subject,
    string DisplayName,
    IReadOnlyList<string> Groups,
    bool IsAdmin);

/// <summary>PKCE authorization request ready to redirect the browser to.</summary>
public sealed record OidcAuthorizationRequest(string AuthorizeUrl, string CodeVerifier);

/// <summary>
/// The server-side half of an OIDC Authorization Code + PKCE flow — the CLI and the SPA
/// never talk to the IdP directly, only to Lodge's own <c>/api/v1/auth/oidc/*</c>
/// endpoints (see <c>docs/AGENTS.md</c> invariant 10). Works against any provider that
/// publishes a standard discovery document at <c>{authority}/.well-known/openid-configuration</c>.
/// </summary>
public sealed class OidcClient
{
    private readonly HttpClient _http;
    private readonly IOptions<OidcOptions> _options;
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> _configManager;

    public OidcClient(HttpClient http, IOptions<OidcOptions> options)
    {
        _http = http;
        _options = options;
        _configManager = new Lazy<ConfigurationManager<OpenIdConnectConfiguration>>(() =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{options.Value.Authority.TrimEnd('/')}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                // Only relaxed when the admin themselves configured an http:// authority
                // (a local test IdP) — a real deployment's https:// authority still gets
                // the retriever's own enforcement.
                new HttpDocumentRetriever(_http)
                {
                    RequireHttps = options.Value.Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                }));
    }

    public async Task<OidcAuthorizationRequest> BuildAuthorizationRequestAsync(
        string redirectUri, string state, CancellationToken ct)
    {
        var config = await _configManager.Value.GetConfigurationAsync(ct);
        var opts = _options.Value;

        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var url = QueryHelpers.AddQueryString(config.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = opts.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = opts.Scopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        });

        return new OidcAuthorizationRequest(url, verifier);
    }

    public async Task<OidcIdentity> ExchangeCodeAsync(
        string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var config = await _configManager.Value.GetConfigurationAsync(ct);
        var opts = _options.Value;

        using var response = await _http.PostAsync(config.TokenEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = opts.ClientId,
                ["client_secret"] = opts.ClientSecret,
                ["code_verifier"] = codeVerifier
            }), ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"OIDC token exchange failed ({(int)response.StatusCode}): {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("OIDC token endpoint returned an empty response.");
        if (string.IsNullOrEmpty(payload.IdToken))
        {
            throw new InvalidOperationException("OIDC token response did not include an id_token.");
        }

        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        var validation = await handler.ValidateTokenAsync(payload.IdToken, new TokenValidationParameters
        {
            ValidIssuer = config.Issuer,
            ValidAudience = opts.ClientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true
        });
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"OIDC id_token failed validation: {validation.Exception?.Message}");
        }

        var claims = validation.ClaimsIdentity;
        var subject = claims.FindFirst("sub")?.Value
            ?? throw new InvalidOperationException("OIDC id_token is missing the 'sub' claim.");
        var displayName = claims.FindFirst("name")?.Value
            ?? claims.FindFirst("email")?.Value
            ?? subject;
        var groups = claims.FindAll(opts.GroupsClaim).Select(c => c.Value).ToArray();
        var isAdmin = !string.IsNullOrWhiteSpace(opts.AdminGroup) && groups.Contains(opts.AdminGroup, StringComparer.Ordinal);

        return new OidcIdentity(subject, displayName, groups, isAdmin);
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("id_token")]
        public string? IdToken { get; set; }
    }
}
