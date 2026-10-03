using System.Diagnostics;

namespace Lodge.Infrastructure.Secrets;

/// <summary>Result of running an external process to completion.</summary>
public sealed record ProcessRunResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Narrow seam around <see cref="Process"/>, scoped to <see cref="PassCliSecretProvider"/>
/// only (not shared with <c>ShellCommandRunbookExecutor</c>/<c>DockerRunbookExecutor</c>,
/// which fire-and-forget a long-running process rather than run one to completion) —
/// lets tests substitute a fake without a real <c>pass-cli</c> binary in CI.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken = default);
}

/// <summary>Real implementation: starts the process, waits for exit, captures stdout/stderr.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken = default)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{startInfo.FileName}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new ProcessRunResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
