namespace Lodge.Core.Domain.Enums;

/// <summary>
/// Which concrete <c>IRunbookExecutor</c> runs an action — declared explicitly per action
/// in the capability YAML (<c>executor: docker|http</c>), never inferred. The value stored
/// on an action row is a historical record of what the row ran under, same as <see
/// cref="ActionPolicy"/>.
/// </summary>
public enum ExecutorKind
{
    /// <summary>A container run via the Docker CLI — <c>DockerRunbookExecutor</c>.</summary>
    Docker,

    /// <summary>One HTTP request — <c>HttpRunbookExecutor</c>.</summary>
    Http
}
