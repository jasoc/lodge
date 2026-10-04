using Lodge.Core.Diff;
using Lodge.Core.Domain.Enums;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lodge.Core.Catalog;

/// <summary>
/// Loads a <see cref="CapabilityDefinition"/> from one capability YAML file and merges
/// per-instance override definitions additively into the generic kind catalog.
/// STATE-rule <c>when</c> values are normalized to the same canonical JSON the
/// reconciler derives from instance inventory, so comparisons are exact.
/// </summary>
public static class CapabilityCatalogLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static CapabilityDefinition LoadCapability(string yaml, string? sourceFile = null)
    {
        CapabilityDto dto;
        try
        {
            dto = Deserializer.Deserialize<CapabilityDto>(yaml) ?? new CapabilityDto();
        }
        catch (Exception ex)
        {
            throw new CatalogFormatException($"{sourceFile ?? "capability file"}: invalid YAML — {ex.Message}", ex);
        }

        var code = dto.Capability?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            throw new CatalogFormatException($"{sourceFile ?? "capability file"}: missing required 'capability' code.");
        }

        var signals = new List<SignalDefinition>();
        foreach (var s in dto.Signals ?? new List<SignalDto>())
        {
            signals.Add(LoadSignal(code, s, sourceFile));
        }

        return new CapabilityDefinition
        {
            Code = code,
            Title = dto.Title ?? code,
            Description = dto.Description ?? string.Empty,
            SourceFile = sourceFile,
            Signals = signals
        };
    }

    /// <summary>
    /// Additively merges instance override definitions into the generic kind catalog:
    /// capabilities match by code, signals by path, and override rules are appended after
    /// the generic ones. Overrides never replace or remove generic rules.
    /// </summary>
    public static CapabilityCatalog Merge(
        string kindCode,
        IReadOnlyList<CapabilityDefinition> generic,
        IReadOnlyList<CapabilityDefinition> instanceOverrides)
    {
        var merged = generic.Select(c => new CapabilityDefinition
        {
            Code = c.Code,
            Title = c.Title,
            Description = c.Description,
            SourceFile = c.SourceFile,
            Signals = c.Signals.Select(s => new SignalDefinition
            {
                Path = s.Path,
                Kind = s.Kind,
                Exclude = s.Exclude,
                Files = s.Files,
                FileIndex = s.FileIndex,
                Rules = s.Rules.ToList()
            }).ToList<SignalDefinition>()
        }).ToList();

        foreach (var over in instanceOverrides)
        {
            var target = merged.FirstOrDefault(c => string.Equals(c.Code, over.Code, StringComparison.Ordinal));
            if (target is null)
            {
                merged.Add(over);
                continue;
            }

            var signals = (List<SignalDefinition>)target.Signals;
            foreach (var overSignal in over.Signals)
            {
                var targetSignal = signals.FirstOrDefault(s => string.Equals(s.Path, overSignal.Path, StringComparison.Ordinal));
                if (targetSignal is null)
                {
                    signals.Add(overSignal);
                    continue;
                }

                var rules = (List<SignalRule>)targetSignal.Rules;
                rules.AddRange(overSignal.Rules);
            }
        }

        foreach (var capability in merged)
        {
            foreach (var signal in capability.Signals)
            {
                ValidateSignalInvariants(capability.Code, signal);
            }
        }

        var catalog = new CapabilityCatalog { KindCode = kindCode, Capabilities = merged };
        ValidateDependsOn(catalog);
        return catalog;
    }

    /// <summary>
    /// Resolves every action template's raw <c>depends_on</c> addresses against the fully
    /// merged catalog: rejects item-bracket/universal forms (a catalog-static dependency
    /// can't reference a specific instance-runtime item), rejects references to an unknown
    /// (signal, action key) node, and rejects cycles in the resulting dependency graph.
    /// This is a static authoring error, so — unlike instance-data-level <c>past_history</c>
    /// validation — it fails loudly at load time rather than degrading per-entry.
    /// </summary>
    private static void ValidateDependsOn(CapabilityCatalog catalog)
    {
        var nodes = new HashSet<(string SignalPath, string ActionKey)>();
        foreach (var capability in catalog.Capabilities)
        {
            foreach (var signal in capability.Signals)
            {
                foreach (var action in signal.Rules.SelectMany(r => r.Actions))
                {
                    nodes.Add((signal.Path, action.Key));
                }
            }
        }

        var edges = new Dictionary<(string SignalPath, string ActionKey), List<(string SignalPath, string ActionKey)>>();
        foreach (var capability in catalog.Capabilities)
        {
            foreach (var signal in capability.Signals)
            {
                foreach (var action in signal.Rules.SelectMany(r => r.Actions))
                {
                    var from = (signal.Path, action.Key);
                    foreach (var raw in action.DependsOn)
                    {
                        if (!ActionAddressParser.TryParse(raw, catalog, out var address, out var parseError))
                        {
                            throw new CatalogFormatException(
                                $"capability '{capability.Code}', signal '{signal.Path}', action '{action.Key}': invalid depends_on '{raw}' — {parseError}");
                        }
                        if (address.IsUniversal || address.ItemKey is not null)
                        {
                            throw new CatalogFormatException(
                                $"capability '{capability.Code}', signal '{signal.Path}', action '{action.Key}': depends_on '{raw}' — " +
                                "only 'signal_path.action_key' is valid (no item bracket, no universal marker); a collection-signal target " +
                                "is resolved against the current item automatically.");
                        }

                        var to = (address.SignalPath!, address.ActionKey);
                        if (!nodes.Contains(to))
                        {
                            throw new CatalogFormatException(
                                $"capability '{capability.Code}', signal '{signal.Path}', action '{action.Key}': depends_on '{raw}' " +
                                "does not match any known action key.");
                        }

                        if (!edges.TryGetValue(from, out var list))
                        {
                            edges[from] = list = new List<(string, string)>();
                        }
                        list.Add(to);
                    }
                }
            }
        }

        var visiting = new HashSet<(string, string)>();
        var visited = new HashSet<(string, string)>();
        foreach (var node in nodes)
        {
            DetectCycle(node, edges, visiting, visited);
        }
    }

    private static void DetectCycle(
        (string SignalPath, string ActionKey) node,
        Dictionary<(string, string), List<(string, string)>> edges,
        HashSet<(string, string)> visiting,
        HashSet<(string, string)> visited)
    {
        if (visited.Contains(node))
        {
            return;
        }
        if (!visiting.Add(node))
        {
            throw new CatalogFormatException(
                $"depends_on cycle detected involving '{node.SignalPath}.{node.ActionKey}'.");
        }

        if (edges.TryGetValue(node, out var targets))
        {
            foreach (var target in targets)
            {
                DetectCycle(target, edges, visiting, visited);
            }
        }

        visiting.Remove(node);
        visited.Add(node);
    }

    private static SignalDefinition LoadSignal(string capabilityCode, SignalDto dto, string? sourceFile)
    {
        var path = dto.Path?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            throw new CatalogFormatException($"{Location(sourceFile, capabilityCode)}: a signal is missing its 'path'.");
        }

        var kind = ParseKind(dto.Kind, sourceFile, capabilityCode, path);

        var rules = new List<SignalRule>();
        foreach (var r in dto.Rules ?? new List<RuleDto>())
        {
            rules.Add(LoadRule(capabilityCode, path, kind, !string.IsNullOrWhiteSpace(dto.Files), r, sourceFile));
        }

        var where = $"{Location(sourceFile, capabilityCode)}, signal '{path}'";
        var segments = path.Split('.');
        var wildcards = segments.Count(s => s == "*");
        if (wildcards > 0 &&
            (kind == SignalKind.Scalar || wildcards > 1 || segments[0] == "*" || segments[^1] == "*"))
        {
            throw new CatalogFormatException(
                $"{where}: a '*' segment is only valid once, in the middle of a collection path " +
                "(e.g. 'proxmox.virtual_machines.*.compose').");
        }
        if (segments.Any(s => s.Length == 0))
        {
            throw new CatalogFormatException($"{where}: the path has an empty segment.");
        }

        var exclude = (dto.Exclude ?? new List<string>()).Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        if (exclude.Count > 0 && kind != SignalKind.KeyedCollection)
        {
            throw new CatalogFormatException($"{where}: 'exclude' only applies to keyed_collection signals.");
        }

        string? files = null;
        if (!string.IsNullOrWhiteSpace(dto.Files))
        {
            if (kind != SignalKind.ScalarList)
            {
                throw new CatalogFormatException($"{where}: 'files' only applies to scalar_list signals (a list of file paths).");
            }
            files = NormalizeRelativePath(dto.Files, "files", where);
        }

        var signal = new SignalDefinition { Path = path, Kind = kind, Exclude = exclude, Files = files, Rules = rules };
        ValidateSignalInvariants(capabilityCode, signal, sourceFile);
        return signal;
    }

    private static SignalRule LoadRule(string capabilityCode, string path, SignalKind kind, bool fileBacked, RuleDto dto, string? sourceFile)
    {
        var where = $"{Location(sourceFile, capabilityCode)}, signal '{path}'";
        var trigger = ParseTrigger(dto.On, where);

        if (kind == SignalKind.Scalar && trigger != SignalTrigger.STATE)
        {
            throw new CatalogFormatException($"{where}: 'on: {dto.On}' rules are only valid on collection signals; scalar signals use 'when' state-match rules.");
        }
        if (kind != SignalKind.Scalar && trigger == SignalTrigger.STATE)
        {
            throw new CatalogFormatException($"{where}: collection signals take 'on: add|delete|modify' rules, not 'when' state-match rules.");
        }
        if (kind == SignalKind.ScalarList && trigger == SignalTrigger.MODIFY && !fileBacked)
        {
            throw new CatalogFormatException(
                $"{where}: 'on: modify' is meaningless for a scalar_list signal — a scalar value is its own identity " +
                "(unless the list names files: with 'files:', editing a file is a modify).");
        }

        var actions = new List<ActionTemplate>();
        foreach (var a in dto.Actions ?? new List<ActionDto>())
        {
            if (string.IsNullOrWhiteSpace(a.Runbook))
            {
                throw new CatalogFormatException($"{where}: an action is missing its 'runbook'.");
            }
            if (string.IsNullOrWhiteSpace(a.Key))
            {
                throw new CatalogFormatException($"{where}: action '{a.Runbook}' is missing its required 'key'.");
            }

            var inputs = ParseInputs(a.Inputs);
            var policy = Enum.Parse<ActionPolicy>(a.Policy ?? "MANUAL_REQUIRED", ignoreCase: true);
            if (policy == ActionPolicy.AUTO && inputs.Any(i => i.Kind == RuleInputKind.Prompt))
            {
                throw new CatalogFormatException(
                    $"{where}: action '{a.Runbook}' is AUTO but declares prompt inputs — AUTO runs unattended with nobody to answer them.");
            }

            var executorKind = ParseExecutor(a.Executor, where);
            DockerExecutorConfig? dockerConfig = null;
            if (executorKind == ExecutorKind.Docker)
            {
                dockerConfig = ParseDocker(a.Docker, $"{where}, action '{a.Key}'");
            }
            else if (a.Docker is not null)
            {
                throw new CatalogFormatException(
                    $"{where}: action '{a.Key}' declares a 'docker' block but 'executor' is not 'docker'.");
            }

            actions.Add(new ActionTemplate
            {
                Key = a.Key!.Trim(),
                Runbook = a.Runbook!,
                Label = a.Label ?? a.Runbook!,
                Policy = policy,
                ExecutorKind = executorKind,
                Docker = dockerConfig,
                Inputs = inputs,
                DependsOn = (a.DependsOn ?? new List<string>()).Select(d => d.Trim()).ToList()
            });
        }

        return new SignalRule
        {
            Trigger = trigger,
            WhenJson = dto.When is null ? null : YamlFlattener.ToJsonValue(dto.When),
            ItemKey = string.IsNullOrWhiteSpace(dto.ItemKey) ? null : dto.ItemKey.Trim(),
            Actions = actions
        };
    }

    /// <summary>
    /// An identity's SUCCEEDED history is keyed by (signal, item key, action key); the
    /// same runbook or action key appearing under two different non-STATE triggers of one
    /// signal would merge those histories and corrupt satisfaction checks, so the catalog
    /// rejects it outright. STATE rules are exempt by design: a scalar signal's "when:
    /// true"/"when: false" branches intentionally reuse the same key for the same
    /// identity in opposite directions — only one branch ever matches at a time, and a
    /// direction flip is exactly what should re-open that identity as unsatisfied. A
    /// duplicate key within the very same rule is always rejected outright.
    /// </summary>
    private static void ValidateSignalInvariants(string capabilityCode, SignalDefinition signal, string? sourceFile = null)
    {
        foreach (var rule in signal.Rules)
        {
            var keysInRule = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in rule.Actions)
            {
                if (!keysInRule.Add(action.Key))
                {
                    throw new CatalogFormatException(
                        $"{Location(sourceFile, capabilityCode)}, signal '{signal.Path}': action key '{action.Key}' is used more than once in the same rule.");
                }
            }
        }

        var triggersByRunbook = new Dictionary<string, SignalTrigger>(StringComparer.Ordinal);
        var triggersByKey = new Dictionary<string, SignalTrigger>(StringComparer.Ordinal);
        foreach (var rule in signal.Rules.Where(r => r.Trigger != SignalTrigger.STATE))
        {
            foreach (var action in rule.Actions)
            {
                if (triggersByRunbook.TryGetValue(action.Runbook, out var existingRunbookTrigger) && existingRunbookTrigger != rule.Trigger)
                {
                    throw new CatalogFormatException(
                        $"{Location(sourceFile, capabilityCode)}, signal '{signal.Path}': runbook '{action.Runbook}' appears under both " +
                        $"'{existingRunbookTrigger}' and '{rule.Trigger}' triggers — use a distinct runbook per trigger.");
                }
                triggersByRunbook[action.Runbook] = rule.Trigger;

                if (triggersByKey.TryGetValue(action.Key, out var existingKeyTrigger) && existingKeyTrigger != rule.Trigger)
                {
                    throw new CatalogFormatException(
                        $"{Location(sourceFile, capabilityCode)}, signal '{signal.Path}': action key '{action.Key}' appears under both " +
                        $"'{existingKeyTrigger}' and '{rule.Trigger}' triggers — use a distinct key per trigger.");
                }
                triggersByKey[action.Key] = rule.Trigger;
            }
        }
    }

    private static SignalKind ParseKind(string? kind, string? sourceFile, string capabilityCode, string path)
        => kind?.Trim().ToLowerInvariant() switch
        {
            null or "" or "scalar" => SignalKind.Scalar,
            "keyed_collection" => SignalKind.KeyedCollection,
            "scalar_list" => SignalKind.ScalarList,
            _ => throw new CatalogFormatException(
                $"{Location(sourceFile, capabilityCode)}, signal '{path}': unknown kind '{kind}' (expected scalar, keyed_collection, or scalar_list).")
        };

    private static SignalTrigger ParseTrigger(string? on, string where)
        => on?.Trim().ToLowerInvariant() switch
        {
            null or "" or "state" => SignalTrigger.STATE,
            "add" => SignalTrigger.ADD,
            "delete" => SignalTrigger.DELETE,
            "modify" => SignalTrigger.MODIFY,
            _ => throw new CatalogFormatException($"{where}: unknown trigger 'on: {on}' (expected add, delete, or modify).")
        };

    /// <summary>
    /// Exactly one of <c>image</c> (pulled as-is) or <c>build</c> (a folder of the
    /// inventory, built and cached by the executor). <c>entrypoint</c>/<c>command</c> are
    /// optional with either — omitted, the image's own ENTRYPOINT/CMD apply, like a plain
    /// <c>docker run</c>. Build paths are syntactically confined here (relative, no
    /// <c>..</c>); the catalog provider resolves them against the kind folder and rejects
    /// anything that still escapes it.
    /// </summary>
    private static DockerExecutorConfig ParseDocker(DockerDto? dto, string where)
    {
        if (dto is null)
        {
            throw new CatalogFormatException($"{where}: declares 'executor: docker' but has no 'docker' block.");
        }

        var hasImage = !string.IsNullOrWhiteSpace(dto.Image);
        if (hasImage == (dto.Build is not null))
        {
            throw new CatalogFormatException(
                $"{where}: the 'docker' block needs exactly one of 'image' (a ready-made image) or 'build' (a playbook folder).");
        }

        DockerBuildConfig? build = null;
        if (dto.Build is not null)
        {
            var context = NormalizeRelativePath(dto.Build.Context, "docker.build.context", where)
                ?? throw new CatalogFormatException($"{where}: 'docker.build' is missing its 'context' folder.");
            var dockerfile = NormalizeRelativePath(dto.Build.Dockerfile, "docker.build.dockerfile", where);
            var args = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in dto.Build.Args ?? new Dictionary<string, string?>())
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new CatalogFormatException($"{where}: 'docker.build.args' has an empty argument name.");
                }
                args[name.Trim()] = value ?? string.Empty;
            }

            build = new DockerBuildConfig(
                context,
                dockerfile,
                string.IsNullOrWhiteSpace(dto.Build.Target) ? null : dto.Build.Target.Trim(),
                args.Count == 0 ? null : args);
        }

        var mounts = new List<DockerMount>();
        foreach (var raw in dto.Mounts ?? new List<string>())
        {
            mounts.Add(ParseMount(raw, where));
        }

        SortedDictionary<string, string>? env = null;
        foreach (var (name, value) in dto.Env ?? new Dictionary<string, string?>())
        {
            if (!EnvNamePattern.IsMatch(name ?? string.Empty) || name!.StartsWith("LODGE_", StringComparison.Ordinal))
            {
                throw new CatalogFormatException(
                    $"{where}: 'docker.env' name '{name}' must be a plain variable name and may not start with LODGE_ (reserved for inputs).");
            }
            env ??= new SortedDictionary<string, string>(StringComparer.Ordinal);
            env[name] = value ?? string.Empty;
        }

        return new DockerExecutorConfig(
            hasImage ? dto.Image!.Trim() : null,
            dto.Command ?? new List<string>(),
            dto.Entrypoint is { Count: > 0 } ? dto.Entrypoint : null,
            build,
            mounts.Count == 0 ? null : mounts,
            env);
    }

    private static readonly System.Text.RegularExpressions.Regex EnvNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$");
    private static readonly System.Text.RegularExpressions.Regex MountAliasPattern = new("^[A-Za-z0-9_-]+$");

    /// <summary><c>alias:/absolute/target</c> with an optional <c>:ro</c>/<c>:rw</c> suffix.</summary>
    private static DockerMount ParseMount(string? raw, string where)
    {
        var parts = (raw ?? string.Empty).Trim().Split(':');
        var readOnly = false;
        if (parts.Length == 3 && parts[2] is "ro" or "rw")
        {
            readOnly = parts[2] == "ro";
        }
        else if (parts.Length != 2)
        {
            parts = Array.Empty<string>();
        }

        if (parts.Length < 2 || !MountAliasPattern.IsMatch(parts[0]) || !parts[1].StartsWith('/'))
        {
            throw new CatalogFormatException(
                $"{where}: mount '{raw}' must be 'alias:/container/path[:ro]', where alias names an entry of the server's DockerExecutor:Mounts.");
        }

        return new DockerMount(parts[0], parts[1], readOnly);
    }

    private static string? NormalizeRelativePath(string? raw, string field, string where)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var path = raw.Trim().Replace('\\', '/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(seg => seg != ".").ToList();
        if (path.StartsWith('/') || Path.IsPathRooted(path) || segments.Contains(".."))
        {
            throw new CatalogFormatException(
                $"{where}: '{field}: {raw}' must be a relative path inside the kind's inventory folder (no leading '/', no '..').");
        }
        if (segments.Count == 0)
        {
            throw new CatalogFormatException($"{where}: '{field}: {raw}' does not name a folder or file.");
        }

        return string.Join('/', segments);
    }

    private static ExecutorKind ParseExecutor(string? executor, string where)
        => executor?.Trim().ToLowerInvariant() switch
        {
            null or "" or "shell" => ExecutorKind.Shell,
            "webhook" => ExecutorKind.Webhook,
            "docker" => ExecutorKind.Docker,
            "octopus" or "kubernetes" => throw new CatalogFormatException(
                $"{where}: executor '{executor}' is reserved for a future release and not yet supported."),
            _ => throw new CatalogFormatException($"{where}: unknown executor '{executor}' (expected shell, webhook, or docker).")
        };

    private static List<RuleInput> ParseInputs(Dictionary<string, InputDto>? inputs)
    {
        var result = new List<RuleInput>();
        if (inputs is null)
        {
            return result;
        }

        foreach (var (name, spec) in inputs)
        {
            if (spec is null)
            {
                continue;
            }

            if (spec.From is not null)
            {
                result.Add(new RuleInput { Name = name, Kind = RuleInputKind.From, Value = spec.From });
            }
            else if (spec.Const is not null)
            {
                result.Add(new RuleInput { Name = name, Kind = RuleInputKind.Const, Value = spec.Const });
            }
            else if (spec.Prompt is not null)
            {
                result.Add(new RuleInput { Name = name, Kind = RuleInputKind.Prompt, Value = spec.Prompt, Required = spec.Required });
            }
            else if (spec.Secret is not null)
            {
                result.Add(new RuleInput { Name = name, Kind = RuleInputKind.Secret, Value = spec.Secret });
            }
        }

        return result;
    }

    private static string Location(string? sourceFile, string capabilityCode)
        => sourceFile is null ? $"capability '{capabilityCode}'" : $"{sourceFile} (capability '{capabilityCode}')";

    private sealed class CapabilityDto
    {
        public string? Capability { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public List<SignalDto>? Signals { get; set; }
    }

    private sealed class SignalDto
    {
        public string? Path { get; set; }
        public string? Kind { get; set; }
        public List<string>? Exclude { get; set; }
        public string? Files { get; set; }
        public List<RuleDto>? Rules { get; set; }
    }

    private sealed class RuleDto
    {
        public object? When { get; set; }
        public string? On { get; set; }
        public string? ItemKey { get; set; }
        public List<ActionDto>? Actions { get; set; }
    }

    private sealed class ActionDto
    {
        public string? Key { get; set; }
        public string? Runbook { get; set; }
        public string? Label { get; set; }
        public string? Policy { get; set; }
        public string? Executor { get; set; }
        public DockerDto? Docker { get; set; }
        public Dictionary<string, InputDto>? Inputs { get; set; }
        public List<string>? DependsOn { get; set; }
    }

    private sealed class DockerDto
    {
        public string? Image { get; set; }
        public DockerBuildDto? Build { get; set; }
        public List<string>? Entrypoint { get; set; }
        public List<string>? Command { get; set; }
        public List<string>? Mounts { get; set; }
        public Dictionary<string, string?>? Env { get; set; }
    }

    private sealed class DockerBuildDto
    {
        public string? Context { get; set; }
        public string? Dockerfile { get; set; }
        public string? Target { get; set; }
        public Dictionary<string, string?>? Args { get; set; }
    }

    private sealed class InputDto
    {
        public string? From { get; set; }
        public string? Const { get; set; }
        public string? Prompt { get; set; }
        public string? Secret { get; set; }
        public bool Required { get; set; }
    }
}
