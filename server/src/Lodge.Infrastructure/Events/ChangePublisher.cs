using System.Runtime.CompilerServices;
using Lodge.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lodge.Infrastructure.Events;

/// <summary>
/// Turns committed changes into <see cref="LodgeEvent"/>s, from the one place every write
/// passes through. Every state change of an action or an instance is audited (see
/// <c>AuditLog.AddAudit</c>), so an <see cref="AuditEvent"/> added in a save is the signal
/// that something a UI shows has changed; a new or completed <see cref="SyncCycle"/> is the
/// other. Events go out only after the save succeeded, deduplicated per save.
/// </summary>
public sealed class ChangePublisher : SaveChangesInterceptor
{
    private readonly LodgeEventBus _bus;
    private readonly ConditionalWeakTable<DbContext, List<LodgeEvent>> _pending = new();

    public ChangePublisher(LodgeEventBus bus)
    {
        _bus = bus;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Discard(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var events = new List<LodgeEvent>();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case AuditEvent audit when entry.State == EntityState.Added:
                    events.Add(new LodgeEvent(audit.EventType, audit.KindCode, audit.InstanceId));
                    break;
                case SyncCycle when entry.State is EntityState.Added or EntityState.Modified:
                    events.Add(new LodgeEvent(SyncCycleEventType, null, null));
                    break;
            }
        }

        if (events.Count > 0)
        {
            _pending.AddOrUpdate(context, events);
        }
    }

    public const string SyncCycleEventType = "sync.cycle";

    private void Flush(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var events))
        {
            return;
        }
        _pending.Remove(context);

        // One event per (instance, kind, type) per save: a cycle writing fifty audit rows
        // for one instance is one invalidation, not fifty.
        foreach (var e in events.Distinct())
        {
            _bus.Publish(e);
        }
    }

    private void Discard(DbContext? context)
    {
        if (context is not null)
        {
            _pending.Remove(context);
        }
    }
}
