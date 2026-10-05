namespace Lodge.Core.Domain.Enums;

/// <summary>
/// Which concrete <c>IRunbookExecutor</c> runs an action — declared explicitly per action
/// in the capability YAML (<c>executor: container|http|none</c>), never inferred. The value stored
/// on an action row is a historical record of what the row ran under, same as <see
/// cref="ActionPolicy"/>.
/// </summary>
public enum ExecutorKind
{
    /// <summary>
    /// A container — <c>ContainerRunbookExecutor</c>. The catalog describes only the
    /// container (image or build, command, env); which runtime runs it (Docker today)
    /// is server configuration, so an inventory never names one.
    /// </summary>
    Container,

    /// <summary>One HTTP request — <c>HttpRunbookExecutor</c>.</summary>
    Http,

    /// <summary>
    /// Nothing runs — <c>NoneRunbookExecutor</c>. The action exists only to be confirmed: the
    /// human gate in front of a chain of AUTO actions.
    /// </summary>
    None
}
