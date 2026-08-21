namespace Lodge.Core.Catalog;

/// <summary>How a signal's value is shaped in instance inventory.</summary>
public enum SignalKind
{
    /// <summary>A single leaf value (bool/string/number) at the signal path.</summary>
    Scalar,

    /// <summary>A map of stable item keys to item bodies (e.g. <c>virtual_machines.vm-alpha-01: {...}</c>).</summary>
    KeyedCollection,

    /// <summary>A list of scalars where each value is its own identity key (e.g. quirk tags). MODIFY is meaningless here.</summary>
    ScalarList
}

/// <summary>
/// One observed point in a instance's inventory: a dotted path plus the rules that map
/// its current value (or its items' presence/shape) to required actions.
/// </summary>
public sealed class SignalDefinition
{
    /// <summary>Dotted path into the instance YAML, e.g. <c>features.sso_login</c> or <c>virtual_machines</c>.</summary>
    public string Path { get; set; } = string.Empty;

    public SignalKind Kind { get; set; } = SignalKind.Scalar;

    public IReadOnlyList<SignalRule> Rules { get; set; } = new List<SignalRule>();
}
