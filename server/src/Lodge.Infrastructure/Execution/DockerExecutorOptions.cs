namespace Lodge.Infrastructure.Execution;

/// <summary>Configuration for <see cref="DockerRunbookExecutor"/>.</summary>
public sealed class DockerExecutorOptions
{
    public const string SectionName = "DockerExecutor";

    /// <summary>
    /// Path to the docker CLI binary. It talks to whichever daemon the standard docker
    /// environment points at — the local socket by default, or <c>DOCKER_HOST</c> if set
    /// on the server process (e.g. a socket proxy).
    /// </summary>
    public string DockerBinaryPath { get; set; } = "docker";

    /// <summary>Directory for per-run stdout/stderr logs and status sidecar files.</summary>
    public string LogDirectory { get; set; } = "data/docker-runbook-logs";

    /// <summary>
    /// How many built images to keep per playbook folder (newest first) after a fresh
    /// build; older ones are removed best-effort. 0 disables pruning.
    /// </summary>
    public int KeepImagesPerPlaybook { get; set; } = 3;

    /// <summary>
    /// What a catalog <c>mounts:</c> alias may refer to: alias → absolute host path (bind
    /// mount; it's the host's daemon, so it's a path on the docker host), or
    /// <c>volume:&lt;name&gt;</c> for a docker named volume (e.g. an NFS-backed one). The
    /// only things an inventory-defined container can mount — nothing else is reachable.
    /// </summary>
    public Dictionary<string, string> Mounts { get; set; } = new(StringComparer.Ordinal);
}
