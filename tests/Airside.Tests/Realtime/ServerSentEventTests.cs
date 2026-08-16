using System.Text;
using Airside.Api.Realtime;
using Microsoft.AspNetCore.Http;

namespace Airside.Tests.Realtime;

/// <summary>
/// The wire format and the resume contract.
/// </summary>
/// <remarks>
/// SSE is forgiving in a bad way: a malformed frame is silently ignored by the
/// browser rather than reported, so a framing bug shows up as "the UI stopped
/// updating" with nothing in any log. These assert the bytes.
/// </remarks>
public class ServerSentEventWriterTests
{
    private static async Task<(string Body, DefaultHttpContext Http)> WriteAsync(
        params ServerSentEvent[] events)
    {
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;
        http.Request.Protocol = "HTTP/1.1";

        await using (var writer = await ServerSentEventWriter.StartAsync(http, CancellationToken.None))
        {
            foreach (var e in events)
            {
                await writer.SendAsync(e, CancellationToken.None);
            }
        }

        return (Encoding.UTF8.GetString(body.ToArray()), http);
    }

    [Fact]
    public async Task SetsTheHeadersThatKeepAStreamAlive()
    {
        var (_, http) = await WriteAsync();

        Assert.Equal("text/event-stream", http.Response.ContentType);
        Assert.Equal("no-cache, no-store", http.Response.Headers.CacheControl);

        // Without this an intermediary can hold the whole stream until it closes,
        // turning live progress into one burst at the end.
        Assert.Equal("no", http.Response.Headers["X-Accel-Buffering"]);
    }

    [Fact]
    public async Task WritesAWellFormedFrame()
    {
        var (body, _) = await WriteAsync(new ServerSentEvent("job.step", new { sequence = 3 }, "3"));

        Assert.Equal("id: 3\nevent: job.step\ndata: {\"sequence\":3}\n\n", body);
    }

    [Fact]
    public async Task OmitsTheIdWhenThereIsNoResumePoint()
    {
        var (body, _) = await WriteAsync(new ServerSentEvent("job.updated", new { status = "running" }));

        Assert.StartsWith("event: job.updated\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("id:", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryFrameEndsWithABlankLine()
    {
        // The blank line is what tells the browser the event is complete. Without
        // it, events accumulate unparsed and nothing is ever delivered.
        var (body, _) = await WriteAsync(
            new ServerSentEvent("a", new { x = 1 }),
            new ServerSentEvent("b", new { x = 2 }));

        Assert.Equal(2, body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith("\n\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SplitsAMultilinePayloadAcrossDataLines()
    {
        // A raw newline inside a frame starts a new field, so everything after it
        // would be dropped and the event silently truncated.
        var (body, _) = await WriteAsync(
            new ServerSentEvent("log.line", new MultilineProbe("first\nsecond")));

        var dataLines = body.Split('\n').Count(l => l.StartsWith("data:", StringComparison.Ordinal));

        Assert.Equal(1, dataLines);
        Assert.DoesNotContain("first\nsecond", body, StringComparison.Ordinal);
        Assert.Contains("first\\nsecond", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingEventCarriesAReason()
    {
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;
        http.Request.Protocol = "HTTP/1.1";

        await using (var writer = await ServerSentEventWriter.StartAsync(http, CancellationToken.None))
        {
            await writer.SendClosingAsync("rate-limited", CancellationToken.None);
        }

        var text = Encoding.UTF8.GetString(body.ToArray());

        Assert.Contains("event: stream.closing", text, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"rate-limited\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNotSetConnectionHeaderOnHttp2()
    {
        // HTTP/2 forbids it and Kestrel rejects the response outright.
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.Request.Protocol = "HTTP/2";

        await using var writer = await ServerSentEventWriter.StartAsync(http, CancellationToken.None);

        Assert.False(http.Response.Headers.ContainsKey("Connection"));
    }

    private sealed record MultilineProbe(string Text);
}

public class ResumeTests
{
    private static HttpRequest RequestWith(string? header = null, string? query = null)
    {
        var http = new DefaultHttpContext();

        if (header is not null)
        {
            http.Request.Headers["Last-Event-ID"] = header;
        }

        if (query is not null)
        {
            http.Request.QueryString = new QueryString($"?lastEventId={query}");
        }

        return http.Request;
    }

    [Fact]
    public void ReadsTheHeaderTheBrowserSendsAutomatically()
    {
        Assert.Equal("42", ServerSentEventWriter.ResumeFrom(RequestWith(header: "42")));
    }

    [Fact]
    public void FallsBackToAQueryParameterForClientsThatCannotSetHeaders()
    {
        // EventSource sets the header itself, but curl and the CLI need a way in.
        Assert.Equal("42", ServerSentEventWriter.ResumeFrom(RequestWith(query: "42")));
    }

    [Fact]
    public void PrefersTheHeaderWhenBothArePresent()
    {
        Assert.Equal("99", ServerSentEventWriter.ResumeFrom(RequestWith(header: "99", query: "1")));
    }

    [Fact]
    public void ReturnsNullWhenThereIsNoResumePoint()
    {
        Assert.Null(ServerSentEventWriter.ResumeFrom(RequestWith()));
    }

    [Fact]
    public void SequenceCursorsRoundTrip()
    {
        Assert.Equal(7, StreamCursor.ToSequence(StreamCursor.FromSequence(7)));
    }

    [Fact]
    public void TimestampCursorsRoundTripToTheMillisecond()
    {
        var original = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_123_456);

        Assert.Equal(original, StreamCursor.ToTimestamp(StreamCursor.FromTimestamp(original)));
    }

    [Fact]
    public void GarbageCursorsAreIgnoredRatherThanThrowing()
    {
        // A client can send anything. An unparseable cursor means "start from the
        // beginning", not a 500.
        Assert.Null(StreamCursor.ToSequence("not-a-number"));
        Assert.Null(StreamCursor.ToTimestamp("../../etc/passwd"));
        Assert.Null(StreamCursor.ToSequence(null));
    }
}

public class EventBusTests
{
    [Fact]
    public async Task DeliversToEverySubscriberOnATopic()
    {
        var bus = new InProcessEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var first = bus.Subscribe("job:a");
        using var second = bus.Subscribe("job:a");

        await bus.PublishAsync("job:a", new ServerSentEvent("job.step", new { n = 1 }), cts.Token);

        var a = first.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var b = second.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await a.MoveNextAsync());
        Assert.True(await b.MoveNextAsync());
        Assert.Equal("job.step", a.Current.EventName);
        Assert.Equal("job.step", b.Current.EventName);
    }

    [Fact]
    public async Task SubscribingRegistersBeforeAnythingIsRead()
    {
        // The bug this replaced: an async-iterator Subscribe deferred registration
        // to the first read, so anything published between subscribing and
        // consuming vanished — which is exactly the window the job stream uses to
        // replay persisted steps.
        var bus = new InProcessEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var subscription = bus.Subscribe("job:early");

        await bus.PublishAsync("job:early", new ServerSentEvent("job.step", new { n = 1 }), cts.Token);

        var reader = subscription.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("job.step", reader.Current.EventName);
    }

    [Fact]
    public async Task DoesNotDeliverAcrossTopics()
    {
        var bus = new InProcessEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        using var subscription = bus.Subscribe("job:a");
        var move = subscription.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token).MoveNextAsync();

        await bus.PublishAsync("job:b", new ServerSentEvent("job.step", new { n = 1 }), cts.Token);

        Assert.False(move.IsCompleted);
    }

    [Fact]
    public async Task PublishingToNobodyIsHarmless()
    {
        // Jobs run whether or not anyone is watching, and a publish with no
        // subscribers must not throw or block the handler.
        var bus = new InProcessEventBus();

        await bus.PublishAsync("job:nobody", new ServerSentEvent("job.step", new { n = 1 }), CancellationToken.None);
    }

    [Fact]
    public async Task ASlowSubscriberNeverBlocksThePublisher()
    {
        // The whole reason the channel is bounded with DropWrite: one browser tab
        // that has stopped reading must not stall the job producing the events.
        var bus = new InProcessEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var subscription = bus.Subscribe("job:slow");

        // Subscribed and then deliberately never drained: the channel fills and
        // every later publish is dropped rather than queued.
        for (var i = 0; i < 5000; i++)
        {
            await bus.PublishAsync("job:slow", new ServerSentEvent("job.step", new { i }), cts.Token);
        }

        Assert.False(cts.IsCancellationRequested, "Publishing must never block on a subscriber that stopped reading.");

        var reader = subscription.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await reader.MoveNextAsync());
    }
}
