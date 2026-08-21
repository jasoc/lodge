using Lodge.Core.Abstractions;
using Lodge.Infrastructure.Execution;

namespace Lodge.Server.Endpoints;

/// <summary>Body an external system pushes to report a webhook run's outcome.</summary>
public sealed record ExecutionStatusRequest(string Status, string? Message);

/// <summary>
/// Lets an external system push its own status for a webhook run it couldn't answer
/// synchronously about and has no status URL configured for — the third option alongside
/// a synchronous response and polling (see <see cref="WebhookRunbookExecutor"/>).
/// </summary>
public static class ExecutionEndpoints
{
    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/executions/{runId}/status", (
            string runId, ExecutionStatusRequest body, CompositeRunbookExecutor executor) =>
        {
            var state = body.Status.ToLowerInvariant() switch
            {
                "succeeded" or "success" => RunbookRunState.Succeeded,
                "failed" or "failure" or "error" => RunbookRunState.Failed,
                _ => RunbookRunState.Running
            };

            return executor.TryReportWebhookStatus(runId, state, body.Message)
                ? Results.Ok()
                : Results.NotFound();
        }).RequireAuthorization();

        return app;
    }
}
