using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Lodge.Infrastructure.Events;

/// <summary>
/// Carries <see cref="LodgeEvent"/>s between replicas over Postgres LISTEN/NOTIFY, so a UI
/// connected to one replica hears about changes made by another. Two independent loops: one
/// sends this process's events with <c>pg_notify</c>, one listens and republishes what other
/// processes sent (its own are recognised by <see cref="_origin"/> and skipped). Best
/// effort by design — a lost notification only means that client refreshes at its fallback
/// poll instead — so failures are logged and retried, never fatal.
/// </summary>
public sealed class PgEventRelay : BackgroundService
{
    private const string Channel = "lodge_events";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly LodgeEventBus _bus;
    private readonly string _connectionString;
    private readonly ILogger<PgEventRelay> _logger;
    private readonly string _origin = Guid.NewGuid().ToString("N");

    public PgEventRelay(LodgeEventBus bus, string connectionString, ILogger<PgEventRelay> logger)
    {
        _bus = bus;
        _connectionString = connectionString;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(SendLoopAsync(stoppingToken), ListenLoopAsync(stoppingToken));

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(ct);
                await foreach (var e in _bus.Outbound.ReadAllAsync(ct))
                {
                    await using var command = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", connection);
                    command.Parameters.AddWithValue("channel", Channel);
                    command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new Envelope(_origin, e)));
                    await command.ExecuteNonQueryAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event relay could not send to Postgres; retrying");
                await DelayAsync(ct);
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(ct);
                connection.Notification += (_, args) => Receive(args.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(ct);
                }

                while (!ct.IsCancellationRequested)
                {
                    await connection.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event relay lost its Postgres listener; reconnecting");
                await DelayAsync(ct);
            }
        }
    }

    private void Receive(string payload)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(payload);
            if (envelope is { Event: not null } && envelope.Origin != _origin)
            {
                _bus.PublishLocal(envelope.Event);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Ignoring a malformed event notification");
        }
    }

    private static async Task DelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(RetryDelay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record Envelope(string Origin, LodgeEvent? Event);
}
