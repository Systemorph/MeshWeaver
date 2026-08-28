#pragma warning disable CS1591

using System;
using Memex.Portal.Shared.Settings;
using Memex.Portal.Shared.SelfUpdate;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Instants shown to a viewer must be rendered in the VIEWER's zone.
///
/// <para>These are stored as UTC and were formatted as UTC, so the displayed value was simply the
/// wrong time for anyone not on UTC — and, because the fields are date-only, wrong by a whole DAY
/// either side of midnight rather than by hours (#2302). On Blazor Server the ambient culture is
/// the container's and identical for every simultaneous viewer, so this cannot be left to it.</para>
///
/// <para>The probe is 23:30Z: in Zurich (UTC+2 in August) that is the NEXT day, which is the
/// failure a UTC-formatted date-only field hides.</para>
/// </summary>
public class ViewerZoneTimestampTest
{
    private static string Echo(string key, object?[] args) => key + ":" + string.Join(",", args);

    [Fact]
    public void UpdatePolicy_checked_at_renders_in_the_viewer_zone_not_utc()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = "3.0.0-rc8.ci.9999",
            CheckedAt = new DateTimeOffset(2026, 8, 28, 23, 30, 0, TimeSpan.Zero),
        };

        var utc = UpdatePolicySettingsTab.StatusMarkdown(content, Echo);
        var zurich = UpdatePolicySettingsTab.StatusMarkdown(content, Echo, "Europe/Zurich");

        Assert.Contains("2026-08-28 23:30", utc, StringComparison.Ordinal);
        // +02:00 in August — the same instant, the NEXT day for the viewer.
        Assert.Contains("2026-08-29 01:30", zurich, StringComparison.Ordinal);
        Assert.NotEqual(utc, zurich);
    }

    /// <summary>A null zone must stay UTC — the documented fallback, and what every existing caller
    /// and test relies on.</summary>
    [Fact]
    public void A_null_zone_still_renders_utc()
    {
        var content = new UpdatePolicyContent
        {
            LatestAvailableTag = "t",
            CheckedAt = new DateTimeOffset(2026, 8, 28, 23, 30, 0, TimeSpan.Zero),
        };
        Assert.Equal(
            UpdatePolicySettingsTab.StatusMarkdown(content, Echo),
            UpdatePolicySettingsTab.StatusMarkdown(content, Echo, null));
    }
}
