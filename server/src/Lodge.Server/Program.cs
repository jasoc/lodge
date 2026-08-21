using Lodge.Infrastructure;
using Lodge.Infrastructure.Auth;
using Lodge.Infrastructure.Migrations;
using Lodge.Infrastructure.Persistence;
using Lodge.Server.Endpoints;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

// Resolve the repository root (holds inventory/, schemas/, migrations/) so the inventory
// source, capability catalog provider, and migration runner read the SSOT regardless of
// the process's own working directory.
var repoRoot = RepoRootLocator.Resolve(builder.Configuration["GitSnapshot:RepoRoot"]);
builder.Configuration["GitSnapshot:RepoRoot"] = repoRoot;

// Shell executor scripts/logs are addressed relative to the repo root too, so a runbook
// alias like "runbooks/provision-service.sh" resolves the same way regardless of the
// process's own working directory.
if (string.IsNullOrWhiteSpace(builder.Configuration["ShellExecutor:WorkingDirectory"]))
{
    builder.Configuration["ShellExecutor:WorkingDirectory"] = repoRoot;
}
if (string.IsNullOrWhiteSpace(builder.Configuration["ShellExecutor:LogDirectory"]) ||
    !Path.IsPathRooted(builder.Configuration["ShellExecutor:LogDirectory"]))
{
    var relative = builder.Configuration["ShellExecutor:LogDirectory"];
    relative = string.IsNullOrWhiteSpace(relative) ? "data/runbook-logs" : relative;
    builder.Configuration["ShellExecutor:LogDirectory"] = Path.Combine(repoRoot, relative);
}

builder.Services.AddLodgeInfrastructure(builder.Configuration);

builder.Services
    .AddAuthentication(LodgeBearerAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, LodgeBearerAuthenticationHandler>(
        LodgeBearerAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

// snake_case request/response bodies so kind_code, new_ref, ... bind correctly.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var app = builder.Build();

// The server migrates itself: same idempotent, tracked-in-the-database SQL files that
// POST /internal/migrate re-runs on demand — no separate migration container/step.
await MigrationRunner.RunAsync(
    LodgeConnectionString.Build(builder.Configuration),
    Path.Combine(repoRoot, "migrations"),
    app.Logger);

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapTokenEndpoints();
app.MapMigrationEndpoints();
app.MapExecutionEndpoints();
app.MapSyncEndpoints();
app.MapReadEndpoints();
app.MapActionEndpoints();

// The SPA (ui/spa, built into wwwroot at image build time) is served by this same
// process — same origin as the API always, no CORS and no reverse proxy needed. Any
// route that isn't an API route falls back to index.html so Angular's own router can
// take over client-side navigation (e.g. /instances/acme/demo on a hard refresh).
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>
/// Walks up from a starting directory to find the repository root, identified by the
/// presence of both <c>inventory</c> and <c>schemas</c> folders.
/// </summary>
internal static class RepoRootLocator
{
    public static string Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "inventory")) &&
                Directory.Exists(Path.Combine(dir.FullName, "schemas")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
