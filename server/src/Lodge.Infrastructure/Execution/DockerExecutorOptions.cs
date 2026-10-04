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
    /// The docker network the <c>default</c> network profile maps to — what an action joins
    /// unless it names another profile (<c>docker create --network</c>); null keeps docker's
    /// default bridge. Set it to reach services on that network by name without publishing
    /// them on the host — e.g. the Postgres holding Terraform state (dev: <c>lodge_default</c>,
    /// the network of docker-compose.yml).
    /// </summary>
    public string? Network { get; set; }

    /// <summary>
    /// Named network profiles an action may select with <c>container.network</c>: profile
    /// name → docker network name. Built in: <c>default</c> (<see cref="Network"/>) and
    /// <c>none</c> (no network at all). An inventory names a profile, never a docker network,
    /// so whoever can edit it can't attach a container to an arbitrary host network.
    /// Configured as <c>DockerExecutor__NetworkProfiles__&lt;name&gt;=&lt;docker network&gt;</c>.
    /// </summary>
    public Dictionary<string, string> NetworkProfiles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a container may run before it is killed, for actions that set no
    /// <c>container.timeout_seconds</c>. 0 means no limit.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// Names this Lodge deployment on a docker daemon that other Lodge deployments may share:
    /// every container gets a <c>lodge.deployment</c> label with it, and the startup cleanup
    /// only removes containers carrying its own. Defaults to a hash of the database the server
    /// is configured with, so replicas of one deployment (same database) agree; set it explicitly
    /// (<c>DockerExecutor__DeploymentId</c>) when replicas reach the database under different
    /// host names. Null (not configured, e.g. in tests) adds no label and cleans up every
    /// managed container.
    /// </summary>
    public string? DeploymentId { get; set; }

    /// <summary>Size of the writable tmpfs mounts (<c>/tmp</c>, <c>/work</c>, ...) of a read-only container, docker's size syntax.</summary>
    public string TmpfsSize { get; set; } = "256m";
}
