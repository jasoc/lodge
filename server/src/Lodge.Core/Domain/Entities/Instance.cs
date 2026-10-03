namespace Lodge.Core.Domain.Entities;

/// <summary>
/// A customer environment for a given kind. Every instance is kind-scoped.
/// </summary>
public class Instance
{
    public Guid Id { get; set; }

    /// <summary>Owning kind code (FK to <see cref="Kind.Code"/>).</summary>
    public string KindCode { get; set; } = string.Empty;

    /// <summary>Kind-unique instance code, e.g. "instance-alpha".</summary>
    public string InstanceCode { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int Generation { get; set; }

    public string Region { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>False once the instance's folder is gone from the inventory: hidden and no longer reconciled, history kept.</summary>
    public bool Enabled { get; set; } = true;

    public Kind? Kind { get; set; }

    public ICollection<RegistryRevision> Revisions { get; set; } = new List<RegistryRevision>();

    public ICollection<Action> Actions { get; set; } = new List<Action>();
}
