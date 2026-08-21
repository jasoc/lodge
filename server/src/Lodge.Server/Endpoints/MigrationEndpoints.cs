using Lodge.Infrastructure.Migrations;
using Lodge.Infrastructure.Persistence;

namespace Lodge.Server.Endpoints;

/// <summary>
/// On-demand re-run of the same migration Lodge.Server already applies to itself once at
/// startup — useful after dropping in a new migration file without restarting the server.
/// </summary>
public static class MigrationEndpoints
{
    public static IEndpointRouteBuilder MapMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/migrate", async (
            IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var result = await MigrationRunner.RunAsync(
                LodgeConnectionString.Build(configuration),
                Path.Combine(RepoRootLocator.Resolve(configuration["GitSnapshot:RepoRoot"]), "migrations"),
                loggerFactory.CreateLogger("Migrations"),
                ct);

            return result.Success ? Results.Ok(result) : Results.Problem(result.Output, statusCode: 500);
        }).RequireAuthorization();

        return app;
    }
}
