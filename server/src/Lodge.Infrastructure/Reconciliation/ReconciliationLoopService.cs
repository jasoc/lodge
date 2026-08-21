using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lodge.Infrastructure.Reconciliation;

/// <summary>
/// The single background loop: one timer, one cycle, both provider modes. Interval and
/// enabled flag are re-read from DB-persisted settings every iteration, so the Sync
/// page can retune or pause the loop without a restart. Failures are logged and the
/// loop keeps ticking — reconciliation is idempotent, the next cycle heals.
/// </summary>
public sealed class ReconciliationLoopService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ReconciliationCoordinator _coordinator;
    private readonly ILogger<ReconciliationLoopService> _logger;

    public ReconciliationLoopService(
        IServiceScopeFactory scopeFactory,
        ReconciliationCoordinator coordinator,
        ILogger<ReconciliationLoopService> logger)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delaySeconds = SettingsService.DefaultIntervalSeconds;
            try
            {
                ReconciliationSettings settings;
                using (var scope = _scopeFactory.CreateScope())
                {
                    settings = await scope.ServiceProvider
                        .GetRequiredService<SettingsService>()
                        .GetReconciliationSettingsAsync(stoppingToken);
                }

                delaySeconds = settings.IntervalSeconds;
                if (settings.Enabled)
                {
                    await _coordinator.TriggerAsync(SyncTrigger.Timer, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconciliation cycle failed; retrying on the next tick");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
