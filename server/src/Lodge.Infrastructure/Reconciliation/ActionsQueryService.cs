using Lodge.Core.Domain.Enums;
using Lodge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>Row shown on the global/per-instance/per-capability action queue.</summary>
public sealed record ActionRow(
    Guid Id,
    string KindCode,
    string InstanceCode,
    string InstanceDisplayName,
    string CapabilityCode,
    string SignalPath,
    string? ItemKey,
    string ActionKey,
    string RunbookRef,
    SignalTrigger Trigger,
    string Label,
    ActionPolicy Policy,
    ActionStatus Status,
    bool Synthetic,
    bool Invalidated,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ActionsFilter(
    string? Kind = null,
    string? Instance = null,
    string? Capability = null,
    ActionStatus? Status = null,
    ActionPolicy? Policy = null,
    SignalTrigger? Trigger = null);

/// <summary>
/// DB-backed cross-instance action listing for the UI action queue (global, per-instance,
/// per-capability — all the same query with different filters applied). Deliberately
/// separate from <see cref="ReconciliationRunner"/>, which is the imperative shell around
/// the pure per-instance engine and shouldn't also own this unrelated cross-instance
/// listing concern.
/// </summary>
public sealed class ActionsQueryService
{
    private const int MaxRows = 500;

    private readonly LodgeDbContext _db;

    public ActionsQueryService(LodgeDbContext db) => _db = db;

    public async Task<IReadOnlyList<ActionRow>> QueryAsync(ActionsFilter filter, CancellationToken cancellationToken = default)
    {
        var query =
            from a in _db.Actions
            join t in _db.Instances on a.InstanceId equals t.Id
            select new { Action = a, Instance = t };

        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            query = query.Where(x => x.Instance.KindCode == filter.Kind);
        }
        if (!string.IsNullOrWhiteSpace(filter.Instance))
        {
            query = query.Where(x => x.Instance.InstanceCode == filter.Instance);
        }
        if (!string.IsNullOrWhiteSpace(filter.Capability))
        {
            query = query.Where(x => x.Action.CapabilityCode == filter.Capability);
        }
        if (filter.Status is not null)
        {
            query = query.Where(x => x.Action.Status == filter.Status);
        }
        if (filter.Policy is not null)
        {
            query = query.Where(x => x.Action.Policy == filter.Policy);
        }
        if (filter.Trigger is not null)
        {
            query = query.Where(x => x.Action.Trigger == filter.Trigger);
        }

        var rows = await query
            .OrderByDescending(x => x.Action.CreatedAt)
            .Take(MaxRows)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new ActionRow(
            x.Action.Id, x.Instance.KindCode, x.Instance.InstanceCode, x.Instance.DisplayName,
            x.Action.CapabilityCode, x.Action.SignalPath, x.Action.ItemKey, x.Action.ActionKey,
            x.Action.RunbookRef, x.Action.Trigger, x.Action.Label, x.Action.Policy, x.Action.Status,
            x.Action.Synthetic, x.Action.InvalidatedAt is not null, x.Action.CreatedAt, x.Action.CompletedAt))
            .ToList();
    }

    /// <summary>Distinct kind codes, instance codes, and capability codes for filter dropdowns.</summary>
    public async Task<ActionsFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default)
    {
        var kinds = await _db.Instances.Select(t => t.KindCode).Distinct().OrderBy(p => p).ToListAsync(cancellationToken);
        var instances = await _db.Instances
            .OrderBy(t => t.KindCode).ThenBy(t => t.InstanceCode)
            .Select(t => new InstanceOption(t.KindCode, t.InstanceCode, t.DisplayName))
            .ToListAsync(cancellationToken);
        var capabilities = await _db.Actions.Select(a => a.CapabilityCode).Distinct().OrderBy(c => c).ToListAsync(cancellationToken);

        return new ActionsFilterOptions(kinds, instances, capabilities);
    }
}

public sealed record InstanceOption(string KindCode, string InstanceCode, string DisplayName);

public sealed record ActionsFilterOptions(
    IReadOnlyList<string> Kinds,
    IReadOnlyList<InstanceOption> Instances,
    IReadOnlyList<string> Capabilities);
