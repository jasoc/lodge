namespace Lodge.Server.Auth;

/// <summary>
/// Endpoint-level gates layered on top of <c>RequireAuthorization()</c>. A personal
/// token (a human, always admin or RBAC-gated per runbook) is never scope-checked; only
/// a non-admin service token needs an explicit scope to call a given endpoint. Admin-only
/// endpoints (token management) require the <c>lodge:admin</c> claim regardless of which
/// token kind is calling.
/// </summary>
public static class ScopeEndpointExtensions
{
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var user = context.HttpContext.User;
            var isAdmin = user.HasClaim("lodge:admin", "true");
            var isServiceToken = user.FindFirst("lodge:token_kind")?.Value == "Service";

            if (isAdmin || !isServiceToken)
            {
                return await next(context);
            }

            var hasScope = user.FindAll("lodge:scope").Any(c => c.Value == scope);
            if (!hasScope)
            {
                return Results.Problem(
                    detail: $"This service token lacks the '{scope}' scope.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(context);
        });
    }

    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.User.HasClaim("lodge:admin", "true"))
            {
                return Results.Problem(
                    detail: "This operation requires an admin identity.",
                    statusCode: StatusCodes.Status403Forbidden);
            }
            return await next(context);
        });
    }
}
