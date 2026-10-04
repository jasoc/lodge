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
        // lastConfirmed: most recent SUCCEEDED per (signal, item key) across action keys —
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
        // Views have nothing to reconcile: they're rendered straight from the inventory.
        var capabilities = input.Catalog.Capabilities
            .Where(c => !c.IsView)
            .Select(evaluation.EvaluateCapability)
            .ToList();

        // --- Match live rows against the current need-live set -----------------------
        var toSupersede = new List<Guid>();
        var toCreate = new List<RequiredAction>();
        var toAdopt = new List<RequiredAction>();
        var toAutoStart = new List<RequiredAction>();
        var toBlock = new List<Guid>();
        var toUnblock = new List<Guid>();

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
                // Waited BLOCKED for a dependency and now covered by past_history: adopt
                // it as already done, exactly like one emitted unblocked would have been.
                if (live.Status == ActionStatus.BLOCKED && !required.Blocked &&
                    required.AdoptOnFaith && required.Policy != ActionPolicy.OPTIONAL)
                {
                    toSupersede.Add(live.Id);
                    toAdopt.Add(required);
                    continue;
                }

                // The executor config is part of the snapshot: a row approved against one
                // image/playbook fingerprint must never run a different one, so a changed
                // playbook folder re-queues the action for a fresh confirmation.
                // Same for who may approve it: a row emitted for anyone must not stay
                // confirmable by anyone once the catalog restricts it to a group. And
                // for what it runs with and under which policy: a row must never run
                // inputs (from/const, secret refs, prompts) or show a policy the
                // catalog no longer declares.
                var snapshotMatches = SnapshotMatches(live, required);
                if (live.Status == ActionStatus.RUNNING || snapshotMatches)
                {
                    // A RUNNING row is never superseded mid-flight even if its snapshot
                    // went stale; the next cycle reconciles whatever it lands as.
                    required.LiveRowId = live.Id;
                    required.LiveStatus = live.Status;

                    // A row that hasn't run yet follows its dependencies both ways;
                    // RUNNING/FAILED rows already ran and are left as they are.
                    if (live.Status == ActionStatus.QUEUED && required.Blocked)
                    {
                        toBlock.Add(live.Id);
                        required.LiveStatus = ActionStatus.BLOCKED;
                    }
                    else if (live.Status == ActionStatus.BLOCKED && !required.Blocked)
                    {
                        toUnblock.Add(live.Id);
                        required.LiveStatus = ActionStatus.QUEUED;
                    }

                    if (required.LiveStatus == ActionStatus.QUEUED && IsAutoStartable(required))
                    {
                        toAutoStart.Add(required);
                    }
                    continue;
                }

                toSupersede.Add(live.Id);
            }

            if (required.AdoptOnFaith && required.Policy != ActionPolicy.OPTIONAL && !required.Blocked)
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

        return new ReconciliationResult(views, toCreate, toAdopt, toSupersede, toAutoStart, errors)
        {
            ToBlock = toBlock,
            ToUnblock = toUnblock
        };
    }

    private static bool SnapshotMatches(LiveActionRow live, RequiredAction required)
        => string.Equals(live.DesiredValueJson, required.DesiredValueJson, StringComparison.Ordinal) &&
           string.Equals(live.ExecutorConfigJson, required.ExecutorConfigJson, StringComparison.Ordinal) &&
           string.Equals(live.Requires, required.Requires, StringComparison.Ordinal) &&
           live.Policy == required.Policy &&
           string.Equals(live.ResolvedInputsJson, required.ResolvedInputsJson, StringComparison.Ordinal) &&
           string.Equals(live.SecretInputsJson, required.SecretInputsJson, StringComparison.Ordinal) &&
           string.Equals(live.PendingPromptsJson, required.PendingPromptsJson, StringComparison.Ordinal);

    private static bool IsAutoStartable(RequiredAction required)
        => required.Policy == ActionPolicy.AUTO && !required.Satisfied && !required.Blocked && required.PendingPrompts.Count == 0;

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
        /// The action's depends_on targets as identities, and the ones not satisfied yet
        /// (no SucceededRecord of the current incarnation). An action with unsatisfied
        /// targets is still emitted — BLOCKED — so the whole chain a change sets off is
        /// visible in the cycle that detects it. A collection-signal target with no item
        /// key of its own resolves against the current item when it shares the depending
        /// action's signal, against the parent item for a nested signal, and otherwise
        /// addresses the target's scalar identity.
        /// </summary>
        private (List<ActionIdentity> DependsOn, List<ActionIdentity> BlockedBy) ResolveDependencies(
            ActionTemplate template, ActionIdentity identity)
        {
            var dependsOn = new List<ActionIdentity>();
            var blockedBy = new List<ActionIdentity>();
            foreach (var raw in template.DependsOn)
            {
                if (!ActionAddressParser.TryParse(raw, _input.Catalog, out var address, out _))
                {
                    continue; // validated at catalog-load time; cannot fail for a loaded catalog
                }

                var targetItemKey = TargetItemKey(address.SignalPath!, identity);
                var targetIdentity = new ActionIdentity(address.SignalPath!, targetItemKey, address.ActionKey);
                dependsOn.Add(targetIdentity);

                // A success from before the item was last deleted belongs to a previous
                // incarnation (a destroyed-then-recreated VM): it doesn't count.
                var satisfied = _lastSuccess.TryGetValue(targetIdentity, out var success) &&
                    !(targetItemKey is not null &&
                      _lastDelete.TryGetValue((address.SignalPath!, targetItemKey), out var deletedAt) &&
                      deletedAt > success.CompletedAt);
                if (!satisfied)
                {
                    blockedBy.Add(targetIdentity);
                }
            }
            return (dependsOn, blockedBy);
        }

        /// <summary>
        /// Which item a dependency on <paramref name="targetSignalPath"/> means for the
        /// depending action: its own item on the same signal; its parent item when the
        /// depending signal is nested under the target (a container waiting on its VM);
        /// otherwise the target's scalar identity.
        /// </summary>
        private IReadOnlyCollection<SignalDefinition> AllSignals => _allSignals ??= _input.Catalog.Capabilities.SelectMany(c => c.Signals).ToList();
        private IReadOnlyCollection<SignalDefinition>? _allSignals;

        private string? TargetItemKey(string targetSignalPath, ActionIdentity identity)
        {
            if (string.Equals(targetSignalPath, identity.SignalPath, StringComparison.Ordinal))
            {
                return identity.ItemKey;
            }

            var depending = AllSignals.FirstOrDefault(s => string.Equals(s.Path, identity.SignalPath, StringComparison.Ordinal));
            if (depending is { IsNested: true } &&
                string.Equals(depending.ParentPath, targetSignalPath, StringComparison.Ordinal) &&
                identity.ItemKey is { } nestedKey)
            {
                return SplitNestedKey(nestedKey).Parent;
            }

            return null;
        }

        private static (string Parent, string Child) SplitNestedKey(string key)
        {
            var at = key.IndexOf(SignalDefinition.NestedItemKeySeparator);
            return at < 0 ? (key, key) : (key[..at], key[(at + 1)..]);
        }

        public CapabilityEvaluation EvaluateCapability(CapabilityDefinition capability)
        {
            // Visibility gate first: a capability applies to a instance only when every
            // signal path exists in its YAML (false/empty containers count as present).
            // A nested signal is present wherever its parent collection is.
            var resolved = capability.Signals
                .Select(s => (Signal: s, Value: ResolvePath(_input.DesiredRoot, s.ParentPath ?? s.Path)))
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
            if (!TryCollectItems(signal, node, out var desiredItems, out var parents, out var validationError))
            {
                _errors.Add($"{_input.InstanceCode}: signal '{signal.Path}' — {validationError}");
                return new SignalStatusView(signal.Path, signal.Kind, true, currentJson, Array.Empty<RequiredAction>(), validationError);
            }

            var actions = new List<RequiredAction>();
            var collectionJson = CollectionJson(desiredItems);
            Dictionary<string, string?> ItemContextFor(string key, string? bodyJson)
                => ItemContext(signal, key, bodyJson, parents, collectionJson);

            // Present items: ADD (presence not confirmed) and MODIFY (body changed).
            foreach (var (key, bodyJson) in desiredItems)
            {
                var itemId = (signal.Path, key);
                var confirmed = _lastConfirmed.TryGetValue(itemId, out var c) && c.Trigger != SignalTrigger.DELETE ? c : null;

                foreach (var rule in MatchingRules(signal, SignalTrigger.ADD, key))
                {
                    var context = ItemContextFor(key, bodyJson);
                    foreach (var template in rule.Actions)
                    {
                        var identity = new ActionIdentity(signal.Path, key, template.Key);
                        var neverSucceeded = !_lastSuccess.TryGetValue(identity, out var success);
                        // Confirmed-present for this identity unless a later delete undid it —
                        // its own, or (nested) its parent's: a recreated VM needs its
                        // containers deployed again.
                        var satisfied = !neverSucceeded &&
                            !(_lastDelete.TryGetValue(itemId, out var deletedAt) && deletedAt > success!.CompletedAt) &&
                            !(signal.IsNested &&
                              _lastDelete.TryGetValue((signal.ParentPath!, SplitNestedKey(key).Parent), out var parentDeletedAt) &&
                              parentDeletedAt > success!.CompletedAt);

                        Emit(actions, capability, identity, SignalTrigger.ADD, template, bodyJson, context, satisfied);
                    }
                }

                if (confirmed is not null && !string.Equals(confirmed.DesiredValueJson, bodyJson, StringComparison.Ordinal))
                {
                    foreach (var rule in MatchingRules(signal, SignalTrigger.MODIFY, key))
                    {
                        var context = ItemContextFor(key, bodyJson);
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
                if (signal.IsNested && !parents.ContainsKey(SplitNestedKey(key).Parent))
                {
                    // The whole parent is going away (a VM being destroyed takes its
                    // containers with it) — its own DELETE covers this, and a recreated
                    // parent re-requires the child via the parent-delete check above.
                    continue;
                }

                foreach (var rule in MatchingRules(signal, SignalTrigger.DELETE, key))
                {
                    var context = ItemContextFor(key, c!.DesiredValueJson);
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
        /// re-invocable), into the need-live set. An action with an unsatisfied depends_on
        /// target is emitted all the same, BLOCKED (see <see cref="ResolveDependencies"/>);
        /// marks <see cref="RequiredAction.AdoptOnFaith"/>
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
            var (dependsOn, blockedBy) = ResolveDependencies(template, identity);

            var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
            var prompts = new List<PendingPrompt>();
            var secretInputs = new List<SecretInputRef>();
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
                    case RuleInputKind.Secret:
                        secretInputs.Add(new SecretInputRef(inputDef.Name, inputDef.Value ?? inputDef.Name));
                        break;
                }
            }

            var neverSucceeded = !_lastSuccess.ContainsKey(identity);
            var adoptOnFaith = neverSucceeded && CoveredByPastHistory(identity);

            var required = new RequiredAction(
                identity, trigger, capability.Code, template.Label, template.Requires, template.Policy,
                desiredValueJson, resolved, prompts, secretInputs, template.ExecutorKind, template.Container,
                template.Http, satisfied, adoptOnFaith)
            {
                DependsOn = dependsOn,
                BlockedBy = blockedBy,
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

        /// <summary>
        /// The <c>from:</c> context of one collection item. Beyond instance/kind/path/key/item:
        /// <c>collection</c> is every current item of the signal as one JSON object (e.g. the
        /// whole VM map a Terraform root module needs, even when applying a single VM); for a
        /// nested signal <c>key</c> is the child's own key, and <c>parent_key</c>/<c>parent</c>
        /// identify the item it lives in (the VM a container runs on).
        /// </summary>
        private Dictionary<string, string?> ItemContext(
            SignalDefinition signal, string key, string? bodyJson,
            IReadOnlyDictionary<string, string?> parents, string collectionJson)
        {
            var context = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["instance"] = _input.InstanceCode,
                ["kind"] = _input.KindCode,
                ["path"] = $"{signal.Path}.{key}",
                ["key"] = key,
                ["item"] = Unwrap(bodyJson),
                ["value"] = Unwrap(bodyJson),
                ["collection"] = collectionJson
            };

            if (signal.IsNested)
            {
                var (parentKey, childKey) = SplitNestedKey(key);
                context["path"] = $"{signal.ParentPath}.{parentKey}.{signal.ChildPath}.{childKey}";
                context["key"] = childKey;
                context["parent_key"] = parentKey;
                context["parent"] = parents.TryGetValue(parentKey, out var parentJson) ? parentJson : null;
            }

            return context;
        }

        private static string CollectionJson(IReadOnlyDictionary<string, string?> items)
        {
            var obj = new System.Text.Json.Nodes.JsonObject();
            foreach (var (key, body) in items.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                obj[key] = body is null ? null : System.Text.Json.Nodes.JsonNode.Parse(body);
            }
            return obj.ToJsonString();
        }

        /// <summary>
        /// The signal's current items with <see cref="SignalDefinition.Exclude"/> applied. A
        /// nested signal walks every item of its parent collection (<paramref name="node"/>
        /// is the parent map) and keys each child <c>{parent}/{child}</c>; <paramref name="parents"/>
        /// returns every current parent body (for <c>from: parent</c>), empty otherwise.
        /// </summary>
        private static bool TryCollectItems(
            SignalDefinition signal, object? node,
            out IReadOnlyDictionary<string, string?> items, out IReadOnlyDictionary<string, string?> parents,
            out string? validationError)
        {
            var parentBodies = new Dictionary<string, string?>(StringComparer.Ordinal);
            parents = parentBodies;

            if (!signal.IsNested)
            {
                return TryGetItems(signal, node, signal.Exclude, out items, out validationError);
            }

            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            items = result;
            validationError = null;
            if (node is null)
            {
                return true;
            }
            if (node is not IDictionary<object, object> parentMap)
            {
                validationError = $"parent collection '{signal.ParentPath}' must be a keyed map.";
                return false;
            }

            foreach (var (rawParentKey, parentBody) in parentMap)
            {
                var parentKey = Convert.ToString(rawParentKey, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (parentKey.Contains(SignalDefinition.NestedItemKeySeparator))
                {
                    validationError = $"parent key '{parentKey}' may not contain '{SignalDefinition.NestedItemKeySeparator}'.";
                    return false;
                }
                parentBodies[parentKey] = YamlFlattener.ToCanonicalJson(parentBody);

                var (found, childNode) = ResolvePath(parentBody, signal.ChildPath!);
                if (!found)
                {
                    continue; // this parent simply has none
                }
                if (!TryGetItems(signal, childNode, signal.Exclude, out var children, out validationError))
                {
                    validationError = $"'{parentKey}': {validationError}";
                    result.Clear();
                    return false;
                }
                foreach (var (childKey, body) in children)
                {
                    result[$"{parentKey}{SignalDefinition.NestedItemKeySeparator}{childKey}"] = body;
                }
            }

            return true;
        }

        /// <summary>
        /// Extracts the stable-keyed items of a collection signal. Keyed collections must
        /// be maps (an object array is the classic mistake this engine refuses to guess
        /// about); scalar lists must contain only scalars, each being its own key.
        /// </summary>
        private static bool TryGetItems(
            SignalDefinition signal, object? node, IReadOnlyList<string> exclude,
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
                        result[key] = YamlFlattener.ToCanonicalJson(WithoutFields(kvp.Value, exclude));
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
                        if (signal.Files is null)
                        {
                            result[key] = YamlFlattener.ToJsonValue(element);
                            continue;
                        }
                        if (!TryAddFileItems(signal, key, result, out validationError))
                        {
                            result.Clear();
                            return false;
                        }
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

        /// <summary>
        /// Expands one entry of a file-backed list (a path or a <c>*</c>/<c>**</c> glob,
        /// relative to the signal's <see cref="SignalDefinition.Files"/> folder) into one item
        /// per matching file, keyed by its path, with body <c>{content, file, sha256}</c>. A
        /// plain path naming no file is an error; a glob matching nothing is just empty.
        /// </summary>
        private static bool TryAddFileItems(
            SignalDefinition signal, string entry, Dictionary<string, string?> result, out string? validationError)
        {
            validationError = null;
            var index = signal.FileIndex;
            if (index is null)
            {
                validationError = $"the files under '{signal.Files}' were not loaded.";
                return false;
            }

            var pattern = entry.Trim().TrimStart('.', '/');
            IEnumerable<string> matches;
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                var regex = GlobToRegex(pattern);
                matches = index.Keys.Where(k => regex.IsMatch(k));
            }
            else if (index.ContainsKey(pattern))
            {
                matches = new[] { pattern };
            }
            else
            {
                validationError = $"'{entry}' is not a file under '{signal.Files}/'.";
                return false;
            }

            foreach (var file in matches.OrderBy(k => k, StringComparer.Ordinal))
            {
                var content = index[file];
                var body = new System.Text.Json.Nodes.JsonObject
                {
                    ["content"] = content,
                    ["file"] = file,
                    ["sha256"] = Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))
                };
                result[file] = body.ToJsonString();
            }
            return true;
        }

        /// <summary><c>**</c> crosses folders, <c>*</c> and <c>?</c> stay within one path segment.</summary>
        private static System.Text.RegularExpressions.Regex GlobToRegex(string glob)
        {
            var sb = new System.Text.StringBuilder("^");
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?"); // "**/": zero or more whole folders
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i++;
                    }
                }
                else if (c == '*')
                {
                    sb.Append("[^/]*");
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
                }
            }
            return new System.Text.RegularExpressions.Regex(sb.Append('$').ToString());
        }

        private static object? WithoutFields(object? body, IReadOnlyList<string> exclude)
        {
            if (exclude.Count == 0 || body is not IDictionary<object, object> dict)
            {
                return body;
            }

            var copy = new Dictionary<object, object>();
            foreach (var (k, v) in dict)
            {
                if (!exclude.Contains(Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture)))
                {
                    copy[k] = v;
                }
            }
            return copy;
        }

        private static bool IsOff(SignalKind kind, string? currentValueJson)
            => currentValueJson is null or "null" or "false" or "\"\"" or "{}" or "[]";
    }

    // ---------------------------------------------------------------------------------

    /// <summary>Walks a dotted path through parsed-YAML dictionaries. Found means the path exists, whatever its value.</summary>
    internal static (bool Found, object? Node) ResolvePath(object? root, string path)
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
        foreach (var prefix in new[] { "item", "parent" })
        {
            if (fromKey.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return context.TryGetValue(prefix, out var json) && json is not null
                    ? ExtractJsonPath(json, fromKey[(prefix.Length + 1)..])
                    : null;
            }
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
