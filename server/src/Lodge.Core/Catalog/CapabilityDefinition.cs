namespace Lodge.Core.Catalog;

/// <summary>
/// A governance-level grouping of a kind, loaded from
/// <c>inventory/{kind}/capabilities/{capability}.yaml</c> and shown as one
/// card in the UI (e.g. SSO, VirtualMachines). A capability owns the signals it
/// observes in instance inventory and the rules that turn their current values into
/// required actions.
/// </summary>
public sealed class CapabilityDefinition
{
    public string Code { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Name of the capability file this definition was loaded from, e.g. <c>sso.yaml</c>.</summary>
    public string? SourceFile { get; set; }

    public IReadOnlyList<SignalDefinition> Signals { get; set; } = new List<SignalDefinition>();
}

/// <summary>The kind-scoped set of capability definitions the reconciler evaluates.</summary>
public sealed class CapabilityCatalog
{
    public string KindCode { get; set; } = string.Empty;

    public IReadOnlyList<CapabilityDefinition> Capabilities { get; set; } = new List<CapabilityDefinition>();
}
