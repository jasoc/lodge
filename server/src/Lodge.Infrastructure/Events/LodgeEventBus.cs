using System.Threading.Channels;

namespace Lodge.Infrastructure.Events;

/// <summary>
/// A "something changed" invalidation pushed to open UIs. It carries no data to render: the
/// client re-reads through the normal endpoints. <see cref="Type"/> is the audit event type
/// (<c>action.generated</c>, <c>action.execution.completed</c>, ...) or <c>sync.cycle</c>;
/// <see cref="InstanceId"/> is null for changes that aren't about one instance.
/// </summary>
public sealed record LodgeEvent(string Type, string? KindCode, Guid? InstanceId);

/// <summary>
/// In-process fan-out of <see cref="LodgeEvent"/>s to every open event stream. Each
/// subscriber has a small bounded buffer that drops its oldest entry when full: events are
/// idempotent invalidations, so a slow client loses nothing but a redundant refresh and can
/// never back-pressure the server. <see cref="Publish"/> also hands the event to
/// <see cref="PgEventRelay"/> so other replicas' clients hear it.
/// </summary>
public sealed class LodgeEventBus
{
    private const int SubscriberBuffer = 128;

    private readonly object _gate = new();
    private readonly List<Channel<LodgeEvent>> _subscribers = new();
    private readonly Channel<LodgeEvent> _outbound = Channel.CreateBounded<LodgeEvent>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /// <summary>Events waiting to be broadcast to other replicas.</summary>
    internal ChannelReader<LodgeEvent> Outbound => _outbound.Reader;

    /// <summary>Delivers to local subscribers and queues the event for the other replicas.</summary>
    public void Publish(LodgeEvent e)
    {
        PublishLocal(e);
        _outbound.Writer.TryWrite(e);
    }

    /// <summary>Delivers to local subscribers only (an event that came from another replica).</summary>
    internal void PublishLocal(LodgeEvent e)
    {
        lock (_gate)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(e);
            }
        }
    }

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<LodgeEvent>(
            new BoundedChannelOptions(SubscriberBuffer) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_gate)
        {
            _subscribers.Add(channel);
        }
        return new Subscription(this, channel);
    }

    private void Remove(Channel<LodgeEvent> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }
        channel.Writer.TryComplete();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly LodgeEventBus _bus;
        private readonly Channel<LodgeEvent> _channel;

        internal Subscription(LodgeEventBus bus, Channel<LodgeEvent> channel)
        {
            _bus = bus;
            _channel = channel;
        }

        public ChannelReader<LodgeEvent> Reader => _channel.Reader;

        public void Dispose() => _bus.Remove(_channel);
    }
}
