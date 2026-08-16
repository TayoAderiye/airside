using Airside.Core.Notifications;

namespace Airside.Tests.Notifications;

/// <summary>
/// When a channel is willing to receive.
/// </summary>
/// <remarks>
/// Two cases here are the reason this is harder than it looks, and both are
/// covered deliberately: a window that wraps midnight — which is what an
/// overnight on-call shift is, and which a naive start-to-end comparison matches
/// never — and daylight saving, which is why a schedule carries a zone identifier
/// rather than an offset.
/// </remarks>
public class ScheduleTests
{
    private const string London = "Europe/London";

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static NotificationSchedule Schedule(
        ScheduleWindow window,
        string zone = "UTC",
        OutsideWindowAction outside = OutsideWindowAction.Suppress,
        NotificationSeverityLevel? always = null) =>
        new()
        {
            TimeZone = zone,
            Windows = [window],
            Outside = outside,
            AlwaysDeliverAtOrAbove = always,
        };

    private static ScheduleDecision Evaluate(
        NotificationSchedule schedule,
        DateTimeOffset now,
        NotificationSeverityLevel severity = NotificationSeverityLevel.Warning) =>
        NotificationScheduler.Evaluate(schedule, severity, now);

    [Fact]
    public void NoWindowsMeansAlwaysOpen()
    {
        // The default, and what every channel created before schedules existed
        // carries. If empty meant "never", adding this would have silenced them all.
        Assert.True(NotificationSchedule.Always.IsAlwaysOpen);
        Assert.True(Evaluate(NotificationSchedule.Always, Utc(2026, 8, 16, 3)).IsOpen);
    }

    [Fact]
    public void APlainWindowOpensAndCloses()
    {
        var schedule = Schedule(new ScheduleWindow([], new TimeOnly(9, 0), new TimeOnly(17, 0)));

        Assert.False(Evaluate(schedule, Utc(2026, 8, 17, 8, 59)).IsOpen);
        Assert.True(Evaluate(schedule, Utc(2026, 8, 17, 9, 0)).IsOpen);
        Assert.True(Evaluate(schedule, Utc(2026, 8, 17, 16, 59)).IsOpen);

        // End is exclusive, so a 09:00–17:00 window is shut at 17:00 exactly.
        Assert.False(Evaluate(schedule, Utc(2026, 8, 17, 17, 0)).IsOpen);
    }

    [Fact]
    public void AWindowThatWrapsMidnightCoversBothSidesOfIt()
    {
        // The on-call shift, and the case a same-day comparison matches never.
        var schedule = Schedule(new ScheduleWindow([], new TimeOnly(22, 0), new TimeOnly(6, 0)));

        Assert.True(Evaluate(schedule, Utc(2026, 8, 17, 22, 0)).IsOpen);
        Assert.True(Evaluate(schedule, Utc(2026, 8, 17, 23, 30)).IsOpen);
        Assert.True(Evaluate(schedule, Utc(2026, 8, 18, 2, 0)).IsOpen);
        Assert.True(Evaluate(schedule, Utc(2026, 8, 18, 5, 59)).IsOpen);

        Assert.False(Evaluate(schedule, Utc(2026, 8, 18, 6, 0)).IsOpen);
        Assert.False(Evaluate(schedule, Utc(2026, 8, 18, 12, 0)).IsOpen);
    }

    [Fact]
    public void AWrappingWindowChecksTheDayItStartedOn()
    {
        // A Friday-night shift running to Saturday morning. At 02:00 on Saturday
        // the day that has to match is Friday — checking Saturday would refuse the
        // hours the shift actually covers.
        var schedule = Schedule(
            new ScheduleWindow([DayOfWeek.Friday], new TimeOnly(22, 0), new TimeOnly(6, 0)));

        // 2026-08-21 is a Friday.
        Assert.True(Evaluate(schedule, Utc(2026, 8, 21, 23, 0)).IsOpen);
        Assert.True(Evaluate(schedule, Utc(2026, 8, 22, 2, 0)).IsOpen);

        // Saturday night is not a Friday shift.
        Assert.False(Evaluate(schedule, Utc(2026, 8, 22, 23, 0)).IsOpen);
    }

    [Fact]
    public void DaysAreTheLocalDayNotTheUtcDay()
    {
        // At 23:00 UTC on a Friday it is already Saturday in Sydney. A schedule of
        // "weekdays" written by someone in Sydney must mean their weekdays.
        var schedule = Schedule(
            new ScheduleWindow([DayOfWeek.Saturday], new TimeOnly(9, 0), new TimeOnly(17, 0)),
            "Australia/Sydney");

        // 2026-08-21 23:00 UTC is 2026-08-22 09:00 in Sydney — a Saturday morning.
        Assert.True(Evaluate(schedule, Utc(2026, 8, 21, 23, 0)).IsOpen);
    }

    [Fact]
    public void TheZoneIsAppliedRatherThanTheServersClock()
    {
        var schedule = Schedule(
            new ScheduleWindow([], new TimeOnly(9, 0), new TimeOnly(17, 0)), London);

        // In August London is UTC+1, so 08:30 UTC is 09:30 locally — inside the
        // window even though the UTC hour is outside it.
        Assert.True(Evaluate(schedule, Utc(2026, 8, 17, 8, 30)).IsOpen);

        // And in January it is UTC+0, so the same UTC time is 08:30 locally and
        // outside. The zone, not a stored offset, is what makes both true.
        Assert.False(Evaluate(schedule, Utc(2026, 1, 19, 8, 30)).IsOpen);
    }

    [Fact]
    public void SuppressReportsWhyAndOffersNoRetry()
    {
        var schedule = Schedule(
            new ScheduleWindow([], new TimeOnly(9, 0), new TimeOnly(17, 0)),
            outside: OutsideWindowAction.Suppress);

        var decision = Evaluate(schedule, Utc(2026, 8, 17, 3, 0));

        Assert.False(decision.IsOpen);
        Assert.Null(decision.OpensAt);
        Assert.Contains("outside this channel's hours", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferSaysWhenItWillBeSent()
    {
        // "Do not wake me" usually means the alert still arrives, just later.
        var schedule = Schedule(
            new ScheduleWindow([], new TimeOnly(9, 0), new TimeOnly(17, 0)),
            outside: OutsideWindowAction.Defer);

        var decision = Evaluate(schedule, Utc(2026, 8, 17, 3, 0));

        Assert.False(decision.IsOpen);
        Assert.Equal(Utc(2026, 8, 17, 9, 0), decision.OpensAt);
    }

    [Fact]
    public void DeferAfterTheWindowWaitsForTheNextDay()
    {
        var schedule = Schedule(
            new ScheduleWindow([], new TimeOnly(9, 0), new TimeOnly(17, 0)),
            outside: OutsideWindowAction.Defer);

        Assert.Equal(Utc(2026, 8, 18, 9, 0), Evaluate(schedule, Utc(2026, 8, 17, 18, 0)).OpensAt);
    }

    [Fact]
    public void AWeeklyWindowIsFoundEvenFromTheOtherEndOfTheWeek()
    {
        // The search has to reach eight days out, or a "Mondays only" schedule
        // evaluated on a Monday evening finds nothing and reports no upcoming
        // window at all.
        var schedule = Schedule(
            new ScheduleWindow([DayOfWeek.Monday], new TimeOnly(9, 0), new TimeOnly(10, 0)),
            outside: OutsideWindowAction.Defer);

        // 2026-08-17 is a Monday; 20:00 is after that day's window.
        Assert.Equal(Utc(2026, 8, 24, 9, 0), Evaluate(schedule, Utc(2026, 8, 17, 20, 0)).OpensAt);
    }

    [Fact]
    public void ASevereEnoughNotificationIgnoresTheScheduleWhenAskedTo()
    {
        // Explicit rather than assumed: a quiet-hours channel that pages anyway is
        // a surprise, and one that holds a production outage until Monday is worse.
        var schedule = Schedule(
            new ScheduleWindow([], new TimeOnly(9, 0), new TimeOnly(17, 0)),
            always: NotificationSeverityLevel.Error);

        Assert.False(Evaluate(schedule, Utc(2026, 8, 17, 3, 0), NotificationSeverityLevel.Warning).IsOpen);
        Assert.True(Evaluate(schedule, Utc(2026, 8, 17, 3, 0), NotificationSeverityLevel.Error).IsOpen);
    }

    [Fact]
    public void TheSpringForwardHourThatDoesNotExistOpensWhenTheClocksLand()
    {
        // In London on 2026-03-29, 01:00 becomes 02:00 — so a window starting at
        // 01:30 never happens. Constructing that instant naively throws or silently
        // shifts by an hour; the honest reading is that it opens the moment the
        // clocks land.
        var schedule = Schedule(
            new ScheduleWindow([], new TimeOnly(1, 30), new TimeOnly(3, 0)),
            London,
            OutsideWindowAction.Defer);

        var decision = Evaluate(schedule, Utc(2026, 3, 29, 0, 30));

        Assert.NotNull(decision.OpensAt);

        // 01:00 UTC is the instant London jumps to 02:00 local.
        Assert.Equal(Utc(2026, 3, 29, 1, 0), decision.OpensAt);
    }

    [Fact]
    public void TheAutumnHourThatHappensTwiceOpensAtTheFirstOne()
    {
        // In London on 2026-10-25, 02:00 becomes 01:00, so 01:30 local happens
        // twice. A window opening "at 01:30" should open when it first does rather
        // than an hour later.
        var schedule = Schedule(
            new ScheduleWindow([], new TimeOnly(1, 30), new TimeOnly(3, 0)),
            London,
            OutsideWindowAction.Defer);

        var decision = Evaluate(schedule, Utc(2026, 10, 25, 0, 0));

        // 00:30 UTC is the first 01:30 local (still BST, UTC+1).
        Assert.Equal(Utc(2026, 10, 25, 0, 30), decision.OpensAt);
    }

    [Fact]
    public void SeveralWindowsAreCheckedTogether()
    {
        var schedule = new NotificationSchedule
        {
            Windows =
            [
                new ScheduleWindow([DayOfWeek.Saturday, DayOfWeek.Sunday], new TimeOnly(0, 0), new TimeOnly(23, 59)),
                new ScheduleWindow(
                    [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
                    new TimeOnly(18, 0),
                    new TimeOnly(23, 59)),
            ],
            Outside = OutsideWindowAction.Suppress,
        };

        // Out of hours on a weekday.
        Assert.True(Evaluate(schedule, Utc(2026, 8, 17, 20, 0)).IsOpen);

        // Working hours on a weekday.
        Assert.False(Evaluate(schedule, Utc(2026, 8, 17, 12, 0)).IsOpen);

        // All weekend.
        Assert.True(Evaluate(schedule, Utc(2026, 8, 22, 12, 0)).IsOpen);
    }

    [Fact]
    public void AnUnknownZoneFallsBackToUtcRatherThanFailing()
    {
        // A configuration mistake costs alerts at an odd hour. Throwing would cost
        // a dispatcher that stops delivering anything at all.
        var schedule = Schedule(
            new ScheduleWindow([], new TimeOnly(9, 0), new TimeOnly(17, 0)), "Mars/Olympus_Mons");

        Assert.True(Evaluate(schedule, Utc(2026, 8, 17, 12, 0)).IsOpen);
    }

    [Fact]
    public void SchedulesSurviveARoundTripThroughStorage()
    {
        var original = new NotificationSchedule
        {
            TimeZone = London,
            Windows = [new ScheduleWindow([DayOfWeek.Friday], new TimeOnly(22, 0), new TimeOnly(6, 0))],
            Outside = OutsideWindowAction.Suppress,
            AlwaysDeliverAtOrAbove = NotificationSeverityLevel.Error,
        };

        var restored = NotificationSchedule.FromJson(original.ToJson());

        Assert.Equal(London, restored.TimeZone);
        Assert.Equal(OutsideWindowAction.Suppress, restored.Outside);
        Assert.Equal(NotificationSeverityLevel.Error, restored.AlwaysDeliverAtOrAbove);
        Assert.Equal(new TimeOnly(22, 0), restored.Windows[0].Start);
        Assert.Equal([DayOfWeek.Friday], restored.Windows[0].Days);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    public void AnUnreadableScheduleFallsBackToAlwaysOpen(string? json) =>
        Assert.True(NotificationSchedule.FromJson(json).IsAlwaysOpen);
}
