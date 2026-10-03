using System.Text.Json;
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
    Prompt,

    /// <summary>
    /// A reference to a secret, resolved to a plaintext value only inside
    /// <see cref="Lodge.Core.Abstractions.ISecretProvider"/> at execution time — never by
    /// the pure Reconciler, never persisted resolved, never logged.
    /// </summary>
    Secret
}

/// <summary>A single declared input to a runbook.</summary>
public sealed class RuleInput
{
    public string Name { get; set; } = string.Empty;

    public RuleInputKind Kind { get; set; }

    /// <summary>
    /// For <see cref="RuleInputKind.From"/>: the context key. For
    /// <see cref="RuleInputKind.Const"/>: the literal value. For
    /// <see cref="RuleInputKind.Prompt"/>: the human-facing prompt text. For
    /// <see cref="RuleInputKind.Secret"/>: the secret reference.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>Only meaningful for <see cref="RuleInputKind.Prompt"/>: whether the value is mandatory.</summary>
    public bool Required { get; set; }
}

/// <summary>A prompt whose value must be supplied by a human at confirm time.</summary>
public sealed record PendingPrompt(string Name, string Prompt, bool Required);

/// <summary>
/// An unresolved secret reference carried on a required action — resolved to a plaintext
/// value only by <see cref="Lodge.Core.Abstractions.ISecretProvider"/> at execution time.
/// </summary>
public sealed record SecretInputRef(string Name, string SecretRef);

/// <summary>
/// Config for <see cref="ExecutorKind.Docker"/>: what a Docker-executed action runs —
/// either a ready-made <see cref="Image"/> or a <see cref="Build"/> context folder inside
/// the inventory (exactly one of the two), plus an optional entrypoint override and fixed
/// command argv. Parameter values never get templated into <see cref="Entrypoint"/>/<see
/// cref="Command"/> — they flow in purely as <c>LODGE_PARAM_*</c> environment variables
/// (plus the whole map as <c>LODGE_PARAMS_JSON</c>), the same convention the shell
/// executor already uses.
/// </summary>
public sealed record DockerExecutorConfig(
    string? Image,
    IReadOnlyList<string> Command,
    IReadOnlyList<string>? Entrypoint = null,
    DockerBuildConfig? Build = null);

/// <summary>
/// A playbook image built from a folder of the inventory itself rather than pulled.
/// <see cref="Context"/> is relative to <c>inventory/{kind}/</c>; <see cref="Dockerfile"/>
/// is relative to the context. <see cref="Args"/> are static build args only — runtime
/// parameters never reach the build, so they can't bust the image cache or end up baked
/// into image history. <see cref="Fingerprint"/> is a content hash of the context (plus
/// dockerfile/target/args) stamped by the catalog provider when the catalog is loaded:
/// it is both the image cache key and what an approved action is pinned to.
/// </summary>
public sealed record DockerBuildConfig(
    string Context,
    string? Dockerfile = null,
    string? Target = null,
    IReadOnlyDictionary<string, string>? Args = null,
    string? Fingerprint = null);

/// <summary>
/// The one canonical JSON encoding of an action's executor config — used both to persist
/// it on the action row and to compare a live row's snapshot against the current catalog,
/// so the two never disagree on formatting.
/// </summary>
public static class ExecutorConfigJson
{
    public static string? Serialize(DockerExecutorConfig? config)
        => config is null ? null : JsonSerializer.Serialize(config);

    public static DockerExecutorConfig? Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<DockerExecutorConfig>(json);
}

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

    /// <summary>Which executor runs this action's runbook — declared explicitly, never inferred.</summary>
    public ExecutorKind ExecutorKind { get; set; } = ExecutorKind.Shell;

    /// <summary>Non-null only when <see cref="ExecutorKind"/> is <see cref="ExecutorKind.Docker"/>.</summary>
    public DockerExecutorConfig? Docker { get; set; }

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
