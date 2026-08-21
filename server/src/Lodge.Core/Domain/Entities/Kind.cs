namespace Lodge.Core.Domain.Entities;

/// <summary>
/// A kind onboarded onto Lodge. The model is
/// multi-kind from day one.
/// </summary>
public class Kind
{
    /// <summary>Stable kind code, e.g. "acme". Primary key.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public ICollection<Instance> Instances { get; set; } = new List<Instance>();
}
