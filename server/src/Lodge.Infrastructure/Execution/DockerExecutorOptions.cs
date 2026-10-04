namespace Lodge.Infrastructure.Execution;

/// <summary>
/// Configuration for the Docker runtime of <see cref="ContainerRunbookExecutor"/> (<see
/// cref="DockerImageBuilder"/>, <see cref="DockerContainerRunner"/>) — plus the run log
/// directory, which the executor itself uses whatever the runtime.
/// </summary>
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
    /// The docker network every action container joins (<c>docker run --network</c>);
    /// null keeps docker's default bridge. Set it to reach services on that network by
    /// name without publishing them on the host — e.g. the Postgres holding Terraform
    /// state (dev: <c>lodge_default</c>, the network of docker-compose.yml).
    /// </summary>
    public string? Network { get; set; }
}
