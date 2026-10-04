using System.Text.Json;
using Lodge.Core.Abstractions;
using Lodge.Core.Catalog;
using Lodge.Core.Domain.Enums;
using Lodge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ActionEntity = Lodge.Core.Domain.Entities.Action;

namespace Lodge.Infrastructure.Execution;

/// <summary>Outcome of a confirm/status operation on an action.</summary>
public sealed record ActionExecutionResult(
    Guid ActionId,
    string Status,
    string? ExecutionRef,
    string? Message,
    bool Denied = false);

/// <summary>
/// Governs execution of actions. Used by two callers: the reconciliation loop starts AUTO
/// drift immediately (<see cref="StartAutoAsync"/>); the confirm endpoint starts QUEUED
/// (or re-runs FAILED) actions after checking the caller against the action's
/// <c>requires</c> group and merging any human-supplied prompt values
/// (<see cref="ConfirmAsync"/>). Lodge supervises; the executor runs the action. Every
/// transition is audited.
/// </summary>
public sealed class ActionExecutionService
{
    private readonly LodgeDbContext _db;
    private readonly IRunbookExecutor _executor;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly ISecretProvider _secrets;

    public ActionExecutionService(
        LodgeDbContext db,
        IRunbookExecutor executor,
        ICurrentUserAccessor currentUser,
        ISecretProvider secrets)
    {
        _db = db;
        _executor = executor;
        _currentUser = currentUser;
        _secrets = secrets;
    }

    /// <summary>
    /// Start an AUTO action the reconciler queued this cycle. Assumes no pending prompts
    /// (the reconciler guarantees it) and runs on behalf of the system, so no
    /// <c>requires</c> gate applies. Does not persist; the caller's unit of work saves.
    /// </summary>
    public async Task StartAutoAsync(
        ActionEntity action, string kindCode, string instanceCode, CancellationToken cancellationToken = default)
    {
        var handle = await _executor.StartAsync(
            await BuildRequestAsync(action, promptValues: null, kindCode, instanceCode, cancellationToken), cancellationToken);

        var previousStatus = action.Status;
        action.Status = ActionStatus.RUNNING;
        action.ExecutionRef = handle.RunId;
        action.UpdatedAt = DateTimeOffset.UtcNow;

        var payload = AuditLog.ActionSnapshot(action);
        payload["previousStatus"] = previousStatus.ToString();
        payload["auto"] = true;
        _db.AddAudit(action.InstanceId, kindCode, "action.confirmed", "system", payload);
    }

    /// <summary>
    /// Human confirmation of an action: check the current user against its <c>requires</c>
    /// group, merge supplied prompt values, then start it.
    /// </summary>
    public async Task<ActionExecutionResult?> ConfirmAsync(
        string kindCode, string instanceCode, Guid actionId, string actor,
        IReadOnlyDictionary<string, string?>? promptValues,
        CancellationToken cancellationToken = default)
    {
        var action = await LoadAsync(kindCode, instanceCode, actionId, cancellationToken);
        if (action is null)
        {
            return null;
        }

        // QUEUED rows await their first run; FAILED rows are parked drift a human may
        // re-run. Everything else (RUNNING/terminal) is not confirmable — OPTIONAL
        // re-invocability comes from the reconciler queueing a fresh row after each
        // success, one row per execution.
        var awaitingConfirmation = action.Status is ActionStatus.QUEUED or ActionStatus.FAILED;

        if (action.Status == ActionStatus.BLOCKED)
        {
            return new ActionExecutionResult(action.Id, action.Status.ToString(), action.ExecutionRef,
                "Action is waiting for its dependencies to succeed; it becomes confirmable once they have.");
        }
        if (!awaitingConfirmation)
        {
            return new ActionExecutionResult(action.Id, action.Status.ToString(), action.ExecutionRef,
                "Action is not awaiting confirmation.");
        }

        if (await DenyUnlessAllowedAsync(action, kindCode, actor, "run", cancellationToken) is { } denied)
        {
            return denied;
        }

        var handle = await _executor.StartAsync(
            await BuildRequestAsync(action, promptValues, kindCode, instanceCode, cancellationToken), cancellationToken);

        var previousStatus = action.Status;
        action.Status = ActionStatus.RUNNING;
        action.ExecutionRef = handle.RunId;
        action.UpdatedAt = DateTimeOffset.UtcNow;
        action.CompletedAt = null;

        // Prompt answers are human-typed runbook inputs, not secrets (those come only
        // from SecretInputsJson via ISecretProvider), so they are recorded as given.
        var payload = AuditLog.ActionSnapshot(action);
        payload["previousStatus"] = previousStatus.ToString();
        if (promptValues is { Count: > 0 })
        {
            payload["promptValues"] = promptValues;
        }
        _db.AddAudit(action.InstanceId, kindCode, "action.confirmed", actor, payload);

        await _db.SaveChangesAsync(cancellationToken);

        return new ActionExecutionResult(action.Id, action.Status.ToString(), action.ExecutionRef,
            $"'{action.Label}' started.");
    }

    /// <summary>
    /// UI-only invalidation of a SUCCEEDED action (real or synthetic): marks it no longer
    /// valid from now, so the identity reverts to "never succeeded" on the next
    /// reconciliation cycle and real drift (or a fresh past_history adoption) resumes.
    /// There is no YAML path to this — it is an operator action, gated by the action's
    /// <c>requires</c> group the same way as <see cref="ConfirmAsync"/>.
    /// </summary>
    public async Task<ActionExecutionResult?> InvalidateAsync(
        string kindCode, string instanceCode, Guid actionId, string actor, CancellationToken cancellationToken = default)
    {
        var action = await LoadAsync(kindCode, instanceCode, actionId, cancellationToken);
        if (action is null)
        {
            return null;
        }

        if (action.Status != ActionStatus.SUCCEEDED || action.InvalidatedAt is not null)
        {
            return new ActionExecutionResult(action.Id, action.Status.ToString(), action.ExecutionRef,
                "Only a currently-valid SUCCEEDED action can be invalidated.");
        }

        if (await DenyUnlessAllowedAsync(action, kindCode, actor, "invalidate", cancellationToken) is { } denied)
        {
            return denied;
        }

        action.InvalidatedAt = DateTimeOffset.UtcNow;
        action.InvalidatedBy = actor;
        action.UpdatedAt = DateTimeOffset.UtcNow;

        _db.AddAudit(action.InstanceId, kindCode, "action.invalidated", actor, AuditLog.ActionSnapshot(action));

        await _db.SaveChangesAsync(cancellationToken);

        return new ActionExecutionResult(action.Id, action.Status.ToString(), action.ExecutionRef,
            "Action invalidated; it will be treated as never having succeeded on the next cycle.");
    }

    public async Task<ActionExecutionResult?> RefreshStatusAsync(
        string kindCode, string instanceCode, Guid actionId, CancellationToken cancellationToken = default)
    {
        var action = await LoadAsync(kindCode, instanceCode, actionId, cancellationToken);
        if (action is null)
        {
            return null;
        }

        if (action.Status != ActionStatus.RUNNING || string.IsNullOrWhiteSpace(action.ExecutionRef))
        {
            return new ActionExecutionResult(action.Id, action.Status.ToString(), action.ExecutionRef, null);
        }

        var message = await ApplyExecutorStatusAsync(action, kindCode, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return new ActionExecutionResult(action.Id, action.Status.ToString(), action.ExecutionRef, message);
    }

    /// <summary>
    /// Lands every finished run of one instance's RUNNING actions right now, instead of
    /// waiting for the next reconciliation cycle — called before the UI reads actions, so a
    /// completed run never keeps showing as RUNNING for up to a whole loop interval.
    /// </summary>
    public async Task RefreshRunningAsync(Guid instanceId, string kindCode, CancellationToken cancellationToken = default)
    {
        var running = await _db.Actions
            .Where(a => a.InstanceId == instanceId && a.Status == ActionStatus.RUNNING && a.ExecutionRef != null)
            .ToListAsync(cancellationToken);
        foreach (var action in running)
        {
            await ApplyExecutorStatusAsync(action, kindCode, cancellationToken);
        }
        if (running.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Moves a RUNNING action to the executor's terminal state, if it has one; doesn't
    /// save. Returns the executor's status message. The single place a run lands — the UI
    /// read path and the reconciliation loop both go through it.
    /// </summary>
    public async Task<string?> ApplyExecutorStatusAsync(ActionEntity action, string kindCode, CancellationToken cancellationToken)
    {
        var status = await _executor.GetStatusAsync(action.ExecutionRef!, cancellationToken);
        var mapped = status.State switch
        {
            RunbookRunState.Succeeded => ActionStatus.SUCCEEDED,
            RunbookRunState.Failed => ActionStatus.FAILED,
            _ => ActionStatus.RUNNING
        };

        if (mapped != action.Status)
        {
            // A RUNNING row is last touched when its run starts, so UpdatedAt is the start time.
            var startedAt = action.UpdatedAt;
            var now = DateTimeOffset.UtcNow;
            action.Status = mapped;
            action.UpdatedAt = now;
            action.CompletedAt = now;

            var payload = AuditLog.ActionSnapshot(action);
            payload["state"] = status.State.ToString();
            payload["message"] = status.Message;
            payload["startedAt"] = startedAt;
            payload["durationSeconds"] = Math.Round((now - startedAt).TotalSeconds, 1);
            _db.AddAudit(action.InstanceId, kindCode, "action.execution.completed", "system", payload);

            if (mapped == ActionStatus.SUCCEEDED && action.Policy == ActionPolicy.OPTIONAL)
            {
                await RequeueOptionalAsync(action, kindCode, cancellationToken);
            }
        }

        return status.Message;
    }

    /// <summary>
    /// OPTIONAL actions (checks, plans, tests) are re-invocable: one row per execution. The
    /// reconciler would queue the next row on its next cycle anyway; doing it the moment
    /// the run lands means the button is there again right away instead of up to a loop
    /// interval later. Same snapshot as the row that just ran — if the inventory changed
    /// meanwhile, the next cycle supersedes it like any stale live row.
    /// </summary>
    private async Task RequeueOptionalAsync(ActionEntity done, string kindCode, CancellationToken cancellationToken)
    {
        var hasLive = await _db.Actions.AnyAsync(a =>
            a.InstanceId == done.InstanceId && a.SignalPath == done.SignalPath && a.ItemKey == done.ItemKey &&
            a.ActionKey == done.ActionKey &&
            (a.Status == ActionStatus.QUEUED || a.Status == ActionStatus.BLOCKED ||
             a.Status == ActionStatus.RUNNING || a.Status == ActionStatus.FAILED),
            cancellationToken);
        if (hasLive)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var next = new ActionEntity
        {
            Id = Guid.NewGuid(),
            InstanceId = done.InstanceId,
            CapabilityCode = done.CapabilityCode,
            SignalPath = done.SignalPath,
            ItemKey = done.ItemKey,
            ActionKey = done.ActionKey,
            Requires = done.Requires,
            Trigger = done.Trigger,
            Label = done.Label,
            Policy = done.Policy,
            Status = ActionStatus.QUEUED,
            DesiredValueJson = done.DesiredValueJson,
            ResolvedInputsJson = done.ResolvedInputsJson,
            PendingPromptsJson = done.PendingPromptsJson,
            ExecutorKind = done.ExecutorKind,
            ExecutorConfigJson = done.ExecutorConfigJson,
            SecretInputsJson = done.SecretInputsJson,
            DependsOnJson = done.DependsOnJson,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Actions.Add(next);
        var payload = AuditLog.ActionSnapshot(next);
        payload["requeuedAfter"] = done.Id;
        _db.AddAudit(done.InstanceId, kindCode, "action.generated", "system", payload);
    }

    /// <summary>
    /// A denied result (audited) when the current user isn't in the action's <c>requires</c>
    /// group; null when they may go ahead.
    /// </summary>
    private async Task<ActionExecutionResult?> DenyUnlessAllowedAsync(
        ActionEntity action, string kindCode, string actor, string operation, CancellationToken cancellationToken)
    {
        // Admins (the no-auth local admin, or the OIDC AdminGroup) may run everything.
        var user = _currentUser.GetCurrentUser();
        if (user.IsAdmin || ActionAccess.Allows(action.Requires, user.Groups))
        {
            return null;
        }

        var payload = AuditLog.ActionSnapshot(action);
        payload["operation"] = operation;
        payload["user"] = user.Id;
        payload["userGroups"] = user.Groups;
        _db.AddAudit(action.InstanceId, kindCode, "action.denied", actor, payload);
        await _db.SaveChangesAsync(cancellationToken);
        return new ActionExecutionResult(action.Id, action.Status.ToString(), action.ExecutionRef,
            $"'{action.Label}' requires the '{action.Requires}' group, and {user.DisplayName} isn't in it.", Denied: true);
    }

    private async Task<RunbookExecutionRequest> BuildRequestAsync(
        ActionEntity action, IReadOnlyDictionary<string, string?>? promptValues,
        string kindCode, string instanceCode, CancellationToken cancellationToken)
    {
        var (parameters, secretNames) = await BuildParametersAsync(action, promptValues, kindCode, instanceCode, cancellationToken);
        return new RunbookExecutionRequest(
            kindCode, instanceCode, $"{action.CapabilityCode}/{action.ActionKey}", action.Id, parameters,
            action.ExecutorKind,
            action.ExecutorKind == ExecutorKind.Container ? ExecutorConfigJson.DeserializeContainer(action.ExecutorConfigJson) : null,
            action.ExecutorKind == ExecutorKind.Http ? ExecutorConfigJson.DeserializeHttp(action.ExecutorConfigJson) : null,
            secretNames);
    }

    /// <summary>
    /// Merge resolved inputs (from/const), the instance/kind context, any supplied prompt
    /// values, and — the only place this ever happens — secret inputs resolved to
    /// plaintext via <see cref="ISecretProvider"/>, into the flat parameter map handed to
    /// the executor. The resolved secret values live only in this local dictionary: they
    /// are never written back to <c>action.*Json</c> and never passed to <c>AddAudit</c>.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string?> Parameters, IReadOnlyCollection<string> SecretNames)> BuildParametersAsync(
        ActionEntity action, IReadOnlyDictionary<string, string?>? promptValues,
        string kindCode, string instanceCode, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["instance"] = instanceCode,
            ["kind"] = kindCode
        };

        if (!string.IsNullOrWhiteSpace(action.ResolvedInputsJson))
        {
            var resolved = JsonSerializer.Deserialize<Dictionary<string, string?>>(action.ResolvedInputsJson);
            if (resolved is not null)
            {
                foreach (var (key, value) in resolved)
                {
                    parameters[key] = value;
                }
            }
        }

        if (promptValues is not null)
        {
            foreach (var (key, value) in promptValues)
            {
                parameters[key] = value;
            }
        }

        var secretNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(action.SecretInputsJson))
        {
            var secretRefs = JsonSerializer.Deserialize<List<SecretInputRef>>(action.SecretInputsJson);
            if (secretRefs is not null)
            {
                foreach (var secretRef in secretRefs)
                {
                    parameters[secretRef.Name] = await _secrets.GetSecretAsync(secretRef.SecretRef, cancellationToken);
                    secretNames.Add(secretRef.Name);
                }
            }
        }

        return (parameters, secretNames);
    }

    private async Task<ActionEntity?> LoadAsync(
        string kindCode, string instanceCode, Guid actionId, CancellationToken cancellationToken)
    {
        var instance = await _db.Instances.FirstOrDefaultAsync(
            t => t.KindCode == kindCode && t.InstanceCode == instanceCode, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        return await _db.Actions
            .FirstOrDefaultAsync(a => a.Id == actionId && a.InstanceId == instance.Id, cancellationToken);
    }
}
