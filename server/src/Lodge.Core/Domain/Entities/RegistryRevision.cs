namespace Lodge.Core.Domain.Entities;

/// <summary>
/// An immutable snapshot of a instance's inventory YAML at a specific Git ref.
/// </summary>
public class RegistryRevision
{
    public Guid Id { get; set; }

    public Guid InstanceId { get; set; }

    /// <summary>Git ref (commit SHA) this snapshot was taken at.</summary>
    public string GitRef { get; set; } = string.Empty;

    /// <summary>Raw YAML content of the instance file at this ref.</summary>
    public string YamlContent { get; set; } = string.Empty;

    /// <summary>SHA-256 hash of <see cref="YamlContent"/> for dedup/integrity.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public Instance? Instance { get; set; }
}
