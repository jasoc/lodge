using Lodge.Core.Abstractions;
using Lodge.Core.Domain.Entities;
using Lodge.Infrastructure.Auth;
using Lodge.Server.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Lodge.Server.Endpoints;

/// <summary>
/// Login is the only place the two auth planes actually differ: in the no-auth profile
/// this unconditionally mints a personal token for an implicit local admin; the OIDC
/// profile mints the same kind of token from a real SSO redirect instead, mirroring the
/// user and their groups into the <see cref="UserDirectory"/> on the way.
/// Everything downstream of a minted token — how a request authenticates — is identical
/// either way, via <see cref="LodgeBearerAuthenticationHandler"/>.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/login", async (
            ApiTokenService tokens, IOptions<AuthOptions> authOptions, CancellationToken ct) =>
        {
            if (!string.Equals(authOptions.Value.Mode, "NoAuth", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    detail: $"Auth mode '{authOptions.Value.Mode}' signs in through its own flow, not this endpoint.",
                    statusCode: StatusCodes.Status501NotImplemented);
            }

            // No auth, no users: one implicit admin, who may run every action.
            var (raw, record) = await tokens.IssueAsync(
                ApiTokenKind.Personal,
                subjectId: "local-admin",
                displayName: "Local Admin",
                groups: Array.Empty<string>(),
                isAdmin: true,
                cancellationToken: ct);

            return Results.Ok(new LoginResponseDto(raw, record.SubjectId, record.ExpiresAt));
        }).AllowAnonymous();

        app.MapGet("/api/v1/auth/me", (ICurrentUserAccessor currentUser) =>
        {
            var user = currentUser.GetCurrentUser();
            return Results.Ok(new MeDto(user.Id, user.DisplayName, user.Groups, user.IsAdmin, user.IsServiceToken));
        }).RequireAuthorization();

        // Public: lets the CLI and SPA discover which login flow to use before they
        // have a token to authenticate with.
        app.MapGet("/api/v1/auth/config", (IOptions<AuthOptions> authOptions) =>
            Results.Ok(new AuthConfigDto(authOptions.Value.Mode))
        ).AllowAnonymous();

        // Step 1 of the OIDC flow: redirect the browser (or the CLI's loopback browser
        // tab) to the IdP. Exactly one of ui_redirect/cli_redirect selects how the
        // callback hands the minted token back — a URL fragment for the SPA (never sent
        // over the network again), a query string for the CLI's local-only loopback
        // listener.
        app.MapGet("/api/v1/auth/oidc/login", async (
            HttpRequest request,
            OidcClient oidc, PendingOidcLoginStore pending,
            IOptions<AuthOptions> authOptions,
            [FromQuery(Name = "ui_redirect")] string? uiRedirect,
            [FromQuery(Name = "cli_redirect")] string? cliRedirect,
            CancellationToken ct) =>
        {
            if (!string.Equals(authOptions.Value.Mode, "Oidc", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    detail: "Auth mode is not 'Oidc'; no SSO login flow is configured.",
                    statusCode: StatusCodes.Status501NotImplemented);
            }
            if (string.IsNullOrWhiteSpace(uiRedirect) == string.IsNullOrWhiteSpace(cliRedirect))
            {
                return Results.Problem(
                    detail: "Provide exactly one of ui_redirect or cli_redirect.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var callbackUri = $"{request.Scheme}://{request.Host}/api/v1/auth/oidc/callback";
            var state = Guid.NewGuid().ToString("N");
            var authorization = await oidc.BuildAuthorizationRequestAsync(callbackUri, state, ct);
            pending.Add(state, new PendingOidcLogin(authorization.CodeVerifier, uiRedirect, cliRedirect, DateTimeOffset.UtcNow));

            return Results.Redirect(authorization.AuthorizeUrl);
        }).AllowAnonymous();

        // Step 2: the IdP redirects here with an authorization code. Lodge exchanges it
        // itself (the client secret never leaves the server), mints a Lodge personal
        // token from the validated ID token's claims, and hands it to whichever caller
        // started the flow.
        app.MapGet("/api/v1/auth/oidc/callback", async (
            OidcClient oidc, PendingOidcLoginStore pendingStore, ApiTokenService tokens, UserDirectory users,
            HttpRequest request, string? code, string? state, string? error,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(state) || !pendingStore.TryTake(state, out var pending))
            {
                return Results.Problem("Unknown or expired login attempt.", statusCode: StatusCodes.Status400BadRequest);
            }
            if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
            {
                return Results.Problem($"SSO login failed: {error ?? "no authorization code returned"}.", statusCode: StatusCodes.Status400BadRequest);
            }

            var callbackUri = $"{request.Scheme}://{request.Host}/api/v1/auth/oidc/callback";
            var identity = await oidc.ExchangeCodeAsync(code, callbackUri, pending.CodeVerifier, ct);
            await users.SyncFromIdentityProviderAsync(identity.Subject, identity.DisplayName, identity.Groups, ct);

            var (raw, record) = await tokens.IssueAsync(
                ApiTokenKind.Personal,
                subjectId: identity.Subject,
                displayName: identity.DisplayName,
                groups: identity.Groups,
                isAdmin: identity.IsAdmin,
                cancellationToken: ct);

            if (pending.CliRedirect is { } cliRedirect)
            {
                var target = QueryHelpers.AddQueryString(cliRedirect, new Dictionary<string, string?>
                {
                    ["token"] = raw,
                    ["subject_id"] = record.SubjectId,
                    ["expires_at"] = record.ExpiresAt?.ToString("O")
                });
                return Results.Redirect(target);
            }

            // Fragment, not query string: it's read client-side by the SPA's callback
            // route and never transmitted back to any server on the next navigation.
            var fragment = $"token={Uri.EscapeDataString(raw)}&subject_id={Uri.EscapeDataString(record.SubjectId)}";
            return Results.Redirect($"{pending.UiRedirect}#{fragment}");
        }).AllowAnonymous();

        return app;
    }
}
