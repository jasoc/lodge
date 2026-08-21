using Lodge.Core.Domain.Enums;

namespace Lodge.Core.Catalog;

/// <summary>How the value of a runbook input is sourced when an action is generated.</summary>
public enum RuleInputKind
{
    /// <summary>Bound from the reconciliation context (<c>instance</c>, <c>kind</c>, <c>path</c>, <c>key</c>, <c>item</c>, <c>item.&lt;field&gt;</c>, <c>value</c>).</summary>
    From,

    /// <summary>A fixed literal value declared in the capability file.</summary>
    Const,

    /// <summary>Asked from the human at confirm time; unresolved until then.</summary>
    Prompt
}

/// <summary>A single declared input to a runbook.</summary>
public sealed class RuleInput
{
    public string Name { get; set; } = string.Empty;

    public RuleInputKind Kind { get; set; }

    /// <summary>
    /// For <see cref="RuleInputKind.From"/>: the context key. For
    /// <see cref="RuleInputKind.Const"/>: the literal value. For
    /// <see cref="RuleInputKind.Prompt"/>: the human-facing prompt text.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>Only meaningful for <see cref="RuleInputKind.Prompt"/>: whether the value is mandatory.</summary>
    public bool Required { get; set; }
}

/// <summary>A prompt whose value must be supplied by a human at confirm time.</summary>
public sealed record PendingPrompt(string Name, string Prompt, bool Required);

/// <summary>
/// One action a rule requires when it matches: a direct Octopus runbook reference plus
/// the inputs it needs. Lodge governs; Octopus executes.
/// </summary>
public sealed class ActionTemplate
{
    /// <summary>
    /// Explicit, signal-unique identity of this action template — independent of
    /// <see cref="Runbook"/>, since the same runbook may be invoked by more than one
    /// conceptually distinct action.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Direct Octopus runbook reference, e.g. <c>acme-instance-ops/configure-sso</c>.</summary>
    public string Runbook { get; set; } = string.Empty;

    /// <summary>Human-facing label shown on the capability card.</summary>
    public string Label { get; set; } = string.Empty;

    public ActionPolicy Policy { get; set; } = ActionPolicy.MANUAL_REQUIRED;

    public IReadOnlyList<RuleInput> Inputs { get; set; } = new List<RuleInput>();

    /// <summary>
    /// Other actions this one depends on, addressed as raw <c>signal_path.action_key</c>
    /// strings (the shared <see cref="ActionAddressParser"/> syntax, minus item brackets
    /// and the universal marker — neither makes sense for a catalog-static dependency).
    /// Resolved and validated (unknown target, cycles) once per kind catalog in
    /// <see cref="CapabilityCatalogLoader.Merge"/>. A collection-signal target has no item
    /// key of its own: at evaluation time it is resolved against whichever item the
    /// depending action is currently being evaluated for, when the target signal equals
    /// the depending action's own signal; otherwise it addresses the target's scalar
    /// identity (no item).
    /// </summary>
    public IReadOnlyList<string> DependsOn { get; set; } = new List<string>();
}

/// <summary>
/// A rule over a signal's <em>current</em> value. STATE rules match when the signal's
/// value equals <see cref="WhenJson"/> (or any present value when null); ADD/DELETE/
/// MODIFY rules apply per item of a collection signal. Whichever rule matches the
/// current state determines the actions required right now — there is no notion of an
/// observed transition.
/// </summary>
public sealed class SignalRule
{
    public SignalTrigger Trigger { get; set; } = SignalTrigger.STATE;

    /// <summary>Canonical JSON literal the current value must equal; null = any present value. STATE rules only.</summary>
    public string? WhenJson { get; set; }

    /// <summary>
    /// Restricts an ADD/DELETE/MODIFY rule to one specific item key (used by per-instance
    /// override files); null = applies to every key of the collection.
    /// </summary>
    public string? ItemKey { get; set; }

    public IReadOnlyList<ActionTemplate> Actions { get; set; } = new List<ActionTemplate>();
}
