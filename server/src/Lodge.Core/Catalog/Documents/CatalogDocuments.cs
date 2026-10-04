using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Lodge.Core.Catalog.Documents;

// The shape of the YAML files the catalog loader reads: what YamlDotNet deserializes into
// (keys are the snake_case of the property names), and what `server/tools/gen-schema`
// turns into schemas/*.schema.json for editor support. The loader stays the authority —
// it adds the semantic rules a schema can't express — so these classes only describe
// shape: [Description] becomes the schema description, [AllowedValues] its enum,
// [RegularExpression] its pattern, [Range] its bounds, [JsonRequired] a required key, and
// [ScalarValue] marks a value that YAML may spell as a string, number or boolean.

/// <summary>Marks a property (or a map's values) that accepts any YAML scalar, not just a string.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ScalarValueAttribute : Attribute;

[Description("One capability file: a governed module of a kind, `inventory/<kind>/capabilities/<capability>.yaml` " +
             "(or an instance's `overrides.yaml`). Signals with rules govern; signals without any make a view.")]
public sealed class CapabilityDocument
{
    [JsonRequired]
    [Description("The capability's code, unique in its kind.")]
    public string? Capability { get; set; }

    [Description("Name shown on the capability card; defaults to the code.")]
    public string? Title { get; set; }

    [Description("What the capability governs.")]
    public string? Description { get; set; }

    [Description("What the capability observes in instance inventory, and the rules that turn it into actions.")]
    public List<SignalDocument>? Signals { get; set; }
}

[Description("A dotted path of the instance YAML, plus the rules that map its value (or its items) to actions.")]
public sealed class SignalDocument
{
    [JsonRequired]
    [Description("Dotted path into the instance YAML, e.g. `features.sso_login`. A keyed collection may contain one `*` " +
                 "segment to address a collection nested inside every item of a parent (`proxmox.virtual_machines.*.compose`); " +
                 "a scalar `*` path (`proxmox.virtual_machines.*.cores`) is only valid in a view.")]
    public string? Path { get; set; }

    [AllowedValues("scalar", "keyed_collection", "scalar_list")]
    [Description("scalar (default): one value, matched with `when`. keyed_collection: a map of stable keys to item bodies. " +
                 "scalar_list: a list of scalars, each its own identity.")]
    public string? Kind { get; set; }

    [Description("keyed_collection only: item fields left out of every item body (what MODIFY compares and `from: item` " +
                 "carries) — used to carve a nested collection out of its parent.")]
    public List<string>? Exclude { get; set; }

    [Description("scalar_list only: a folder of the kind's inventory (relative to `inventory/<kind>/`) whose files the list " +
                 "entries name — paths or `*`/`**` globs. Each file is an item `{file, sha256, content}`; editing it is a MODIFY.")]
    public string? Files { get; set; }

    [Description("Column header where a view shows the signal; defaults to the path's last segment.")]
    public string? Label { get; set; }

    [Description("What to do for each state or change of the signal. No rules at all makes the signal part of a view.")]
    public List<RuleDocument>? Rules { get; set; }
}

[Description("One rule: `when` (scalar signals) or `on` (collection signals) selects the actions required right now.")]
public sealed class RuleDocument
{
    [Description("Scalar signals: the value that triggers the rule; omit to match any present value.")]
    public object? When { get; set; }

    [AllowedValues("add", "delete", "modify")]
    [Description("Collection signals: add (item present, presence not yet confirmed), modify (confirmed item's body changed), " +
                 "delete (item gone while still confirmed present).")]
    public string? On { get; set; }

    [Description("Restricts an add/delete/modify rule to one item key (used by an instance's overrides.yaml).")]
    public string? ItemKey { get; set; }

    [JsonRequired]
    [Description("The actions the rule requires. Each key is unique within the signal.")]
    public List<ActionDocument>? Actions { get; set; }
}

[Description("One action: what runs (`executor` + its block), under which policy, who may confirm it, and its inputs.")]
public sealed class ActionDocument
{
    [JsonRequired]
    [Description("Explicit, signal-unique identity of the action.")]
    public string? Key { get; set; }

    /// <summary>Retired; kept only so the loader can reject it with a clear message. Not part of the schema.</summary>
    [JsonIgnore]
    public string? Runbook { get; set; }

    [Description("Label shown on the capability card; defaults to the key.")]
    public string? Label { get; set; }

    [AllowedValues("AUTO", "MANUAL_REQUIRED", "OPTIONAL")]
    [Description("AUTO runs immediately at reconciliation (never with prompt inputs); MANUAL_REQUIRED (default) waits for a human; " +
                 "OPTIONAL is available but never required.")]
    public string? Policy { get; set; }

    [Description("The user group whose members (and admins) may confirm, retry or revoke the action. Omit, or `nobody`, for anyone. " +
                 "AUTO runs ignore it.")]
    public string? Requires { get; set; }

    [JsonRequired]
    [AllowedValues("container", "http")]
    [Description("What runs the action; selects which block below applies.")]
    public string? Executor { get; set; }

    [Description("`executor: container`: the container to run.")]
    public ContainerDocument? Container { get; set; }

    [Description("`executor: http`: the request to send.")]
    public HttpDocument? Http { get; set; }

    [Description("The action's parameters, by name: resolved from the match (`from`), fixed (`const`), a secret (`secret`) or asked " +
                 "from a human at confirm time (`prompt`). A kind's default inputs are added unless named here (`name: ~` drops one).")]
    public Dictionary<string, InputDocument?>? Inputs { get; set; }

    [Description("Other actions that must have succeeded first, as `signal_path.action_key`. Until then the action is emitted BLOCKED.")]
    public List<string>? DependsOn { get; set; }
}

[Description("A container action. Exactly one of `image` or `build`.")]
public sealed class ContainerDocument
{
    [Description("A ready-made image, pulled as-is. Pin it by digest (`name:tag@sha256:...`).")]
    public string? Image { get; set; }

    [Description("A folder of the inventory with a Dockerfile, built once and cached by content fingerprint.")]
    public BuildDocument? Build { get; set; }

    [Description("Overrides the image's ENTRYPOINT.")]
    public List<string>? Entrypoint { get; set; }

    [Description("Overrides the image's CMD (or follows `entrypoint`). Parameter values are never templated in: they arrive as `LODGE_PARAM_*` environment variables.")]
    public List<string>? Command { get; set; }

    [ScalarValue]
    [Description("Static environment variables. Names may not start with `LODGE_` (reserved for inputs).")]
    public Dictionary<string, string?>? Env { get; set; }

    [Description("Caps on what the container may use.")]
    public ResourcesDocument? Resources { get; set; }

    [Range(1, 86400)]
    [Description("Seconds before a still-running container is killed and the run fails. Omitted: the server default (3600).")]
    public int? TimeoutSeconds { get; set; }

    [Description("Explicit relaxations of the restrictive run defaults (all capabilities dropped, no-new-privileges, read-only root " +
                 "filesystem with tmpfs /tmp and /work, non-root user).")]
    public SecurityDocument? Security { get; set; }

    [RegularExpression("^[a-z0-9][a-z0-9_.-]*$")]
    [Description("A network profile defined by the server (`DockerExecutor__NetworkProfiles__<name>`), not a docker network name. " +
                 "Built in: `default` and `none`.")]
    public string? Network { get; set; }
}

[Description("A playbook image built from a folder of the inventory.")]
public sealed class BuildDocument
{
    [JsonRequired]
    [Description("The build context folder, relative to `inventory/<kind>/` (no `..`).")]
    public string? Context { get; set; }

    [Description("Dockerfile path relative to the context; defaults to `Dockerfile`.")]
    public string? Dockerfile { get; set; }

    [Description("Build target stage.")]
    public string? Target { get; set; }

    [ScalarValue]
    [Description("Static build arguments. Runtime parameters never reach the build.")]
    public Dictionary<string, string?>? Args { get; set; }

    [Description("Extra named build contexts (`COPY --from=<name>`), name → folder relative to `inventory/<kind>/`. " +
                 "They count toward the playbook's fingerprint.")]
    public Dictionary<string, string?>? AdditionalContexts { get; set; }
}

[Description("Caps on what a container run may use.")]
public sealed class ResourcesDocument
{
    [RegularExpression("^[1-9][0-9]*[bBkKmMgG]?$")]
    [Description("Memory limit in docker's size syntax (`512m`, `2g`); swap is capped to the same amount.")]
    public string? Memory { get; set; }

    [Range(0.001, 1024)]
    [Description("Number of CPUs, e.g. 0.5.")]
    public double? Cpus { get; set; }

    [Range(1, int.MaxValue)]
    [Description("The most processes the container may create.")]
    public int? Pids { get; set; }
}

[Description("Relaxations of the restrictive container defaults. Each field undoes one default, for this action only.")]
public sealed class SecurityDocument
{
    [Description("Linux capabilities to add back (every capability is dropped by default), e.g. `NET_ADMIN`.")]
    public List<string>? CapAdd { get; set; }

    [Description("false omits `--security-opt no-new-privileges`. Default true.")]
    public bool? NoNewPrivileges { get; set; }

    [Description("false omits `--read-only` (and the default tmpfs mounts). Default true.")]
    public bool? ReadOnlyRootfs { get; set; }

    [RegularExpression("^(auto|image|[a-z_][a-z0-9_-]*|[0-9]+)(:([a-z_][a-z0-9_-]*|[0-9]+))?$")]
    [Description("`auto` (default): the image's own non-root user, else 65534:65534. `image`: whatever the image says, root included. " +
                 "Or an explicit user/uid with an optional :group/gid.")]
    public string? User { get; set; }

    [Description("Extra writable tmpfs mounts (absolute paths), besides /tmp and /work.")]
    public List<string>? Tmpfs { get; set; }
}

[Description("An HTTP action: one request per run. Every string may reference parameters as `{{ name }}`.")]
public sealed class HttpDocument
{
    [AllowedValues("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD")]
    [Description("HTTP method; defaults to POST.")]
    public string? Method { get; set; }

    [JsonRequired]
    [Description("Absolute http(s) URL, or a `{{ name }}` reference that resolves to one.")]
    public string? Url { get; set; }

    [ScalarValue]
    [Description("Request headers.")]
    public Dictionary<string, string?>? Headers { get; set; }

    [ScalarValue]
    [Description("Query parameters (URL-encoded when substituted).")]
    public Dictionary<string, string?>? Query { get; set; }

    [Description("A mapping or list is sent as JSON (a leaf that is exactly `{{ name }}` whose value is JSON is embedded structured); " +
                 "a scalar is sent as a text template.")]
    public object? Body { get; set; }

    [Range(1, 3600)]
    [Description("Request timeout in seconds; defaults to 60.")]
    public int? TimeoutSeconds { get; set; }

    [Description("Status codes that count as success; any 2xx when omitted.")]
    public List<int>? ExpectStatus { get; set; }
}

[Description("One input: exactly one of `from`, `const`, `prompt` or `secret`. `~` (null) drops a kind default of that name.")]
public sealed class InputDocument
{
    [Description("Bound from the match context: `kind`, `instance`, `path`, `key`, `item`, `item.<field>`, `value`; for collections " +
                 "also `collection`; for nested ones `parent_key`, `parent`, `parent.<field>`.")]
    public string? From { get; set; }

    [ScalarValue]
    [Description("A fixed literal value.")]
    public string? Const { get; set; }

    [Description("Asked from a human at confirm time; this is the prompt text. Not allowed on AUTO actions.")]
    public string? Prompt { get; set; }

    [Description("A secret reference, resolved by the server's secret provider only when the action runs — never stored, never logged.")]
    public string? Secret { get; set; }

    [Description("Prompt inputs only: whether an answer is mandatory.")]
    public bool Required { get; set; }
}

[Description("`inventory/<kind>/kind.yaml`: optional manifest of a kind.")]
public sealed class KindDocument
{
    [Description("Display name of the kind; defaults to its folder name.")]
    public string? Name { get; set; }

    [Description("What every action of the kind gets.")]
    public KindDefaultsDocument? Defaults { get; set; }
}

[Description("Defaults applied to every action of a kind.")]
public sealed class KindDefaultsDocument
{
    [Description("Inputs appended to every action of the kind (capabilities and instance overrides) that doesn't name that input itself; " +
                 "`name: ~` on an action drops a default. Typically the one secret every playbook needs.")]
    public Dictionary<string, InputDocument?>? Inputs { get; set; }
}
