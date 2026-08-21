using System.Text.Json;
using Lodge.Core.Domain.Entities;
using Lodge.Infrastructure.Auth;
using Lodge.Server.Auth;
using Lodge.Server.Contracts;

namespace Lodge.Server.Endpoints;

/// <summary>
/// Admin-only service-token management — the machine/automation half of the two-plane
/// auth model. A service token is scoped, revocable, and never bound to a human; it's
/// what a CI job passes as `lodge reconcile --token $LODGE_SERVICE_TOKEN`. Personal
/// tokens (humans) are never created here, only through a login flow.
/// </summary>
public static class TokenEndpoints
{
    public static IEndpointRouteBuilder MapTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tokens").RequireAuthorization().RequireAdmin();

        group.MapPost("", async (CreateServiceTokenRequest body, ApiTokenService tokens, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.DisplayName))
            {
                return Results.Problem("display_name is required.", statusCode: StatusCodes.Status400BadRequest);
            }

            var scopes = body.Scopes ?? Array.Empty<string>();
            var ttl = body.TtlMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;
            var (raw, record) = await tokens.IssueAsync(
                ApiTokenKind.Service,
                subjectId: $"service:{Guid.NewGuid():N}",
                displayName: body.DisplayName,
                groups: Array.Empty<string>(),
                isAdmin: false,
                scopes: scopes,
                ttl: ttl,
                cancellationToken: ct);

            return Results.Ok(new ServiceTokenDto(record.Id, raw, record.SubjectId, scopes, record.ExpiresAt));
        });

        group.MapGet("", async (ApiTokenService tokens, CancellationToken ct) =>
        {
            var records = await tokens.ListAsync(ct);
            var dtos = records.Select(ToSummary).ToList();
            return Results.Ok(dtos);
        });

        group.MapDelete("/{id:guid}", async (Guid id, ApiTokenService tokens, CancellationToken ct) =>
        {
            var revoked = await tokens.RevokeAsync(id, ct);
            return revoked ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }

    private static ApiTokenSummaryDto ToSummary(ApiToken record)
    {
        var groups = JsonSerializer.Deserialize<string[]>(record.GroupsJson) ?? Array.Empty<string>();
        var scopes = JsonSerializer.Deserialize<string[]>(record.ScopesJson) ?? Array.Empty<string>();
        return new ApiTokenSummaryDto(
            record.Id, record.Kind.ToString(), record.DisplayName, record.SubjectId,
            groups, scopes, record.IsAdmin, record.CreatedAt, record.ExpiresAt,
            record.RevokedAt, record.LastUsedAt);
    }
}
