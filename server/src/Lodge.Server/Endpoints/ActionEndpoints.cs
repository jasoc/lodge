using Lodge.Infrastructure.Execution;
using Lodge.Server.Auth;

namespace Lodge.Server.Endpoints;

/// <summary>Body for confirming an action: optional actor and any prompt values.</summary>
public sealed record ConfirmActionRequest(string? Actor, Dictionary<string, string?>? Prompts);

/// <summary>Body for invalidating an action: optional actor.</summary>
public sealed record InvalidateActionRequest(string? Actor);

/// <summary>
/// Human-driven action operations: confirm (start the runbook, RBAC-gated), invalidate,
/// and status (realtime poll). Lodge supervises the operation; the executor runs it.
/// </summary>
public static class ActionEndpoints
{
    public static IEndpointRouteBuilder MapActionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").RequireAuthorization().RequireScope("actions");

        group.MapPost("/kinds/{kindCode}/instances/{instanceCode}/actions/{actionId:guid}/confirm", async (
            string kindCode, string instanceCode, Guid actionId,
            ConfirmActionRequest? body,
            ActionExecutionService execution,
            CancellationToken ct) =>
        {
            var actor = string.IsNullOrWhiteSpace(body?.Actor) ? "operator" : body!.Actor!;
            var result = await execution.ConfirmAsync(kindCode, instanceCode, actionId, actor, body?.Prompts, ct);
            if (result is null)
            {
                return Results.NotFound();
            }
            return result.Denied
                ? Results.Json(result, statusCode: StatusCodes.Status403Forbidden)
                : Results.Ok(result);
        });

        group.MapPost("/kinds/{kindCode}/instances/{instanceCode}/actions/{actionId:guid}/invalidate", async (
            string kindCode, string instanceCode, Guid actionId,
            InvalidateActionRequest? body,
            ActionExecutionService execution,
            CancellationToken ct) =>
        {
            var actor = string.IsNullOrWhiteSpace(body?.Actor) ? "operator" : body!.Actor!;
            var result = await execution.InvalidateAsync(kindCode, instanceCode, actionId, actor, ct);
            if (result is null)
            {
                return Results.NotFound();
            }
            return result.Denied
                ? Results.Json(result, statusCode: StatusCodes.Status403Forbidden)
                : Results.Ok(result);
        });

        group.MapPost("/kinds/{kindCode}/instances/{instanceCode}/actions/{actionId:guid}/stop", async (
            string kindCode, string instanceCode, Guid actionId,
            InvalidateActionRequest? body,
            ActionExecutionService execution,
            CancellationToken ct) =>
        {
            var actor = string.IsNullOrWhiteSpace(body?.Actor) ? "operator" : body!.Actor!;
            var result = await execution.StopAsync(kindCode, instanceCode, actionId, actor, ct);
            if (result is null)
            {
                return Results.NotFound();
            }
            return result.Denied
                ? Results.Json(result, statusCode: StatusCodes.Status403Forbidden)
                : Results.Ok(result);
        });

        group.MapGet("/kinds/{kindCode}/instances/{instanceCode}/actions/{actionId:guid}/status", async (
            string kindCode, string instanceCode, Guid actionId,
            ActionExecutionService execution,
            CancellationToken ct) =>
        {
            var result = await execution.RefreshStatusAsync(kindCode, instanceCode, actionId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        return app;
    }
}
