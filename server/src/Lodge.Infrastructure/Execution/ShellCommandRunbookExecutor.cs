using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Lodge.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// Runs a runbook as a local shell command — the homelab-appropriate default executor,
/// since it needs nothing beyond a script on disk. A <c>runbook</c> value is resolved
/// through the optional <see cref="ShellExecutorOptions.Runbooks"/> alias map first; if
/// there's no entry, the <c>runbook</c> string itself is executed directly. Every
/// invocation's parameters are passed as <c>LODGE_PARAM_*</c> environment variables, and
/// stdout/stderr are captured to a per-run log file under
/// <see cref="ShellExecutorOptions.LogDirectory"/>.
///
/// Run state is tracked in memory for the lifetime of this process (the common case:
/// this is the instance that started the run) and mirrored to a JSON sidecar file next
/// to the log, so a run survives being asked about after a server restart in a *degraded*
/// but honest way — the outcome of an unfinished run is reported as unknown rather than
/// guessed, since a non-child process's exit code generally isn't recoverable on Linux
/// once this process didn't start it.
/// </summary>
public sealed class ShellCommandRunbookExecutor : IRunbookExecutor
{
    private readonly ShellExecutorOptions _options;
    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);

    public ShellCommandRunbookExecutor(IOptions<ShellExecutorOptions> options)
    {
        _options = options.Value;
        Directory.CreateDirectory(_options.LogDirectory);
    }

    public Task<RunbookRunHandle> StartAsync(RunbookExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var command = _options.Runbooks.TryGetValue(request.RunbookRef, out var alias) ? alias : request.RunbookRef;
        var runId = $"shell-{Guid.NewGuid():N}";
        var logPath = Path.Combine(_options.LogDirectory, $"{runId}.log");
        var sidecarPath = Path.Combine(_options.LogDirectory, $"{runId}.json");

        var escapedCommand = command.Replace("\"", "\\\"");
        var escapedLogPath = logPath.Replace("'", "'\\''");
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"-c \"{escapedCommand} > '{escapedLogPath}' 2>&1\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(_options.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : _options.WorkingDirectory
        };

        startInfo.Environment["LODGE_ACTION_ID"] = request.ActionId.ToString();
        startInfo.Environment["LODGE_KIND_CODE"] = request.KindCode;
        startInfo.Environment["LODGE_INSTANCE_CODE"] = request.InstanceCode;
        startInfo.Environment["LODGE_RUNBOOK_REF"] = request.RunbookRef;
        foreach (var (key, value) in request.Parameters)
        {
            var envName = "LODGE_PARAM_" + new string(key.ToUpperInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            startInfo.Environment[envName] = value ?? string.Empty;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start runbook process for '{request.RunbookRef}'.");

        var startedAt = DateTimeOffset.UtcNow;
        var state = new RunState(process, request.RunbookRef, logPath, sidecarPath, startedAt);
        _runs[runId] = state;
        WriteSidecar(state, exitCode: null, completedAt: null);

        return Task.FromResult(new RunbookRunHandle(runId, RunbookRunState.Running));
    }

    public Task<RunbookRunStatus> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (_runs.TryGetValue(runId, out var state))
        {
            return Task.FromResult(StatusFromLiveProcess(runId, state));
        }

        return Task.FromResult(StatusFromSidecar(runId));
    }

    private RunbookRunStatus StatusFromLiveProcess(string runId, RunState state)
    {
        if (!state.Process.HasExited)
        {
            return new RunbookRunStatus(runId, RunbookRunState.Running,
                $"Runbook '{state.RunbookRef}' is running (pid {state.Process.Id}).", DateTimeOffset.UtcNow);
        }

        var exitCode = state.Process.ExitCode;
        var completedAt = DateTimeOffset.UtcNow;
        WriteSidecar(state, exitCode, completedAt);

        return exitCode == 0
            ? new RunbookRunStatus(runId, RunbookRunState.Succeeded,
                $"Runbook '{state.RunbookRef}' completed (exit 0). Log: {state.LogPath}", completedAt)
            : new RunbookRunStatus(runId, RunbookRunState.Failed,
                $"Runbook '{state.RunbookRef}' failed (exit {exitCode}). Log: {state.LogPath}", completedAt);
    }

    private RunbookRunStatus StatusFromSidecar(string runId)
    {
        var sidecarPath = Path.Combine(_options.LogDirectory, $"{runId}.json");
        if (!File.Exists(sidecarPath))
        {
            return new RunbookRunStatus(runId, RunbookRunState.Failed, "Unknown run.", DateTimeOffset.UtcNow);
        }

        var sidecar = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(sidecarPath))!;
        if (sidecar.ExitCode is { } exitCode)
        {
            var completedAt = sidecar.CompletedAt ?? DateTimeOffset.UtcNow;
            return exitCode == 0
                ? new RunbookRunStatus(runId, RunbookRunState.Succeeded, $"Runbook '{sidecar.RunbookRef}' completed (exit 0).", completedAt)
                : new RunbookRunStatus(runId, RunbookRunState.Failed, $"Runbook '{sidecar.RunbookRef}' failed (exit {exitCode}).", completedAt);
        }

        // The run was still in flight when this server instance last knew about it and
        // we've since restarted — a non-child process's exit code isn't reliably
        // recoverable, so report the outcome as unknown rather than guessing.
        return new RunbookRunStatus(runId, RunbookRunState.Failed,
            $"Runbook '{sidecar.RunbookRef}' outcome unknown: the server restarted while this run was in progress.",
            DateTimeOffset.UtcNow);
    }

    private static void WriteSidecar(RunState state, int? exitCode, DateTimeOffset? completedAt)
    {
        var sidecar = new Sidecar(state.RunbookRef, state.Process.Id, state.StartedAt, completedAt, exitCode);
        File.WriteAllText(state.SidecarPath, JsonSerializer.Serialize(sidecar));
    }

    private sealed record RunState(Process Process, string RunbookRef, string LogPath, string SidecarPath, DateTimeOffset StartedAt);

    private sealed record Sidecar(string RunbookRef, int Pid, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int? ExitCode);
}
