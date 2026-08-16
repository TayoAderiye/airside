using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Airside.Api.Realtime;

/// <summary>One server-sent event.</summary>
/// <param name="Id">
/// The resume point. Echoed back by the browser as <c>Last-Event-ID</c> after a
/// dropped connection, so the stream picks up where it left off rather than
/// silently skipping whatever happened while it was gone.
/// </param>
public sealed record ServerSentEvent(string EventName, object Payload, string? Id = null);

/// <summary>
/// Writes a <c>text/event-stream</c> response.
/// </summary>
/// <remarks>
/// <para>
/// Replaces SignalR, which Airside only ever used in one direction. Every stream
/// here is server-to-client; the client's only input is which resource it wants,
/// and that is a URL. What SSE adds over a hub is resumability: the browser sends
/// <c>Last-Event-ID</c> on reconnect natively, so a client that drops mid-job
/// resumes from the step it last saw. A hub reconnect restores the transport but
/// not the missed application state.
/// </para>
/// <para>
/// It is also plain HTTP, so <c>curl -N</c> observes any stream in the product —
/// which matters for a self-hosted tool whose CLI exists precisely for when the
/// dashboard is not available.
/// </para>
/// </remarks>
public sealed class ServerSentEventWriter : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpResponse _response;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _heartbeatCancellation = new();
    private readonly Task _heartbeat;

    private ServerSentEventWriter(HttpResponse response)
    {
        _response = response;
        _heartbeat = RunHeartbeatAsync(_heartbeatCancellation.Token);
    }

    public static async Task<ServerSentEventWriter> StartAsync(HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers[HeaderNames.CacheControl] = "no-cache, no-store";

        // Tells nginx and friends not to buffer. Without it an intermediary can
        // hold the whole stream until it closes, which turns live progress into a
        // single burst at the end — the exact opposite of the point.
        response.Headers["X-Accel-Buffering"] = "no";

        // HTTP/2 forbids the Connection header, and Kestrel rejects it there.
        if (HttpProtocol.IsHttp11(http.Request.Protocol))
        {
            response.Headers[HeaderNames.Connection] = "keep-alive";
        }

        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await response.Body.FlushAsync(ct).ConfigureAwait(false);

        return new ServerSentEventWriter(response);
    }

    /// <summary>
    /// The resume point the client asked for, from the header a browser sends
    /// automatically or the query parameter a script can set by hand.
    /// </summary>
    public static string? ResumeFrom(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var header = request.Headers["Last-Event-ID"].ToString();

        return !string.IsNullOrEmpty(header)
            ? header
            : request.Query["lastEventId"].ToString() is { Length: > 0 } query ? query : null;
    }

    public async Task SendAsync(ServerSentEvent message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var frame = new StringBuilder();

        if (message.Id is not null)
        {
            frame.Append("id: ").Append(message.Id).Append('\n');
        }

        frame.Append("event: ").Append(message.EventName).Append('\n');

        // A payload containing a newline has to become several data: lines, or
        // everything after the first newline is read as a new field and the event
        // is silently truncated.
        var json = JsonSerializer.Serialize(message.Payload, JsonOptions);

        foreach (var line in json.Split('\n'))
        {
            frame.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
        }

        frame.Append('\n');

        await WriteAsync(frame.ToString(), ct).ConfigureAwait(false);
    }

    /// <summary>Ends the stream with a reason the client can act on.</summary>
    public Task SendClosingAsync(string reason, CancellationToken ct) =>
        SendAsync(new ServerSentEvent("stream.closing", new { reason }), ct);

    /// <summary>
    /// A comment line, which SSE ignores.
    /// </summary>
    /// <remarks>
    /// Sent on a timer because an idle stream looks identical to a dead one to
    /// every proxy and load balancer between here and the browser, and they close
    /// it after a minute or so. It is also how a write failure surfaces promptly
    /// on a stream that has nothing to say.
    /// </remarks>
    private async Task RunHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await WriteAsync(": keep-alive\n\n", ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The stream is closing.
        }
        catch (IOException)
        {
            // The client went away mid-heartbeat; the reader loop notices too.
        }
    }

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        // The heartbeat and the payload writer are different tasks on one
        // response body. Interleaved writes would splice a keep-alive comment
        // into the middle of an event frame.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await _response.WriteAsync(text, ct).ConfigureAwait(false);
            await _response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _heartbeatCancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await _heartbeat.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _heartbeatCancellation.Dispose();
        _writeLock.Dispose();
    }
}

internal static class HttpProtocol
{
    public static bool IsHttp11(string protocol) =>
        string.Equals(protocol, "HTTP/1.1", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Formats a resume id that survives a round trip through a header.</summary>
public static class StreamCursor
{
    public static string FromSequence(int sequence) =>
        sequence.ToString(CultureInfo.InvariantCulture);

    public static string FromTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    public static int? ToSequence(string? id) =>
        int.TryParse(id, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static DateTimeOffset? ToTimestamp(string? id) =>
        long.TryParse(id, CultureInfo.InvariantCulture, out var value)
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : null;
}
