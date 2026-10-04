using System.Text.Json;
using System.Text.Json.Serialization;
using Lodge.Core.Domain.Enums;

namespace Lodge.Core.Catalog;

/// <summary>How the value of an action input is sourced when an action is generated.</summary>
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

/// <summary>A single declared input to an action.</summary>
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
/// Config for <see cref="ExecutorKind.Container"/>: what a container action runs —
/// either a ready-made <see cref="Image"/> or a <see cref="Build"/> context folder inside
/// the inventory (exactly one of the two), plus an optional entrypoint override and fixed
/// command argv. Parameter values never get templated into <see cref="Entrypoint"/>/<see
/// cref="Command"/> — they flow in purely as <c>LODGE_PARAM_*</c> environment variables
/// (plus the whole map as <c>LODGE_PARAMS_JSON</c>). Nothing here names a runtime: the
/// server decides where the container runs.
/// </summary>
public sealed record ContainerExecutorConfig(
    string? Image,
    IReadOnlyList<string> Command,
    IReadOnlyList<string>? Entrypoint = null,
    ContainerBuildConfig? Build = null,
    IReadOnlyDictionary<string, string>? Env = null);

/// <summary>
/// A playbook image built from a folder of the inventory itself rather than pulled.
/// <see cref="Context"/> is relative to <c>inventory/{kind}/</c>; <see cref="Dockerfile"/>
/// is relative to the context. <see cref="Args"/> are static build args only — runtime
/// parameters never reach the build, so they can't bust the image cache or end up baked
/// into image history. <see cref="Fingerprint"/> is a content hash of the context (plus
/// dockerfile/target/args) stamped by the catalog provider when the catalog is loaded:
/// it is both the image cache key and what an approved action is pinned to.
/// <see cref="AdditionalContexts"/> (name → folder, relative to <c>inventory/{kind}/</c>)
/// are extra named build contexts — <c>COPY --from=&lt;name&gt;</c> in the Dockerfile — so
/// several playbooks can share one folder; each counts toward the fingerprint. Left out
/// of the JSON when null, so a build without them keeps its exact pre-existing encoding.
/// </summary>
public sealed record ContainerBuildConfig(
    string Context,
    string? Dockerfile = null,
    string? Target = null,
    IReadOnlyDictionary<string, string>? Args = null,
    string? Fingerprint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? AdditionalContexts = null);

/// <summary>
/// What a kind's <c>kind.yaml</c> declares for all of its actions: <see cref="Inputs"/>
/// are appended to every action that doesn't name that input itself — typically the one
/// secret every playbook of the kind needs, declared once and still visible on each action.
/// </summary>
public sealed record KindDefaults(IReadOnlyList<RuleInput> Inputs)
{
    public static readonly KindDefaults None = new(Array.Empty<RuleInput>());
}

/// <summary>
/// Config for <see cref="ExecutorKind.Http"/>: one HTTP request per run. Every string —
/// <see cref="Url"/>, header and query values, body leaves — may reference parameters as
/// <c>{{ name }}</c> (an input, a secret, a prompt, or <c>instance</c>/<c>kind</c>),
/// substituted at run time; nothing is resolved at catalog load. <see cref="Body"/> is
/// either a text template (<see cref="BodyIsJson"/> false, sent as-is once substituted) or
/// the canonical JSON of a YAML mapping/list (true): its string leaves are substituted,
/// and a leaf that is exactly <c>{{ name }}</c> whose value is itself JSON (e.g.
/// <c>from: item</c>) is embedded structured rather than as a string. The run succeeds on
/// a status in <see cref="ExpectStatus"/>, or any 2xx when that is null.
/// </summary>
public sealed record HttpExecutorConfig(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, string>? Query = null,
    string? Body = null,
    bool BodyIsJson = false,
    int TimeoutSeconds = 60,
    IReadOnlyList<int>? ExpectStatus = null);

/// <summary>
/// The one canonical JSON encoding of an action's executor config — used both to persist
/// it on the action row and to compare a live row's snapshot against the current catalog,
/// so the two never disagree on formatting. Which type it decodes to follows the row's
/// <see cref="ExecutorKind"/>.
/// </summary>
public static class ExecutorConfigJson
{
    public static string? Serialize(ContainerExecutorConfig? docker, HttpExecutorConfig? http = null)
        => docker is not null ? JsonSerializer.Serialize(docker)
         : http is not null ? JsonSerializer.Serialize(http)
         : null;

    public static ContainerExecutorConfig? DeserializeContainer(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ContainerExecutorConfig>(json);

    public static HttpExecutorConfig? DeserializeHttp(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<HttpExecutorConfig>(json);
}

/// <summary>Who may confirm, retry or revoke an action.</summary>
public static class ActionAccess
{
    /// <summary>The <c>requires</c> value meaning "anyone" — the same as leaving it out.</summary>
    public const string Nobody = "nobody";

    /// <summary>Normalizes a catalog <c>requires</c> value: null for anyone, else the group name.</summary>
    public static string? Normalize(string? requires)
    {
        var value = requires?.Trim();
        return string.IsNullOrEmpty(value) || string.Equals(value, Nobody, StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    /// <summary>True when an action that requires <paramref name="requires"/> may be run by a member of <paramref name="groups"/>.</summary>
    public static bool Allows(string? requires, IEnumerable<string> groups)
        => Normalize(requires) is not { } group || groups.Contains(group, StringComparer.Ordinal);
}

/// <summary>One action a rule requires when it matches: what runs it, and the inputs it needs.</summary>
public sealed class ActionTemplate
{
    /// <summary>Explicit, signal-unique identity of this action template.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Human-facing label shown on the capability card.</summary>
    public string Label { get; set; } = string.Empty;

    public ActionPolicy Policy { get; set; } = ActionPolicy.MANUAL_REQUIRED;

    /// <summary>
    /// The user group whose members alone may confirm, retry or revoke this action; null
    /// (catalog: omitted, or <c>nobody</c>) means anyone may. AUTO actions start on their
    /// own regardless — this gates humans, not the reconciler.
    /// </summary>
    public string? Requires { get; set; }

    /// <summary>Which executor runs this action — declared explicitly, never inferred.</summary>
    public ExecutorKind ExecutorKind { get; set; } = ExecutorKind.Container;

    /// <summary>Non-null only when <see cref="ExecutorKind"/> is <see cref="ExecutorKind.Container"/>.</summary>
    public ContainerExecutorConfig? Container { get; set; }

    /// <summary>Non-null only when <see cref="ExecutorKind"/> is <see cref="ExecutorKind.Http"/>.</summary>
    public HttpExecutorConfig? Http { get; set; }

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
