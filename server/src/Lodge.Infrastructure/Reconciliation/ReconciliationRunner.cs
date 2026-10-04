using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lodge.Core.Abstractions;
using Lodge.Core.Catalog;
using Lodge.Core.Diff;
using Lodge.Core.Domain.Entities;
using Lodge.Core.Domain.Enums;
using Lodge.Core.Reconciliation;
using Lodge.Infrastructure.Execution;
using Lodge.Infrastructure.Git;
using Lodge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ActionEntity = Lodge.Core.Domain.Entities.Action;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>Outcome of one reconciliation cycle, recorded on the Sync page.</summary>
public sealed record CycleSummary(
    int KindsChecked,
    int InstancesReconciled,
    int DriftCount,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> ValidationErrors);

/// <summary>Read-only reconciliation view of one instance, shared by the UI pages.</summary>
public sealed record InstanceReconcileView(
    Instance Instance,
    string? LatestYaml,
    IReadOnlyList<CapabilityStatusView> Capabilities,
    IReadOnlyList<string> ValidationErrors);

/// <summary>
/// The imperative shell around the pure <see cref="Reconciler"/>: one cycle checks the
/// inventory head per kind, stores new revisions when it moved, and then — head
/// moved or not — recomputes drift for every instance against the live catalog and
/// applies the verdict: adopt synthetic SUCCEEDED rows on first sight, supersede stale
/// rows, create QUEUED rows for drift, self-start AUTO ones, and advance RUNNING rows.
/// Every step is idempotent, so a crashed half-applied cycle just heals on the next tick.
/// </summary>
public sealed class ReconciliationRunner
{
    private readonly LodgeDbContext _db;
    private readonly IInventorySource _inventory;
    private readonly ICapabilityCatalogProvider _catalogs;
    private readonly ActionExecutionService _execution;
    private readonly GitOptions _gitOptions;
    private readonly ILogger<ReconciliationRunner> _logger;

    public ReconciliationRunner(
        LodgeDbContext db,
        IInventorySource inventory,
        ICapabilityCatalogProvider catalogs,
        ActionExecutionService execution,
        IOptions<GitOptions> gitOptions,
        ILogger<ReconciliationRunner> logger)
    {
        _db = db;
        _inventory = inventory;
        _catalogs = catalogs;
        _execution = execution;
        _gitOptions = gitOptions.Value;
        _logger = logger;
    }

    public async Task<CycleSummary> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var validationErrors = new List<string>();
        var instancesReconciled = 0;
        var driftCount = 0;

        try
        {
            await SyncKindsAsync(messages, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never disable anything on a failed read — keep last cycle's kinds as they are.
            _logger.LogError(ex, "Kind discovery from inventory failed");
            messages.Add($"kind discovery failed — {ex.Message}");
        }

        var kinds = await _db.Kinds.Where(p => p.Enabled).OrderBy(p => p.Code).ToListAsync(cancellationToken);

        foreach (var kind in kinds)
        {
            try
            {
                await SyncInventoryAsync(kind.Code, messages, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory sync failed for kind {Kind}", kind.Code);
                messages.Add($"{kind.Code}: inventory sync failed — {ex.Message}");
            }

            // Always reconcile every instance, even when the head did not move: the rule
            // catalog can change independently of any instance's inventory.
            var instances = await _db.Instances
                .Where(t => t.KindCode == kind.Code && t.Enabled)
                .OrderBy(t => t.InstanceCode)
                .ToListAsync(cancellationToken);

            foreach (var instance in instances)
            {
                try
                {
                    var outcome = await ReconcileInstanceAsync(kind.Code, instance, cancellationToken);
                    instancesReconciled++;
                    driftCount += outcome.DriftCount;
                    messages.AddRange(outcome.Messages);
                    validationErrors.AddRange(outcome.ValidationErrors);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Reconciliation failed for {Kind}/{Instance}", kind.Code, instance.InstanceCode);
                    messages.Add($"{kind.Code}/{instance.InstanceCode}: reconciliation failed — {ex.Message}");
                }
            }
        }

        return new CycleSummary(kinds.Count, instancesReconciled, driftCount, messages, validationErrors);
    }

    /// <summary>
    /// Read-only reconciliation of one instance for page rendering: same pure engine, no
    /// row mutations, so the UI and the loop can never disagree about what is required.
    /// </summary>
    public async Task<InstanceReconcileView?> GetInstanceViewAsync(
        string kindCode, string instanceCode, CancellationToken cancellationToken = default)
    {
        var instance = await _db.Instances.FirstOrDefaultAsync(
            t => t.KindCode == kindCode && t.InstanceCode == instanceCode, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        var (input, latestYaml, errors) = await BuildInputAsync(kindCode, instance, cancellationToken);
        if (input is null)
        {
            return new InstanceReconcileView(instance, null, Array.Empty<CapabilityStatusView>(), errors);
        }

        var result = Reconciler.Reconcile(input);
        return new InstanceReconcileView(
            instance, latestYaml, result.Capabilities, errors.Concat(result.ValidationErrors).ToList());
    }

    // --- Inventory bookkeeping ---------------------------------------------------------

    /// <summary>
    /// Makes the kinds table mirror the inventory's <c>inventory/{kind}/</c> folders: a new
    /// folder registers (and enables) a kind, a vanished one disables it — never deletes,
    /// so its instances and action history stay intact and come back if the folder does.
    /// The display name follows the optional <c>kind.yaml</c>, defaulting to the code.
    /// </summary>
    private async Task SyncKindsAsync(List<string> messages, CancellationToken cancellationToken)
    {
        var declared = (await _inventory.GetKindsAsync(cancellationToken)).ToDictionary(k => k.Code, StringComparer.Ordinal);
        var existing = await _db.Kinds.ToListAsync(cancellationToken);

        foreach (var kind in existing)
        {
            if (declared.Remove(kind.Code, out var descriptor))
            {
                var name = descriptor.Name ?? kind.Code;
                if (!kind.Enabled || kind.Name != name)
                {
                    if (!kind.Enabled)
                    {
                        messages.Add($"{kind.Code}: kind re-enabled (folder present in inventory)");
                        _db.AddAudit(null, kind.Code, "kind.enabled", "system", new { kind.Code });
                    }
                    kind.Enabled = true;
                    kind.Name = name;
                }
            }
            else if (kind.Enabled)
            {
                kind.Enabled = false;
                messages.Add($"{kind.Code}: kind disabled (no inventory/{kind.Code}/ folder)");
                _db.AddAudit(null, kind.Code, "kind.disabled", "system", new { kind.Code });
            }
        }

        foreach (var descriptor in declared.Values)
        {
            _db.Kinds.Add(new Kind { Code = descriptor.Code, Name = descriptor.Name ?? descriptor.Code, Enabled = true });
            messages.Add($"{descriptor.Code}: kind registered from inventory");
            _db.AddAudit(null, descriptor.Code, "kind.registered", "system", new { descriptor.Code, descriptor.Name });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task SyncInventoryAsync(string kindCode, List<string> messages, CancellationToken cancellationToken)
    {
        var branch = _gitOptions.IsGitHub ? _gitOptions.GitHub.Branch : "local";
        var head = await _inventory.GetHeadAsync(kindCode, cancellationToken);

        var state = await _db.RegistrySyncStates.FirstOrDefaultAsync(
            s => s.KindCode == kindCode && s.Branch == branch, cancellationToken);
        if (state is null)
        {
            state = new RegistrySyncState
            {
                Id = Guid.NewGuid(),
                KindCode = kindCode,
                Branch = branch,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _db.RegistrySyncStates.Add(state);
        }
        else if (string.Equals(state.LastSeenSha, head, StringComparison.Ordinal))
        {
            return; // steady state: one cheap head check, nothing to fetch
        }

        var files = await _inventory.GetInstanceFilesAsync(kindCode, cancellationToken);
        foreach (var group in files.GroupBy(f => f.InstanceCode, StringComparer.Ordinal))
        {
            var instanceCode = group.Key;
            var (mergedYaml, mergeError) = InstanceYamlMerger.Merge(
                group.Select(f => (f.RepoRelativePath, f.Content)).ToList());
            if (mergeError is not null)
            {
                _logger.LogError("Inventory merge failed for {Kind}/{Instance}: {Error}", kindCode, instanceCode, mergeError);
                messages.Add($"{kindCode}/{instanceCode}: inventory merge failed — {mergeError}");
                continue;
            }

            var instance = await UpsertInstanceAsync(kindCode, instanceCode, mergedYaml!, cancellationToken);
            var latest = await _db.RegistryRevisions
                .Where(r => r.InstanceId == instance.Id)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var hash = ComputeHash(mergedYaml!);
            if (latest is not null && string.Equals(latest.ContentHash, hash, StringComparison.Ordinal))
            {
                continue;
            }

            _db.RegistryRevisions.Add(new RegistryRevision
            {
                Id = Guid.NewGuid(),
                InstanceId = instance.Id,
                GitRef = head,
                YamlContent = mergedYaml!,
                ContentHash = hash,
                CreatedAt = DateTimeOffset.UtcNow
            });
            _db.AddAudit(instance.Id, kindCode, "registry.revision.stored", "system", new
            {
                instance = instance.InstanceCode,
                gitRef = head,
                previousGitRef = latest?.GitRef,
                contentHash = hash,
                files = group.Select(f => f.RepoRelativePath).ToList(),
                changes = DiffValues(latest?.YamlContent, mergedYaml!)
            });
            messages.Add($"{kindCode}/{instance.InstanceCode}: new revision stored ({Shorten(head)})");
        }

        await SyncInstanceEnablementAsync(kindCode, files.Select(f => f.InstanceCode).ToHashSet(StringComparer.Ordinal), messages, cancellationToken);

        state.LastSeenSha = head;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Same contract as <see cref="SyncKindsAsync"/>, one level down: an instance whose
    /// folder no longer has inventory files is disabled (never deleted — its history
    /// stays), and re-enabled if the folder comes back.
    /// </summary>
    private async Task SyncInstanceEnablementAsync(
        string kindCode, IReadOnlySet<string> presentCodes, List<string> messages, CancellationToken cancellationToken)
    {
        var instances = await _db.Instances.Where(t => t.KindCode == kindCode).ToListAsync(cancellationToken);
        foreach (var instance in instances)
        {
            var present = presentCodes.Contains(instance.InstanceCode);
            if (present == instance.Enabled)
            {
                continue;
            }

            instance.Enabled = present;
            var verb = present ? "enabled" : "disabled";
            messages.Add(present
                ? $"{kindCode}/{instance.InstanceCode}: instance re-enabled (folder present in inventory)"
                : $"{kindCode}/{instance.InstanceCode}: instance disabled (no inventory/{kindCode}/instances/{instance.InstanceCode}/ files)");
            _db.AddAudit(instance.Id, kindCode, $"instance.{verb}", "system", new { instance = instance.InstanceCode });
        }
    }

    private async Task<Instance> UpsertInstanceAsync(string kindCode, string instanceCode, string mergedYaml, CancellationToken cancellationToken)
    {
        var instance = await _db.Instances.FirstOrDefaultAsync(
            t => t.KindCode == kindCode && t.InstanceCode == instanceCode, cancellationToken);

        var flat = YamlFlattener.Flatten(mergedYaml);
        var displayName = flat.TryGetValue("display_name", out var dn) ? Unquote(dn) : instanceCode;
        var generation = flat.TryGetValue("generation", out var gen) && int.TryParse(gen, out var g) ? g : 0;
        var region = flat.TryGetValue("region", out var reg) ? Unquote(reg) : string.Empty;

        if (instance is null)
        {
            instance = new Instance
            {
                Id = Guid.NewGuid(),
                KindCode = kindCode,
                InstanceCode = instanceCode,
                DisplayName = displayName,
                Generation = generation,
                Region = region,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Instances.Add(instance);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            instance.DisplayName = displayName;
            instance.Generation = generation;
            instance.Region = region;
        }

        return instance;
    }

    // --- Per-instance reconciliation -----------------------------------------------------

    private sealed record InstanceOutcome(int DriftCount, IReadOnlyList<string> Messages, IReadOnlyList<string> ValidationErrors);

    private async Task<InstanceOutcome> ReconcileInstanceAsync(
        string kindCode, Instance instance, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var (input, _, errors) = await BuildInputAsync(kindCode, instance, cancellationToken);
        if (input is null)
        {
            return new InstanceOutcome(0, messages, errors);
        }

        var result = Reconciler.Reconcile(input);
        errors.AddRange(result.ValidationErrors);

        // Supersede first and save, so the one-live-row-per-identity unique index is
        // free before replacement rows are inserted.
        if (result.ToSupersede.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var stale = await _db.Actions
                .Where(a => result.ToSupersede.Contains(a.Id))
                .ToListAsync(cancellationToken);
            var replacements = result.ToCreate.Concat(result.ToAdopt).ToDictionary(r => r.Identity);
            foreach (var row in stale)
            {
                var previousStatus = row.Status;
                row.Status = ActionStatus.SUPERSEDED;
                row.UpdatedAt = now;
                row.CompletedAt = now;

                var payload = AuditLog.ActionSnapshot(row);
                payload["previousStatus"] = previousStatus.ToString();
                if (replacements.TryGetValue(new ActionIdentity(row.SignalPath, row.ItemKey, row.ActionKey), out var replacement))
                {
                    payload["newDesiredValue"] = AuditLog.ParseJson(replacement.DesiredValueJson);
                }
                else
                {
                    payload["reason"] = "no longer required by the current inventory and catalog";
                }
                _db.AddAudit(instance.Id, kindCode, "action.superseded", "system", payload);
            }
            await _db.SaveChangesAsync(cancellationToken);
            messages.Add($"{kindCode}/{instance.InstanceCode}: superseded {stale.Count} stale action(s)");
        }

        // past_history adoption: an identity explicitly declared as already-done outside
        // Lodge's governance is recorded once as synthetic SUCCEEDED instead of queueing a
        // real run for it (à la `terraform import`) — see instance YAML `past_history`.
        foreach (var adopt in result.ToAdopt)
        {
            var row = NewActionRow(instance.Id, adopt);
            row.Status = ActionStatus.SUCCEEDED;
            row.Synthetic = true;
            row.CompletedAt = row.CreatedAt;
            _db.Actions.Add(row);
            _db.AddAudit(instance.Id, kindCode, "action.adopted", "system", AuditLog.ActionSnapshot(row));
        }
        if (result.ToAdopt.Count > 0)
        {
            messages.Add($"{kindCode}/{instance.InstanceCode}: adopted {result.ToAdopt.Count} action(s) via past_history");
        }

        // The last confirmed value per identity, so a generated action records the change
        // it is meant to apply (previous -> desired), not just the target.
        var lastConfirmed = input.History
            .GroupBy(h => h.Identity)
            .ToDictionary(g => g.Key, g => g.MaxBy(h => h.CompletedAt)!);

        var createdByIdentity = new Dictionary<ActionIdentity, ActionEntity>();
        foreach (var create in result.ToCreate)
        {
            var row = NewActionRow(instance.Id, create);
            row.Status = create.Blocked ? ActionStatus.BLOCKED : ActionStatus.QUEUED;
            _db.Actions.Add(row);
            createdByIdentity[create.Identity] = row;

            var payload = AuditLog.ActionSnapshot(row);
            if (lastConfirmed.TryGetValue(create.Identity, out var previous))
            {
                payload["previousValue"] = AuditLog.ParseJson(previous.DesiredValueJson);
                payload["previousActionId"] = previous.Id;
            }
            if (create.Blocked)
            {
                payload["blockedBy"] = create.BlockedBy;
            }
            if (create.PendingPrompts.Count > 0)
            {
                payload["pendingPrompts"] = create.PendingPrompts.Select(p => p.Name).ToList();
            }
            _db.AddAudit(instance.Id, kindCode, "action.generated", "system", payload);
        }

        // Live rows that haven't run yet follow their dependencies: unblocked ones become
        // QUEUED before the AUTO step below, so an unblocked AUTO row starts this cycle.
        await SetLiveStatusAsync(instance, kindCode, result.ToBlock, ActionStatus.BLOCKED, "action.blocked", cancellationToken);
        await SetLiveStatusAsync(instance, kindCode, result.ToUnblock, ActionStatus.QUEUED, "action.unblocked", cancellationToken);
        if (result.ToUnblock.Count > 0)
        {
            messages.Add($"{kindCode}/{instance.InstanceCode}: unblocked {result.ToUnblock.Count} action(s)");
        }
        await _db.SaveChangesAsync(cancellationToken);

        // AUTO drift runs itself; FAILED rows are deliberately absent from ToAutoStart
        // (parked until a human re-runs them or the desired snapshot changes). Each start
        // commits the row as RUNNING before its run is launched.
        var autoStarted = 0;
        foreach (var auto in result.ToAutoStart)
        {
            var row = auto.LiveRowId is { } liveId
                ? await _db.Actions.FirstAsync(a => a.Id == liveId, cancellationToken)
                : createdByIdentity[auto.Identity];
            if (await _execution.StartAutoAsync(row, kindCode, instance.InstanceCode, cancellationToken))
            {
                autoStarted++;
            }
        }
        if (autoStarted > 0)
        {
            messages.Add($"{kindCode}/{instance.InstanceCode}: auto-started {autoStarted} action(s)");
        }

        // Advance RUNNING rows so AUTO completions land without anyone watching the UI.
        await RefreshRunningActionsAsync(kindCode, instance, createdByIdentity.Values, cancellationToken);

        var drift = result.Capabilities
            .SelectMany(c => c.Signals)
            .SelectMany(s => s.Actions)
            .Count(a => a.IsDrift);

        return new InstanceOutcome(drift, messages, errors);
    }

    private async Task<(ReconciliationInput? Input, string? LatestYaml, List<string> Errors)> BuildInputAsync(
        string kindCode, Instance instance, CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        var latestYaml = await _db.RegistryRevisions
            .Where(r => r.InstanceId == instance.Id)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.YamlContent)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestYaml is null)
        {
            return (null, null, errors);
        }

        var catalogResult = await _catalogs.GetCatalogAsync(kindCode, instance.InstanceCode, cancellationToken);
        errors.AddRange(catalogResult.Errors);

        var rows = await _db.Actions
            .Where(a => a.InstanceId == instance.Id)
            .ToListAsync(cancellationToken);

        var history = rows
            .Where(a => a.Status == ActionStatus.SUCCEEDED && a.InvalidatedAt is null)
            .Select(a => new SucceededRecord(
                a.Id, new ActionIdentity(a.SignalPath, a.ItemKey, a.ActionKey),
                a.Trigger, a.DesiredValueJson, a.CompletedAt ?? a.CreatedAt, a.Synthetic))
            .ToList();
        var live = rows
            .Where(a => a.Status is ActionStatus.QUEUED or ActionStatus.BLOCKED or ActionStatus.RUNNING or ActionStatus.FAILED)
            .Select(a => new LiveActionRow(
                a.Id, new ActionIdentity(a.SignalPath, a.ItemKey, a.ActionKey),
                a.Trigger, a.Status, a.DesiredValueJson, a.ExecutorConfigJson, a.Requires,
                a.Policy, a.ResolvedInputsJson, a.SecretInputsJson, a.PendingPromptsJson))
            .ToList();

        var input = new ReconciliationInput(
            kindCode,
            instance.InstanceCode,
            YamlFlattener.Parse(latestYaml),
            catalogResult.Catalog,
            history,
            live);

        return (input, latestYaml, errors);
    }

    private async Task RefreshRunningActionsAsync(
        string kindCode, Instance instance, IEnumerable<ActionEntity> justCreated, CancellationToken cancellationToken)
    {
        var justCreatedIds = justCreated.Select(a => a.Id).ToHashSet();
        var running = await _db.Actions
            .Where(a => a.InstanceId == instance.Id && a.Status == ActionStatus.RUNNING && a.ExecutionRef != null)
            .ToListAsync(cancellationToken);

        var changed = false;
        foreach (var row in running.Where(r => !justCreatedIds.Contains(r.Id)))
        {
            var before = row.Status;
            await _execution.ApplyExecutorStatusAsync(row, kindCode, cancellationToken);
            changed |= row.Status != before;
        }

        if (changed)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    // --- Helpers -------------------------------------------------------------------------

    private async Task SetLiveStatusAsync(
        Instance instance, string kindCode, IReadOnlyList<Guid> ids, ActionStatus status, string eventType, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var rows = await _db.Actions.Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.Status = status;
            row.UpdatedAt = now;
            _db.AddAudit(instance.Id, kindCode, eventType, "system", AuditLog.ActionSnapshot(row));
        }
    }

    private static ActionEntity NewActionRow(Guid instanceId, RequiredAction required)
    {
        var now = DateTimeOffset.UtcNow;
        return new ActionEntity
        {
            Id = Guid.NewGuid(),
            InstanceId = instanceId,
            CapabilityCode = required.CapabilityCode,
            SignalPath = required.Identity.SignalPath,
            ItemKey = required.Identity.ItemKey,
            ActionKey = required.Identity.ActionKey,
            Requires = required.Requires,
            Trigger = required.Trigger,
            Label = required.Label,
            Policy = required.Policy,
            DesiredValueJson = required.DesiredValueJson,
            ResolvedInputsJson = required.ResolvedInputsJson,
            PendingPromptsJson = required.PendingPromptsJson,
            ExecutorKind = required.ExecutorKind,
            ExecutorConfigJson = required.ExecutorConfigJson,
            SecretInputsJson = required.SecretInputsJson,
            DependsOnJson = required.DependsOn.Count == 0 ? null : JsonSerializer.Serialize(required.DependsOn),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// Leaf-level changes between two inventory revisions, with the values themselves:
    /// <c>from</c> is null for an added path, <c>to</c> null for a removed one.
    /// </summary>
    private static List<object> DiffValues(string? previousYaml, string currentYaml)
    {
        var before = previousYaml is null ? new Dictionary<string, string>() : YamlFlattener.Flatten(previousYaml);
        var after = YamlFlattener.Flatten(currentYaml);

        return before.Keys.Union(after.Keys)
            .Order(StringComparer.Ordinal)
            .Select(path => (path, from: before.GetValueOrDefault(path), to: after.GetValueOrDefault(path)))
            .Where(c => !string.Equals(c.from, c.to, StringComparison.Ordinal))
            .Select(c => (object)new { c.path, from = AuditLog.ParseJson(c.from), to = AuditLog.ParseJson(c.to) })
            .ToList();
    }

    public static string ComputeHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string Shorten(string sha) => sha.Length > 12 ? sha[..12] : sha;

    private static string Unquote(string canonicalJson)
        => canonicalJson.Length >= 2 && canonicalJson[0] == '"' && canonicalJson[^1] == '"'
            ? canonicalJson[1..^1]
            : canonicalJson;
}
