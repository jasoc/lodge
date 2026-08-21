using System.Text.Json;
using Lodge.Core.Catalog;
using Lodge.Core.Diff;
using Lodge.Core.Domain.Enums;

namespace Lodge.Core.Reconciliation;

/// <summary>
/// The pure state-diff reconciliation engine. Every cycle it recomputes, from scratch,
/// what the current desired state (instance YAML) requires according to the live catalog,
/// and compares that against the confirmed-state history (most recent SUCCEEDED action
/// per identity). Nothing is ever accumulated: "required now" is a function of
/// (desired state, catalog, history), never a sum of past events.
/// </summary>
public static class Reconciler
{
    public static ReconciliationResult Reconcile(ReconciliationInput input)
    {
        var errors = new List<string>();

        // --- Confirmed-state history projections -------------------------------------
        // lastSuccess: most recent SUCCEEDED per identity (satisfaction of STATE/ADD).
        // lastConfirmed: most recent SUCCEEDED per (signal, item key) across runbooks —
        // its trigger says whether the item is confirmed present or absent.
        var lastSuccess = new Dictionary<ActionIdentity, SucceededRecord>();
        var lastConfirmed = new Dictionary<(string SignalPath, string ItemKey), SucceededRecord>();
        var lastDelete = new Dictionary<(string SignalPath, string ItemKey), DateTimeOffset>();

        foreach (var rec in input.History)
        {
            if (!lastSuccess.TryGetValue(rec.Identity, out var existing) || rec.CompletedAt > existing.CompletedAt)
            {
                lastSuccess[rec.Identity] = rec;
            }

            if (rec.Identity.ItemKey is not null)
            {
                var itemId = (rec.Identity.SignalPath, rec.Identity.ItemKey);
                if (!lastConfirmed.TryGetValue(itemId, out var confirmed) || rec.CompletedAt > confirmed.CompletedAt)
                {
                    lastConfirmed[itemId] = rec;
                }

                if (rec.Trigger == SignalTrigger.DELETE &&
                    (!lastDelete.TryGetValue(itemId, out var deletedAt) || rec.CompletedAt > deletedAt))
                {
                    lastDelete[itemId] = rec.CompletedAt;
                }
            }
        }

        // --- Evaluate the catalog against the current desired state ------------------
        var evaluation = new Evaluation(input, lastSuccess, lastConfirmed, lastDelete, errors);
        var capabilities = input.Catalog.Capabilities
            .Select(evaluation.EvaluateCapability)
            .ToList();

        // --- Match live rows against the current need-live set -----------------------
        var toSupersede = new List<Guid>();
        var toCreate = new List<RequiredAction>();
        var toAdopt = new List<RequiredAction>();
        var toAutoStart = new List<RequiredAction>();

        var liveByIdentity = new Dictionary<ActionIdentity, LiveActionRow>();
        foreach (var live in input.LiveRows)
        {
            // The DB enforces one live row per identity; if legacy duplicates exist,
            // keep one arbitrarily and supersede the rest.
            if (!liveByIdentity.TryAdd(live.Identity, live))
            {
                toSupersede.Add(live.Id);
            }
        }

        foreach (var required in evaluation.NeedLive.Values)
        {
            if (liveByIdentity.Remove(required.Identity, out var live))
            {
                var snapshotMatches = string.Equals(live.DesiredValueJson, required.DesiredValueJson, StringComparison.Ordinal);
                if (live.Status == ActionStatus.RUNNING || snapshotMatches)
                {
                    // A RUNNING row is never superseded mid-flight even if its snapshot
                    // went stale; the next cycle reconciles whatever it lands as.
                    required.LiveRowId = live.Id;
                    required.LiveStatus = live.Status;
                    if (live.Status == ActionStatus.QUEUED && IsAutoStartable(required))
                    {
                        toAutoStart.Add(required);
                    }
                    continue;
                }

                toSupersede.Add(live.Id);
            }

            if (required.AdoptOnFaith && required.Policy != ActionPolicy.OPTIONAL)
            {
                toAdopt.Add(required);
                continue;
            }

            toCreate.Add(required);
            if (IsAutoStartable(required))
            {
                toAutoStart.Add(required);
            }
        }

        // Whatever live rows remain are no longer called for by the current state.
        foreach (var live in liveByIdentity.Values)
        {
            if (live.Status != ActionStatus.RUNNING)
            {
                toSupersede.Add(live.Id);
            }
        }

        var views = capabilities.Select(c => c.ToView()).ToList();

        return new ReconciliationResult(views, toCreate, toAdopt, toSupersede, toAutoStart, errors);
    }

    private static bool IsAutoStartable(RequiredAction required)
        => required.Policy == ActionPolicy.AUTO && !required.Satisfied && required.PendingPrompts.Count == 0;

    // ---------------------------------------------------------------------------------

    private sealed class CapabilityEvaluation
    {
        public required CapabilityDefinition Definition { get; init; }
        public required bool Visible { get; init; }
        public required IReadOnlyList<SignalStatusView> Signals { get; init; }
        public required bool AllOff { get; init; }

        public CapabilityStatusView ToView()
        {
            var actions = Signals.SelectMany(s => s.Actions).ToList();
            var gateDrift = actions.Where(a => a.IsDrift && a.Trigger != SignalTrigger.MODIFY).ToList();
            var hasModifyDrift = actions.Any(a => a.IsDrift && a.Trigger == SignalTrigger.MODIFY);

            var state = !Visible ? CapabilityState.NotVisible
                : gateDrift.Any(a => a.LiveStatus != ActionStatus.FAILED) ? CapabilityState.Pending
                : gateDrift.Count > 0 ? CapabilityState.Drifted
                : AllOff ? CapabilityState.Disabled
                : CapabilityState.Active;

            return new CapabilityStatusView(
                Definition.Code, Definition.Title, Definition.Description, Definition.SourceFile,
                state, hasModifyDrift, Signals);
        }
    }

    /// <summary>Per-instance evaluation pass: turns catalog + desired state + history into required actions.</summary>
    private sealed class Evaluation
    {
        private readonly record struct PastHistoryEntry(DateTimeOffset? DateUtc, ActionAddress Address, string? Description);

        private readonly ReconciliationInput _input;
        private readonly Dictionary<ActionIdentity, SucceededRecord> _lastSuccess;
        private readonly Dictionary<(string, string), SucceededRecord> _lastConfirmed;
        private readonly Dictionary<(string, string), DateTimeOffset> _lastDelete;
        private readonly List<string> _errors;
        private readonly List<PastHistoryEntry> _pastHistory;

        /// <summary>Identities that must have a live row after this cycle (drift + available OPTIONAL).</summary>
        public Dictionary<ActionIdentity, RequiredAction> NeedLive { get; } = new();

        public Evaluation(
            ReconciliationInput input,
            Dictionary<ActionIdentity, SucceededRecord> lastSuccess,
            Dictionary<(string, string), SucceededRecord> lastConfirmed,
            Dictionary<(string, string), DateTimeOffset> lastDelete,
            List<string> errors)
        {
            _input = input;
            _lastSuccess = lastSuccess;
            _lastConfirmed = lastConfirmed;
            _lastDelete = lastDelete;
            _errors = errors;
            _pastHistory = ParsePastHistory();
        }

        /// <summary>
        /// Parses the instance's declared <c>past_history</c> section: explicit facts about
        /// what already happened outside Lodge's governance, addressed with the same
        /// syntax as <c>depends_on</c> plus item-scoped/universal wildcards. Malformed
        /// entries are reported as non-fatal validation errors (the entry is skipped, the
        /// rest of the cycle proceeds) — this is instance data, not a static authoring bug.
        /// </summary>
        private List<PastHistoryEntry> ParsePastHistory()
        {
            var result = new List<PastHistoryEntry>();
            var (found, node) = ResolvePath(_input.DesiredRoot, "past_history");
            if (!found || node is null)
            {
                return result;
            }

            if (node is not IList<object> list)
            {
                _errors.Add($"{_input.InstanceCode}: 'past_history' must be a list of entries.");
                return result;
            }

            foreach (var element in list)
            {
                if (element is not IDictionary<object, object> dict)
                {
                    _errors.Add($"{_input.InstanceCode}: each 'past_history' entry must be a mapping with date_utc/action/description.");
                    continue;
                }

                var actionRaw = GetStringField(dict, "action");
                var description = GetStringField(dict, "description");
                var dateRaw = GetStringField(dict, "date_utc");

                if (string.IsNullOrWhiteSpace(actionRaw))
                {
                    _errors.Add($"{_input.InstanceCode}: a 'past_history' entry is missing 'action'.");
                    continue;
                }

                if (!ActionAddressParser.TryParse(actionRaw, _input.Catalog, out var address, out var parseError))
                {
                    _errors.Add($"{_input.InstanceCode}: past_history entry '{actionRaw}' — {parseError}");
                    continue;
                }

                if (address.IsUniversal && string.IsNullOrWhiteSpace(description))
                {
                    _errors.Add($"{_input.InstanceCode}: past_history universal marker (action: \"*\") requires a non-empty 'description'.");
                    continue;
                }

                DateTimeOffset? dateUtc = null;
                if (!string.IsNullOrWhiteSpace(dateRaw))
                {
                    if (!DateTimeOffset.TryParse(
                        dateRaw, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedDate))
                    {
                        _errors.Add($"{_input.InstanceCode}: past_history entry '{actionRaw}' has an unparseable date_utc '{dateRaw}'.");
                        continue;
                    }
                    dateUtc = parsedDate;
                }

                result.Add(new PastHistoryEntry(dateUtc, address, description));
            }

            return result;
        }

        private static string? GetStringField(IDictionary<object, object> dict, string key)
        {
            var match = dict.Keys.FirstOrDefault(k =>
                string.Equals(Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture), key, StringComparison.Ordinal));
            return match is null ? null : Convert.ToString(dict[match], System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Whether a past_history entry explicitly covers this identity: an exact address,
        /// an item-scoped action wildcard (any action key of that item — deliberately
        /// forever-open for as long as the entry stays in YAML, see the two-commit removal
        /// discipline documented on the instance fixtures), or the universal instance marker.
        /// </summary>
        private bool CoveredByPastHistory(ActionIdentity identity)
        {
            foreach (var entry in _pastHistory)
            {
                if (entry.Address.IsUniversal)
                {
                    return true;
                }
                if (!string.Equals(entry.Address.SignalPath, identity.SignalPath, StringComparison.Ordinal))
                {
                    continue;
                }
                if (entry.Address.IsActionWildcard)
                {
                    if (string.Equals(entry.Address.ItemKey, identity.ItemKey, StringComparison.Ordinal))
                    {
                        return true;
                    }
                    continue;
                }
                if (string.Equals(entry.Address.ItemKey, identity.ItemKey, StringComparison.Ordinal) &&
                    string.Equals(entry.Address.ActionKey, identity.ActionKey, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// An action with unmet depends_on targets is fully absent this cycle (not shown
        /// disabled) — it reappears on its own once every target has a SucceededRecord.
        /// A collection-signal target with no item key of its own resolves against the
        /// current item when it shares the depending action's signal; otherwise it
        /// addresses the target's scalar identity.
        /// </summary>
        private bool DependenciesSatisfied(ActionTemplate template, ActionIdentity identity)
        {
            foreach (var raw in template.DependsOn)
            {
                if (!ActionAddressParser.TryParse(raw, _input.Catalog, out var address, out _))
                {
                    continue; // validated at catalog-load time; cannot fail for a loaded catalog
                }

                var targetItemKey = string.Equals(address.SignalPath, identity.SignalPath, StringComparison.Ordinal)
                    ? identity.ItemKey
                    : null;
                var targetIdentity = new ActionIdentity(address.SignalPath!, targetItemKey, address.ActionKey);
                if (!_lastSuccess.ContainsKey(targetIdentity))
                {
                    return false;
                }
            }
            return true;
        }

        public CapabilityEvaluation EvaluateCapability(CapabilityDefinition capability)
        {
            // Visibility gate first: a capability applies to a instance only when every
            // signal path exists in its YAML (false/empty containers count as present).
            var resolved = capability.Signals
                .Select(s => (Signal: s, Value: ResolvePath(_input.DesiredRoot, s.Path)))
                .ToList();
            var visible = resolved.All(r => r.Value.Found);

            var signalViews = new List<SignalStatusView>();
            var allOff = true;

            foreach (var (signal, (found, node)) in resolved)
            {
                if (!visible)
                {
                    signalViews.Add(new SignalStatusView(signal.Path, signal.Kind, found,
                        found ? YamlFlattener.ToCanonicalJson(node) : null, Array.Empty<RequiredAction>(), null));
                    continue;
                }

                var view = signal.Kind == SignalKind.Scalar
                    ? EvaluateScalarSignal(capability, signal, node)
                    : EvaluateCollectionSignal(capability, signal, node);
                signalViews.Add(view);
                allOff &= IsOff(signal.Kind, view.CurrentValueJson);
            }

            return new CapabilityEvaluation
            {
                Definition = capability,
                Visible = visible,
                Signals = signalViews,
                AllOff = visible && capability.Signals.Count > 0 && allOff
            };
        }

        private SignalStatusView EvaluateScalarSignal(CapabilityDefinition capability, SignalDefinition signal, object? node)
        {
            var currentJson = YamlFlattener.ToCanonicalJson(node);
            var actions = new List<RequiredAction>();

            foreach (var rule in signal.Rules)
            {
                if (rule.Trigger != SignalTrigger.STATE ||
                    (rule.WhenJson is not null && !string.Equals(rule.WhenJson, currentJson, StringComparison.Ordinal)))
                {
                    continue;
                }

                var context = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["instance"] = _input.InstanceCode,
                    ["kind"] = _input.KindCode,
                    ["path"] = signal.Path,
                    ["value"] = Unwrap(currentJson)
                };

                foreach (var template in rule.Actions)
                {
                    var identity = new ActionIdentity(signal.Path, null, template.Key);
                    var satisfied = _lastSuccess.TryGetValue(identity, out var success) &&
                        string.Equals(success.DesiredValueJson, currentJson, StringComparison.Ordinal);

                    Emit(actions, capability, identity, SignalTrigger.STATE, template, currentJson, context, satisfied);
                }
            }

            return new SignalStatusView(signal.Path, signal.Kind, true, currentJson, actions, null);
        }

        private SignalStatusView EvaluateCollectionSignal(CapabilityDefinition capability, SignalDefinition signal, object? node)
        {
            var currentJson = YamlFlattener.ToCanonicalJson(node);
            if (!TryGetItems(signal, node, out var desiredItems, out var validationError))
            {
                _errors.Add($"{_input.InstanceCode}: signal '{signal.Path}' — {validationError}");
                return new SignalStatusView(signal.Path, signal.Kind, true, currentJson, Array.Empty<RequiredAction>(), validationError);
            }

            var actions = new List<RequiredAction>();

            // Present items: ADD (presence not confirmed) and MODIFY (body changed).
            foreach (var (key, bodyJson) in desiredItems)
            {
                var itemId = (signal.Path, key);
                var confirmed = _lastConfirmed.TryGetValue(itemId, out var c) && c.Trigger != SignalTrigger.DELETE ? c : null;

                foreach (var rule in MatchingRules(signal, SignalTrigger.ADD, key))
                {
                    var context = ItemContext(signal.Path, key, bodyJson);
                    foreach (var template in rule.Actions)
                    {
                        var identity = new ActionIdentity(signal.Path, key, template.Key);
                        var neverSucceeded = !_lastSuccess.TryGetValue(identity, out var success);
                        // Confirmed-present for this identity unless a later delete undid it.
                        var satisfied = !neverSucceeded &&
                            !(_lastDelete.TryGetValue(itemId, out var deletedAt) && deletedAt > success!.CompletedAt);

                        Emit(actions, capability, identity, SignalTrigger.ADD, template, bodyJson, context, satisfied);
                    }
                }

                if (confirmed is not null && !string.Equals(confirmed.DesiredValueJson, bodyJson, StringComparison.Ordinal))
                {
                    foreach (var rule in MatchingRules(signal, SignalTrigger.MODIFY, key))
                    {
                        var context = ItemContext(signal.Path, key, bodyJson);
                        foreach (var template in rule.Actions)
                        {
                            var identity = new ActionIdentity(signal.Path, key, template.Key);
                            Emit(actions, capability, identity, SignalTrigger.MODIFY, template, bodyJson, context, satisfied: false);
                        }
                    }
                }
            }

            // Absent-but-known items: DELETE while the item is still confirmed present.
            var knownKeys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var ((path, key), record) in _lastConfirmed)
            {
                if (string.Equals(path, signal.Path, StringComparison.Ordinal) && record.Trigger != SignalTrigger.DELETE)
                {
                    knownKeys.Add(key);
                }
            }

            foreach (var key in knownKeys.Where(k => !desiredItems.ContainsKey(k)))
            {
                var itemId = (signal.Path, key);
                var stillConfirmedPresent = _lastConfirmed.TryGetValue(itemId, out var c) && c.Trigger != SignalTrigger.DELETE;
                if (!stillConfirmedPresent)
                {
                    continue; // never confirmed, or already confirmed deleted: nothing to undo
                }

                foreach (var rule in MatchingRules(signal, SignalTrigger.DELETE, key))
                {
                    var context = ItemContext(signal.Path, key, c!.DesiredValueJson);
                    foreach (var template in rule.Actions)
                    {
                        var identity = new ActionIdentity(signal.Path, key, template.Key);
                        Emit(actions, capability, identity, SignalTrigger.DELETE, template, "null", context, satisfied: false);
                    }
                }
            }

            return new SignalStatusView(signal.Path, signal.Kind, true, currentJson, actions, null);
        }

        /// <summary>
        /// Emits the action into the signal view and, when it needs a live row (any
        /// unsatisfied non-OPTIONAL drift, or any matched OPTIONAL, which stays
        /// re-invocable), into the need-live set. Skips emission entirely when a
        /// depends_on target is unsatisfied, and marks <see cref="RequiredAction.AdoptOnFaith"/>
        /// when the identity has never succeeded but is covered by past_history.
        /// </summary>
        private void Emit(
            List<RequiredAction> actions,
            CapabilityDefinition capability,
            ActionIdentity identity,
            SignalTrigger trigger,
            ActionTemplate template,
            string? desiredValueJson,
            IReadOnlyDictionary<string, string?> context,
            bool satisfied)
        {
            if (!DependenciesSatisfied(template, identity))
            {
                return;
            }

            var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
            var prompts = new List<PendingPrompt>();
            foreach (var inputDef in template.Inputs)
            {
                switch (inputDef.Kind)
                {
                    case RuleInputKind.From:
                        resolved[inputDef.Name] = inputDef.Value is null ? null : ResolveFrom(inputDef.Value, context);
                        break;
                    case RuleInputKind.Const:
                        resolved[inputDef.Name] = inputDef.Value;
                        break;
                    case RuleInputKind.Prompt:
                        prompts.Add(new PendingPrompt(inputDef.Name, inputDef.Value ?? inputDef.Name, inputDef.Required));
                        break;
                }
            }

            var neverSucceeded = !_lastSuccess.ContainsKey(identity);
            var adoptOnFaith = neverSucceeded && CoveredByPastHistory(identity);

            var required = new RequiredAction(
                identity, trigger, capability.Code, template.Label, template.Runbook, template.Policy,
                desiredValueJson, resolved, prompts, satisfied, adoptOnFaith)
            {
                SucceededActionId = satisfied && _lastSuccess.TryGetValue(identity, out var succeededRow) ? succeededRow.Id : null
            };

            if (NeedLive.ContainsKey(identity))
            {
                _errors.Add(
                    $"{_input.InstanceCode}: action key '{identity.ActionKey}' is required twice for signal " +
                    $"'{identity.SignalPath}'{(identity.ItemKey is null ? "" : $" item '{identity.ItemKey}'")} — " +
                    "check for overlapping rules; only the first match is kept.");
                return;
            }

            actions.Add(required);
            if (template.Policy == ActionPolicy.OPTIONAL || !satisfied)
            {
                NeedLive[identity] = required;
            }
        }

        private IEnumerable<SignalRule> MatchingRules(SignalDefinition signal, SignalTrigger trigger, string key)
            => signal.Rules.Where(r => r.Trigger == trigger &&
                (r.ItemKey is null || string.Equals(r.ItemKey, key, StringComparison.Ordinal)));

        private Dictionary<string, string?> ItemContext(string signalPath, string key, string? bodyJson) => new(StringComparer.Ordinal)
        {
            ["instance"] = _input.InstanceCode,
            ["kind"] = _input.KindCode,
            ["path"] = $"{signalPath}.{key}",
            ["key"] = key,
            ["item"] = Unwrap(bodyJson),
            ["value"] = Unwrap(bodyJson)
        };

        /// <summary>
        /// Extracts the stable-keyed items of a collection signal. Keyed collections must
        /// be maps (an object array is the classic mistake this engine refuses to guess
        /// about); scalar lists must contain only scalars, each being its own key.
        /// </summary>
        private static bool TryGetItems(
            SignalDefinition signal, object? node,
            out IReadOnlyDictionary<string, string?> items, out string? validationError)
        {
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            items = result;
            validationError = null;

            switch (node)
            {
                case null:
                    return true; // empty container: present, no items

                case IDictionary<object, object> dict when signal.Kind == SignalKind.KeyedCollection:
                    foreach (var kvp in dict)
                    {
                        var key = Convert.ToString(kvp.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                        result[key] = YamlFlattener.ToCanonicalJson(kvp.Value);
                    }
                    return true;

                case IList<object> list when signal.Kind == SignalKind.ScalarList:
                    foreach (var element in list)
                    {
                        if (element is IDictionary<object, object> or IList<object>)
                        {
                            validationError = "a scalar_list signal may only contain scalar values; " +
                                "objects need a keyed_collection signal with stable keys.";
                            result.Clear();
                            return false;
                        }
                        var key = Convert.ToString(element, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                        result[key] = YamlFlattener.ToJsonValue(element);
                    }
                    return true;

                case IList<object> when signal.Kind == SignalKind.KeyedCollection:
                    validationError = "expected a map of stable keys to item bodies, found an array — " +
                        "object arrays must become keyed maps (e.g. virtual_machines.vm-alpha-01: {...}).";
                    return false;

                default:
                    validationError = $"expected a {(signal.Kind == SignalKind.KeyedCollection ? "keyed map" : "list of scalars")}, " +
                        "found a scalar value.";
                    return false;
            }
        }

        private static bool IsOff(SignalKind kind, string? currentValueJson)
            => currentValueJson is null or "null" or "false" or "\"\"" or "{}" or "[]";
    }

    // ---------------------------------------------------------------------------------

    /// <summary>Walks a dotted path through parsed-YAML dictionaries. Found means the path exists, whatever its value.</summary>
    private static (bool Found, object? Node) ResolvePath(object? root, string path)
    {
        if (root is null || string.IsNullOrEmpty(path))
        {
            return (false, null);
        }

        object? current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current is not IDictionary<object, object> dict)
            {
                return (false, null);
            }

            var match = dict.Keys.FirstOrDefault(k =>
                string.Equals(Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture), segment, StringComparison.Ordinal));
            if (match is null)
            {
                return (false, null);
            }

            current = dict[match];
        }

        return (true, current);
    }

    /// <summary>
    /// Resolves a <c>from</c> input against the match context. <c>item.&lt;dotted.path&gt;</c>
    /// reaches into the item's canonical JSON body and pulls out one nested field;
    /// anything else is a flat context lookup (instance, kind, path, key, item, value).
    /// </summary>
    private static string? ResolveFrom(string fromKey, IReadOnlyDictionary<string, string?> context)
    {
        const string itemPrefix = "item.";
        if (fromKey.StartsWith(itemPrefix, StringComparison.Ordinal) &&
            context.TryGetValue("item", out var itemJson) && itemJson is not null)
        {
            return ExtractJsonPath(itemJson, fromKey[itemPrefix.Length..]);
        }

        return context.TryGetValue(fromKey, out var value) ? value : null;
    }

    private static string? ExtractJsonPath(string json, string dottedPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement;
            foreach (var segment in dottedPath.Split('.'))
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                {
                    return null;
                }
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Null => null,
                _ => element.GetRawText()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Strips surrounding quotes from a canonical JSON scalar for use as a plain input value.</summary>
    private static string? Unwrap(string? canonicalJson)
    {
        if (canonicalJson is null)
        {
            return null;
        }
        if (canonicalJson.Length >= 2 && canonicalJson[0] == '"' && canonicalJson[^1] == '"')
        {
            return canonicalJson[1..^1];
        }
        return canonicalJson;
    }
}
