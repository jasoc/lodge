namespace Lodge.Infrastructure.Execution;

/// <summary>Configuration for <see cref="ShellCommandRunbookExecutor"/>.</summary>
public sealed class ShellExecutorOptions
{
    public const string SectionName = "ShellExecutor";

    /// <summary>
    /// Optional alias map from a capability's <c>runbook</c> value to the actual
    /// command/script to run. A <c>runbook</c> with no entry here is executed literally
    /// as the command itself — so the simplest homelab setup needs no config at all,
    /// point a capability straight at a script path.
    /// </summary>
    public Dictionary<string, string> Runbooks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Working directory for spawned processes. Empty means the process's own CWD.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Directory for per-run stdout/stderr logs and status sidecar files.</summary>
    public string LogDirectory { get; set; } = "data/runbook-logs";
}
