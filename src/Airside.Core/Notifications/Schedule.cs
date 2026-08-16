using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace Airside.Core.Notifications;

/// <summary>
/// When a channel is willing to receive.
/// </summary>
/// <remarks>
/// <para>
/// Everything here exists because "nine to five" is not a fact about the server.
/// Airside stores every timestamp in UTC, and a schedule expressed in UTC is
/// wrong for everyone who does not live there — and wrong by a different amount
/// twice a year. So a schedule carries an IANA zone and the comparison happens in
/// local time.
/// </para>
/// <para>
/// The two things that make this harder than it looks are windows that wrap
/// midnight — <c>22:00–06:00</c> is the on-call shift, and a naive
/// <c>start &lt;= now &lt;= end</c> matches nothing at all — and daylight saving,
/// which is why the zone is an identifier rather than an offset.
/// </para>
/// </remarks>
public sealed record NotificationSchedule
{
    /// <summary>
    /// An IANA zone identifier, such as <c>Europe/London</c>.
    /// </summary>
    /// <remarks>
    /// Not an offset. An offset is correct for half the year and an hour wrong for
    /// the other half, and the half it is wrong for is the half nobody checks.
    /// </remarks>
    public string TimeZone { get; init; } = "UTC";

    /// <summary>Periods the channel accepts in. Empty means always, as with routing rules.</summary>
    public IReadOnlyList<ScheduleWindow> Windows { get; init; } = [];

    /// <summary>What happens to a notification raised outside every window.</summary>
    public OutsideWindowAction Outside { get; init; } = OutsideWindowAction.Defer;

    /// <summary>
    /// A severity that ignores the schedule entirely.
    /// </summary>
    /// <remarks>
    /// Explicit rather than assumed. It is tempting to let errors always through,
    /// but a channel configured for quiet hours that pages anyway is a surprise,
    /// and a channel that silently holds a production outage until Monday is
    /// worse. Making it a setting means whichever the operator wanted is the one
    /// they get.
    /// </remarks>
    public NotificationSeverityLevel? AlwaysDeliverAtOrAbove { get; init; }

    [JsonIgnore]
    public bool IsAlwaysOpen => Windows.Count == 0;

    public static NotificationSchedule Always { get; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static NotificationSchedule FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Always;
        }

        try
        {
            return JsonSerializer.Deserialize<NotificationSchedule>(json, Json) ?? Always;
        }
        catch (JsonException)
        {
            // Noisy beats silent, as everywhere else in routing: a schedule that
            // cannot be read must not leave a channel that looks configured and
            // never fires.
            return Always;
        }
    }
}

/// <param name="Days">Empty means every day. Days are the local day in the schedule's zone.</param>
/// <param name="Start">Inclusive.</param>
/// <param name="End">
/// Exclusive, and may be earlier than <paramref name="Start"/> — that is a window
/// that wraps midnight, which is what an overnight shift looks like.
/// </param>
public sealed record ScheduleWindow(
    IReadOnlyList<DayOfWeek> Days,
    [property: JsonConverter(typeof(FlexibleTimeOnlyConverter))] TimeOnly Start,
    [property: JsonConverter(typeof(FlexibleTimeOnlyConverter))] TimeOnly End)
{
    [JsonIgnore]
    public bool WrapsMidnight => End <= Start;
}

[JsonConverter(typeof(JsonStringEnumConverter<OutsideWindowAction>))]
public enum OutsideWindowAction
{
    /// <summary>
    /// Hold until the window opens.
    /// </summary>
    /// <remarks>
    /// What "do not wake me" usually means: the alert still arrives, just not at
    /// three in the morning. A deferred notification that resolves before the
    /// window opens is dropped rather than delivered stale.
    /// </remarks>
    Defer,

    /// <summary>
    /// Do not send at all.
    /// </summary>
    /// <remarks>
    /// For a channel that is one half of a pair — an out-of-hours channel and a
    /// working-hours channel covering the same events between them. Deferring here
    /// would deliver everything twice.
    /// </remarks>
    Suppress,
}

/// <param name="OpensAt">When to try again, for a deferred delivery.</param>
public sealed record ScheduleDecision(bool IsOpen, DateTimeOffset? OpensAt = null, string? Reason = null)
{
    public static ScheduleDecision Open { get; } = new(true);
}

/// <summary>
/// Reads a time of day written the way people write it.
/// </summary>
/// <remarks>
/// The built-in converter accepts only <c>HH:mm:ss</c>, so <c>"09:00"</c> — which
/// is what anyone types for nine o'clock — fails the whole request with a message
/// about the body rather than about the field. Both forms are accepted, and
/// output is the long one so round-trips are stable.
/// </remarks>
public sealed class FlexibleTimeOnlyConverter : JsonConverter<TimeOnly>
{
    private static readonly string[] Formats = ["HH:mm", "HH:mm:ss", "H:mm", "HH:mm:ss.fff"];

    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();

        if (TimeOnly.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return value;
        }

        throw new JsonException($"'{text}' is not a time of day. Use HH:mm, for example 09:00 or 22:30.");
    }

    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToString("HH:mm", CultureInfo.InvariantCulture));
    }
}

/// <summary>Decides whether a channel is accepting right now.</summary>
public static class NotificationScheduler
{
    /// <summary>How far ahead to look for the next opening before giving up.</summary>
    /// <remarks>
    /// Eight days, so a weekly window is always found — a schedule of "Mondays
    /// only" evaluated on a Monday evening has to reach the following Monday.
    /// </remarks>
    private const int SearchDays = 8;

    public static ScheduleDecision Evaluate(
        NotificationSchedule schedule,
        NotificationSeverityLevel severity,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (schedule.IsAlwaysOpen)
        {
            return ScheduleDecision.Open;
        }

        if (schedule.AlwaysDeliverAtOrAbove is { } floor && severity >= floor)
        {
            return ScheduleDecision.Open;
        }

        var zone = Resolve(schedule.TimeZone);
        var local = TimeZoneInfo.ConvertTime(now, zone);

        if (schedule.Windows.Any(w => Contains(w, local)))
        {
            return ScheduleDecision.Open;
        }

        var description = Describe(schedule, zone);

        if (schedule.Outside == OutsideWindowAction.Suppress)
        {
            return new ScheduleDecision(false, null, $"outside this channel's hours ({description})");
        }

        var opensAt = NextOpening(schedule, zone, local);

        return new ScheduleDecision(
            false,
            opensAt,
            opensAt is null
                ? $"outside this channel's hours ({description}), and no upcoming window was found"
                : $"held until this channel's hours ({description})");
    }

    /// <summary>
    /// Whether a local instant falls inside a window.
    /// </summary>
    /// <remarks>
    /// The wrapping case is the one worth reading twice. A window of 22:00–06:00
    /// on Friday covers Friday from 22:00, <em>and Saturday until 06:00</em> — so
    /// at 02:00 on Saturday the day that has to match is the previous one. Treating
    /// it as a same-day comparison matches nothing at all, which is exactly the
    /// shape an overnight on-call rota takes.
    /// </remarks>
    public static bool Contains(ScheduleWindow window, DateTimeOffset local)
    {
        ArgumentNullException.ThrowIfNull(window);

        var time = TimeOnly.FromDateTime(local.DateTime);
        var day = local.DayOfWeek;

        if (!window.WrapsMidnight)
        {
            return DayMatches(window, day) && time >= window.Start && time < window.End;
        }

        // Started today and running into tomorrow.
        if (DayMatches(window, day) && time >= window.Start)
        {
            return true;
        }

        // Started yesterday and still running.
        var yesterday = (DayOfWeek)(((int)day + 6) % 7);

        return DayMatches(window, yesterday) && time < window.End;
    }

    private static bool DayMatches(ScheduleWindow window, DayOfWeek day) =>
        window.Days.Count == 0 || window.Days.Contains(day);

    /// <summary>
    /// The next instant a window opens, or null if none does within the search horizon.
    /// </summary>
    /// <remarks>
    /// Candidates are built as local wall-clock times and converted back, because
    /// that is what the operator wrote down. The conversion is where daylight
    /// saving shows up: on the spring-forward night a window starting at 01:30 does
    /// not exist, and on the autumn night it happens twice.
    /// </remarks>
    public static DateTimeOffset? NextOpening(
        NotificationSchedule schedule,
        TimeZoneInfo zone,
        DateTimeOffset local)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(zone);

        DateTimeOffset? best = null;

        for (var offset = 0; offset <= SearchDays; offset++)
        {
            var date = DateOnly.FromDateTime(local.DateTime).AddDays(offset);

            foreach (var window in schedule.Windows)
            {
                if (!DayMatches(window, date.DayOfWeek))
                {
                    continue;
                }

                if (ToInstant(date, window.Start, zone) is not { } candidate || candidate <= local)
                {
                    continue;
                }

                if (best is null || candidate < best)
                {
                    best = candidate;
                }
            }

            if (best is not null)
            {
                // Days are searched in order, so the first day that yields anything
                // holds the earliest opening. Later days cannot beat it.
                return best;
            }
        }

        return null;
    }

    /// <summary>Turns a local date and time into an instant, coping with daylight saving.</summary>
    private static DateTimeOffset? ToInstant(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var naive = date.ToDateTime(time, DateTimeKind.Unspecified);

        // Spring forward: this wall-clock time does not exist. The window opens at
        // the moment the clocks land, which is the honest reading of "from 01:30"
        // on a night when 01:30 never happens.
        if (zone.IsInvalidTime(naive))
        {
            for (var minutes = 1; minutes <= 180; minutes++)
            {
                var shifted = naive.AddMinutes(minutes);

                if (!zone.IsInvalidTime(shifted))
                {
                    return new DateTimeOffset(shifted, zone.GetUtcOffset(shifted));
                }
            }

            return null;
        }

        // Autumn back: this wall-clock time happens twice. The earlier of the two
        // is taken, so a window opens when it first opens rather than an hour late.
        var offsets = zone.IsAmbiguousTime(naive)
            ? zone.GetAmbiguousTimeOffsets(naive)
            : [zone.GetUtcOffset(naive)];

        return new DateTimeOffset(naive, offsets.Max());
    }

    /// <summary>
    /// A zone identifier, or UTC when it cannot be resolved.
    /// </summary>
    /// <remarks>
    /// Falling back rather than throwing: an unknown zone is a configuration
    /// mistake, and the cost of guessing UTC is alerts at an odd hour. The cost of
    /// throwing is a dispatcher that stops delivering anything at all.
    /// </remarks>
    public static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static string Describe(NotificationSchedule schedule, TimeZoneInfo zone)
    {
        var parts = schedule.Windows.Select(w =>
        {
            var days = w.Days.Count == 0
                ? "daily"
                : string.Join(
                    ",",
                    w.Days.Order().Select(d => d.ToString()[..3]));

            return $"{days} {w.Start.ToString("HH:mm", CultureInfo.InvariantCulture)}"
                + $"–{w.End.ToString("HH:mm", CultureInfo.InvariantCulture)}";
        });

        return string.Join("; ", parts) + " " + zone.Id;
    }
}
