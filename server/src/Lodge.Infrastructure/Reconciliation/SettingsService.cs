using Lodge.Core.Domain.Entities;
using Lodge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>The reconciliation loop's runtime configuration, editable from the Sync page.</summary>
public sealed record ReconciliationSettings(int IntervalSeconds, bool Enabled);

/// <summary>
/// Reads and writes DB-persisted settings. The loop re-reads them every iteration, so
/// UI changes apply without a restart.
/// </summary>
public sealed class SettingsService
{
    public const string IntervalKey = "reconciliation.interval_seconds";
    public const string EnabledKey = "reconciliation.enabled";
    public const int DefaultIntervalSeconds = 60;

    private readonly LodgeDbContext _db;

    public SettingsService(LodgeDbContext db)
    {
        _db = db;
    }

    public async Task<ReconciliationSettings> GetReconciliationSettingsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.Settings
            .Where(s => s.Key == IntervalKey || s.Key == EnabledKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        var interval = rows.TryGetValue(IntervalKey, out var i) && int.TryParse(i, out var parsed)
            ? Math.Max(5, parsed)
            : DefaultIntervalSeconds;
        var enabled = !rows.TryGetValue(EnabledKey, out var e) || !bool.TryParse(e, out var flag) || flag;

        return new ReconciliationSettings(interval, enabled);
    }

    public async Task SetReconciliationSettingsAsync(ReconciliationSettings settings, CancellationToken cancellationToken = default)
    {
        await UpsertAsync(IntervalKey, Math.Max(5, settings.IntervalSeconds).ToString(), cancellationToken);
        await UpsertAsync(EnabledKey, settings.Enabled ? "true" : "false", cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertAsync(string key, string value, CancellationToken cancellationToken)
    {
        var row = await _db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null)
        {
            _db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
