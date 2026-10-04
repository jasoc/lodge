using Lodge.Core.Domain.Enums;
using Lodge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lodge.Infrastructure.Execution;

/// <summary>
/// At server start, removes the Lodge-managed containers nobody is waiting for: the ones
/// whose run id is not on a RUNNING container action (a run already recorded as finished,
/// or one the server never got to record). Containers of RUNNING actions are left alone —
/// the executor reattaches to them on the next status read. Runs once, in the background:
/// a daemon that is unreachable or slow never holds the server back, and with several
/// replicas each one's RUNNING rows protect that replica's containers.
/// </summary>
public sealed class OrphanContainerReaper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IContainerRunner _runner;
    private readonly ILogger<OrphanContainerReaper> _logger;

    public OrphanContainerReaper(IServiceScopeFactory scopeFactory, IContainerRunner runner, ILogger<OrphanContainerReaper> logger)
    {
        _scopeFactory = scopeFactory;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var removed = await _runner.RemoveOrphansAsync(LiveRunIdsAsync, stoppingToken);
            if (removed > 0)
            {
                _logger.LogInformation("Removed {Count} orphaned Lodge container(s) left over from a previous run", removed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up orphaned Lodge containers (is the docker daemon reachable?)");
        }
    }

    private async Task<IReadOnlySet<string>> LiveRunIdsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LodgeDbContext>();
        var ids = await db.Actions
            .Where(a => a.Status == ActionStatus.RUNNING && a.ExecutorKind == ExecutorKind.Container && a.ExecutionRef != null)
            .Select(a => a.ExecutionRef!)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet(StringComparer.Ordinal);
    }
}
