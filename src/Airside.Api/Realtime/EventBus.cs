using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Airside.Api.Realtime;

/// <summary>
/// In-process fan-out from job handlers to whoever is watching.
/// </summary>
/// <remarks>
/// Replaces SignalR groups with the smallest thing that does the job: a bounded
/// channel per subscriber, keyed by topic. Airside is one process managing one
/// host, so there is nothing to coordinate across nodes and no broker to justify.
/// </remarks>
public interface IEventBus
{
    /// <summary>
    /// Registers a subscriber immediately and returns its queue.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <c>async IAsyncEnumerable</c>. An iterator method does
    /// not run a single line until the first <c>MoveNextAsync</c>, so registration
    /// would be deferred to the first read — and any caller that subscribes, then
    /// reads a replay from the database, then starts consuming would silently lose
    /// everything published in between. That is precisely the gap the job stream's
    /// subscribe-before-replay ordering exists to close, so registration has to
    /// happen when this returns.
    /// </remarks>
    IEventSubscription Subscribe(string topic);

    ValueTask PublishAsync(string topic, ServerSentEvent message, CancellationToken ct);
}

public interface IEventSubscription : IDisposable
{
    IAsyncEnumerable<ServerSentEvent> ReadAllAsync(CancellationToken ct);
}

public sealed class InProcessEventBus : IEventBus
{
    /// <summary>
    /// How far a subscriber may fall behind before its events are dropped.
    /// </summary>
    /// <remarks>
    /// Deliberately small. The alternative to dropping is buffering without limit,
    /// and one browser tab that has stopped reading must not be able to grow the
    /// control plane's heap.
    /// </remarks>
    private const int SubscriberCapacity = 256;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<ServerSentEvent>>> _topics =
        new(StringComparer.Ordinal);

    public IEventSubscription Subscribe(string topic)
    {
        // DropWrite rather than DropOldest: a gap in the middle of a step log is
        // invisible and misleading, whereas refusing further writes lets the
        // reader notice and close the stream. The client then reconnects with
        // Last-Event-ID and replays the gap properly, which is the whole reason
        // the transport carries resume ids.
        var channel = Channel.CreateBounded<ServerSentEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        var subscriberId = Guid.CreateVersion7();
        var subscribers = _topics.GetOrAdd(topic, _ => new ConcurrentDictionary<Guid, Channel<ServerSentEvent>>());
        subscribers[subscriberId] = channel;

        return new Subscription(this, topic, subscriberId, channel);
    }

    public ValueTask PublishAsync(string topic, ServerSentEvent message, CancellationToken ct)
    {
        if (_topics.TryGetValue(topic, out var subscribers))
        {
            foreach (var channel in subscribers.Values)
            {
                // Never awaited and never blocking: a slow reader must not stall
                // the job that is producing the events.
                channel.Writer.TryWrite(message);
            }
        }

        return ValueTask.CompletedTask;
    }

    private void Unsubscribe(string topic, Guid subscriberId)
    {
        if (!_topics.TryGetValue(topic, out var subscribers))
        {
            return;
        }

        subscribers.TryRemove(subscriberId, out _);

        if (subscribers.IsEmpty)
        {
            _topics.TryRemove(topic, out _);
        }
    }

    public static string JobTopic(Guid jobId) => $"job:{jobId}";

    public const string NotificationsTopic = "notifications";

    private sealed class Subscription(
        InProcessEventBus bus,
        string topic,
        Guid subscriberId,
        Channel<ServerSentEvent> channel) : IEventSubscription
    {
        public IAsyncEnumerable<ServerSentEvent> ReadAllAsync(CancellationToken ct) =>
            channel.Reader.ReadAllAsync(ct);

        public void Dispose()
        {
            bus.Unsubscribe(topic, subscriberId);
            channel.Writer.TryComplete();
        }
    }
}
