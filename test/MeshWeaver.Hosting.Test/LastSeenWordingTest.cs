using System;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The "Last online" column of the registered-instances list (Doc/Architecture/InstanceTelemetry):
/// a stored UTC instant renders in the VIEWER's zone with DST applied and the date moving where it
/// must, an instance unseen for a day or more reads as OFFLINE SINCE, and a record with no stamp reads
/// as never seen. Deterministic — independent of the host machine's zone.
/// </summary>
public class LastSeenWordingTest
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoStamp_ReadsAsNever()
    {
        var reading = LastSeenWording.Describe(null, Now, "Europe/Zurich");

        reading.Kind.Should().Be(LastSeenWording.Never);
        reading.When.Should().BeEmpty();
        reading.Age.Should().BeEmpty();
    }

    [Theory]
    // Europe/Zurich: CEST (+2) in summer, CET (+1) in winter — the same 16:00Z reads 18:00 then 17:00.
    [InlineData("Europe/Zurich", 2026, 7, 13, "2026-07-13 18:00")]
    [InlineData("Europe/Zurich", 2026, 1, 13, "2026-01-13 17:00")]
    // America/New_York: EDT (-4) in summer, EST (-5) in winter.
    [InlineData("America/New_York", 2026, 7, 13, "2026-07-13 12:00")]
    [InlineData("America/New_York", 2026, 1, 13, "2026-01-13 11:00")]
    // No zone known for the viewer: UTC, said as such by the hour.
    [InlineData(null, 2026, 7, 13, "2026-07-13 16:00")]
    public void TheInstant_RendersInTheViewersZone_InBothDstDirections(string? zoneId, int year, int month, int day, string expected)
    {
        var lastSeen = new DateTimeOffset(year, month, day, 16, 0, 0, TimeSpan.Zero);
        var now = lastSeen.AddMinutes(12);

        var reading = LastSeenWording.Describe(lastSeen, now, zoneId);

        reading.Kind.Should().Be(LastSeenWording.Online);
        reading.When.Should().Be(expected);
        reading.Age.Should().Be("12 min");
    }

    [Theory]
    // 23:30Z on 29 July is 01:30 on the THIRTIETH in Zurich — the date rolls forward.
    [InlineData("Europe/Zurich", 2026, 7, 29, 23, 30, "2026-07-30 01:30")]
    // 00:30Z on 30 July is 20:30 on the TWENTY-NINTH in New York — the date rolls back.
    [InlineData("America/New_York", 2026, 7, 30, 0, 30, "2026-07-29 20:30")]
    public void AcrossMidnight_TheDateMovesWithTheZone(string zoneId, int year, int month, int day, int hour, int minute, string expected)
    {
        var lastSeen = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

        LastSeenWording.Describe(lastSeen, lastSeen.AddHours(2), zoneId).When.Should().Be(expected);
    }

    [Fact]
    public void ADayOrMoreUnseen_IsOfflineSince_AndADayLessIsNot()
    {
        var justUnder = LastSeenWording.Describe(Now - LastSeenWording.OfflineAfter + TimeSpan.FromMinutes(1), Now, "UTC");
        var exactly = LastSeenWording.Describe(Now - LastSeenWording.OfflineAfter, Now, "UTC");
        var week = LastSeenWording.Describe(Now.AddDays(-7).AddHours(-3), Now, "UTC");

        justUnder.Kind.Should().Be(LastSeenWording.Online);
        justUnder.Age.Should().Be("23 h");
        exactly.Kind.Should().Be(LastSeenWording.Offline, "the boundary itself is offline");
        exactly.Age.Should().Be("1 d");
        week.Kind.Should().Be(LastSeenWording.Offline);
        week.When.Should().Be("2026-07-06 13:00");
        week.Age.Should().Be("7 d");
    }

    [Theory]
    [InlineData(0, "<1 min")]
    [InlineData(59, "<1 min")]
    [InlineData(60, "1 min")]
    [InlineData(59 * 60, "59 min")]
    [InlineData(60 * 60, "1 h")]
    [InlineData(23 * 60 * 60 + 59 * 60, "23 h")]
    [InlineData(24 * 60 * 60, "1 d")]
    [InlineData(40 * 24 * 60 * 60, "40 d")]
    public void TheAge_IsCompactAndInvariant(int seconds, string expected) =>
        LastSeenWording.Age(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Fact]
    public void AStampInTheFuture_ClockSkew_ReadsAsJustNow_NotNegative()
    {
        var reading = LastSeenWording.Describe(Now.AddMinutes(5), Now, "UTC");

        reading.Kind.Should().Be(LastSeenWording.Online);
        reading.Age.Should().Be("<1 min");
    }
}
