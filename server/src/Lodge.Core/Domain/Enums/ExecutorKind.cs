namespace Lodge.Core.Domain.Enums;

/// <summary>
/// Which concrete <c>IRunbookExecutor</c> runs an action's runbook — declared explicitly
/// per action in the capability YAML (<c>executor: shell|docker</c>, default <see
/// cref="Shell"/>), never inferred from the runbook string. The value stored on an action
/// row is a historical record of what the row ran under, same as <see
/// cref="ActionPolicy"/>.
/// </summary>
public enum ExecutorKind
{
    /// <summary>A local shell command — <c>ShellCommandRunbookExecutor</c>.</summary>
    Shell,

    /// <summary>An external HTTP webhook target — <c>WebhookRunbookExecutor</c>.</summary>
    Webhook,

    /// <summary>A container run via the Docker CLI — <c>DockerRunbookExecutor</c>.</summary>
    Docker,

    /// <summary>Reserved for a future release — not yet implemented.</summary>
    Octopus,

    /// <summary>Reserved for a future release — not yet implemented.</summary>
    Kubernetes
}
