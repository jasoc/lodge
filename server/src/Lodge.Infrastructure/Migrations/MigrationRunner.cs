using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Lodge.Infrastructure.Migrations;

public sealed record MigrationResult(bool Success, int ExitCode, string Output);

/// <summary>
/// Shells out to Badgie.Migrator — a small standalone .NET tool, deliberately not a
/// library, so this is the one honest way to drive it from code. Runs automatically once
/// at server startup (safe: migrations are only ever applied once, tracked in the
/// database itself) and is also reachable on demand via <c>POST /internal/migrate</c> —
/// one process knows how to bring its own schema up to date, instead of a separate
/// migration container/step.
/// </summary>
public static class MigrationRunner
{
    private const string ToolPackageId = "Badgie.Migrator";
    private const string ToolCommand = "dotnet-badgie-migrator";

    public static async Task<MigrationResult> RunAsync(
        string connectionString, string migrationsDirectory, ILogger logger, CancellationToken cancellationToken = default)
    {
        var toolPath = await ResolveToolPathAsync(logger, cancellationToken);
        var glob = Path.Combine(migrationsDirectory, "*.sql");

        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(connectionString);
        startInfo.ArgumentList.Add(glob);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("-d:Postgres");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {ToolCommand}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = string.Join(
            Environment.NewLine,
            new[] { await stdoutTask, await stderrTask }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var success = process.ExitCode == 0;
        if (success)
        {
            logger.LogInformation("Migrations applied.{NewLine}{Output}", Environment.NewLine, output);
        }
        else
        {
            logger.LogError("Migrations failed (exit {ExitCode}).{NewLine}{Output}", process.ExitCode, Environment.NewLine, output);
        }

        return new MigrationResult(success, process.ExitCode, output);
    }

    /// <summary>Prefers a copy baked into the image at build time; falls back to
    /// installing the tool once as a global tool — self-bootstrapping for native
    /// `dotnet run` dev, where nothing pre-installs it.</summary>
    private static async Task<string> ResolveToolPathAsync(ILogger logger, CancellationToken cancellationToken)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", ToolCommand);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var globalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", ToolCommand);
        if (File.Exists(globalPath))
        {
            return globalPath;
        }

        logger.LogInformation("{Tool} not found, installing it as a global tool (first run only)...", ToolPackageId);
        var install = Process.Start(new ProcessStartInfo("dotnet", $"tool install -g {ToolPackageId}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Failed to start 'dotnet tool install'.");

        var stderrTask = install.StandardError.ReadToEndAsync(cancellationToken);
        await install.WaitForExitAsync(cancellationToken);
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to install {ToolPackageId}: {await stderrTask}");
        }

        return globalPath;
    }
}
