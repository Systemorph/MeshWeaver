using System;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the per-viewer timestamp display seam (<see cref="DisplayTimeExtensions"/>): a
/// single stored UTC instant renders in each viewer's own named IANA zone, with DST applied
/// automatically. Fully deterministic — the assertions are independent of the host machine's
/// time zone (the whole point of storing UTC + converting through named zones).
/// </summary>
public class DisplayTimeTest
{
    // Same stored instant, one in summer (DST active) and one in winter (standard time).
    private static readonly DateTimeOffset SummerUtc = new(2026, 7, 13, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WinterUtc = new(2026, 1, 13, 16, 0, 0, TimeSpan.Zero);

    [Theory]
    // Europe/Zurich: CEST (+2) in summer, CET (+1) in winter.
    [InlineData("Europe/Zurich", true, 18, 0)]
    [InlineData("Europe/Zurich", false, 17, 0)]
    // America/New_York: EDT (-4) in summer, EST (-5) in winter.
    [InlineData("America/New_York", true, 12, 0)]
    [InlineData("America/New_York", false, 11, 0)]
    // America/Los_Angeles: a DIFFERENT US zone — "US time" is the viewer's specific zone.
    [InlineData("America/Los_Angeles", true, 9, 0)]
    [InlineData("America/Los_Angeles", false, 8, 0)]
    public void ConvertsToNamedZone_WithDst(string zoneId, bool summer, int expectedHour, int expectedMinute)
    {
        var utc = summer ? SummerUtc : WinterUtc;

        var display = DisplayTimeExtensions.ToDisplayTime(utc, zoneId);

        display.Hour.Should().Be(expectedHour);
        display.Minute.Should().Be(expectedMinute);
        display.Date.Should().Be(utc.UtcDateTime.Date); // no day rollover for these cases
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not/A/Zone")]
    public void UnsetOrInvalidZone_FallsBackToUtc(string? zoneId)
    {
        var display = DisplayTimeExtensions.ToDisplayTime(SummerUtc, zoneId);

        display.Offset.Should().Be(TimeSpan.Zero);
        display.Hour.Should().Be(16);
    }

    [Fact]
    public void DateTimeOverload_TreatsUnspecifiedAsUtc()
    {
        var unspecified = new DateTime(2026, 7, 13, 16, 0, 0, DateTimeKind.Unspecified);

        var display = DisplayTimeExtensions.ToDisplayTime(unspecified, "Europe/Zurich");

        display.Hour.Should().Be(18);
    }

    [Fact]
    public void AccessServiceExtension_ResolvesZoneFromRequestContext()
    {
        var access = new AccessService();
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "u", TimeZoneId = "America/New_York" }))
        {
            access.ToDisplayTime(SummerUtc).Hour.Should().Be(12);
        }
    }

    [Fact]
    public void AccessServiceExtension_FallsBackToCircuitContext()
    {
        var access = new AccessService();
        access.SetCircuitContext(new AccessContext { ObjectId = "u", TimeZoneId = "Europe/Zurich" });
        try
        {
            // No request-scoped context set → resolves from the circuit context.
            access.ToDisplayTime(SummerUtc).Hour.Should().Be(18);
        }
        finally
        {
            access.SetCircuitContext(null);
        }
    }

    [Fact]
    public void AccessServiceExtension_NullServiceOrNoZone_IsUtc()
    {
        AccessService? none = null;
        none.ToDisplayTime(SummerUtc).Offset.Should().Be(TimeSpan.Zero);

        var access = new AccessService();
        access.ToDisplayTime(SummerUtc).Offset.Should().Be(TimeSpan.Zero);
    }
}
