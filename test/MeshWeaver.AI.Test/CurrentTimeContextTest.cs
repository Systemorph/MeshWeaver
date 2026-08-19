#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Globalization;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit coverage for <see cref="CurrentTimeContext"/> — the per-round date/time block that gives
/// every agent an anchor for "what day is today" and for every relative expression computed off it
/// (#1651). The formatter is PURE (instant + zone id in, markdown out), so all of this runs with no
/// hub, no circuit and no mesh.
///
/// <para>What each case is guarding, because the failure mode is silence:</para>
/// <list type="bullet">
///   <item><description><b>Both DST directions.</b> A hard-coded offset passes one and fails the
///   other — the whole reason the seam uses named IANA zones. Zurich is UTC+2 in July and UTC+1 in
///   January; New York is UTC−4 in July and UTC−5 in January, on different switch dates, so the two
///   regions together also pin that DST is applied PER REGION rather than globally.</description></item>
///   <item><description><b>The date rollover.</b> An instant late in the UTC evening is already
///   TOMORROW in Zurich, and an instant early in the UTC morning is still YESTERDAY in New York.
///   This is the case that reads as an off-by-one rather than as a timezone bug — and it is exactly
///   the case a calendar agent gets wrong when it books "tomorrow".</description></item>
///   <item><description><b>Zone null / unresolvable → UTC, and SAYS so.</b> Never the server's
///   zone: the deployment container runs UTC, so a server-local fallback looks right in CI and is
///   wrong in Zurich. An unresolvable id must not be echoed either — printing a zone name the
///   conversion did not use claims a localization that never happened.</description></item>
///   <item><description><b>Invariant English, never the ambient culture.</b> The block is
///   model-facing (the same rule that keeps tool-parameter descriptions untranslated), and a render
///   hops schedulers where an AsyncLocal culture would not survive anyway.</description></item>
/// </list>
/// </summary>
public class CurrentTimeContextTest
{
    private const string Zurich = "Europe/Zurich";
    private const string NewYork = "America/New_York";

    private static DateTimeOffset Utc(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // ── DST, both directions, two regions ────────────────────────────────────────────────

    [Fact]
    public void Summer_Zurich_rendersCEST_UtcPlusTwo()
    {
        var block = CurrentTimeContext.Describe(Utc("2026-07-20T14:32:00Z"), Zurich);

        Assert.Contains("- **Today (the user's local date):** Monday, 2026-07-20", block, StringComparison.Ordinal);
        Assert.Contains("- **Local time now:** 16:32 (Europe/Zurich, UTC+02:00)", block, StringComparison.Ordinal);
        Assert.Contains("`2026-07-20T14:32:00Z`", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Winter_Zurich_rendersCET_UtcPlusOne()
    {
        // Same wall-clock UTC as the summer case: an implementation with a hard-coded +02:00
        // passes that one and fails this one.
        var block = CurrentTimeContext.Describe(Utc("2026-01-20T14:32:00Z"), Zurich);

        Assert.Contains("- **Today (the user's local date):** Tuesday, 2026-01-20", block, StringComparison.Ordinal);
        Assert.Contains("- **Local time now:** 15:32 (Europe/Zurich, UTC+01:00)", block, StringComparison.Ordinal);
        Assert.Contains("`2026-01-20T14:32:00Z`", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Summer_NewYork_rendersEDT_UtcMinusFour()
    {
        var block = CurrentTimeContext.Describe(Utc("2026-07-20T14:32:00Z"), NewYork);

        Assert.Contains("- **Today (the user's local date):** Monday, 2026-07-20", block, StringComparison.Ordinal);
        Assert.Contains("- **Local time now:** 10:32 (America/New_York, UTC-04:00)", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Winter_NewYork_rendersEST_UtcMinusFive()
    {
        var block = CurrentTimeContext.Describe(Utc("2026-01-20T14:32:00Z"), NewYork);

        Assert.Contains("- **Today (the user's local date):** Tuesday, 2026-01-20", block, StringComparison.Ordinal);
        Assert.Contains("- **Local time now:** 09:32 (America/New_York, UTC-05:00)", block, StringComparison.Ordinal);
    }

    // ── The date rollover — the case that reads as an off-by-one ─────────────────────────

    [Fact]
    public void LateUtcEvening_isAlreadyTomorrow_inZurich()
    {
        // 23:30 UTC on the 29th is 01:30 on the 30th in Zurich. An agent that anchored on the UTC
        // date would book "tomorrow" a full day early.
        var block = CurrentTimeContext.Describe(Utc("2026-07-29T23:30:00Z"), Zurich);

        Assert.Contains("- **Today (the user's local date):** Thursday, 2026-07-30", block, StringComparison.Ordinal);
        Assert.Contains("- **Local time now:** 01:30 (Europe/Zurich, UTC+02:00)", block, StringComparison.Ordinal);
        // …while the machine-facing instant stays on the UTC calendar, unshifted.
        Assert.Contains("`2026-07-29T23:30:00Z`", block, StringComparison.Ordinal);
    }

    [Fact]
    public void EarlyUtcMorning_isStillYesterday_inNewYork()
    {
        // The mirror image, west of Greenwich: 02:30 UTC on Monday the 20th is 22:30 on Sunday the
        // 19th in New York. "What day is today" is a different DAY, not merely a different hour.
        var block = CurrentTimeContext.Describe(Utc("2026-07-20T02:30:00Z"), NewYork);

        Assert.Contains("- **Today (the user's local date):** Sunday, 2026-07-19", block, StringComparison.Ordinal);
        Assert.Contains("- **Local time now:** 22:30 (America/New_York, UTC-04:00)", block, StringComparison.Ordinal);
        Assert.Contains("`2026-07-20T02:30:00Z`", block, StringComparison.Ordinal);
    }

    // ── Unknown zone → UTC, and it says so ───────────────────────────────────────────────

    [Fact]
    public void NullZone_staysUtc_andSaysTheZoneIsUnknown()
    {
        var block = CurrentTimeContext.Describe(Utc("2026-07-20T14:32:00Z"), null);

        // Unchanged — NOT converted into the host/server zone.
        Assert.Contains("- **Today (the user's local date):** Monday, 2026-07-20", block, StringComparison.Ordinal);
        Assert.Contains("- **Local time now:** 14:32 (UTC — the user's time zone is not known", block, StringComparison.Ordinal);
        Assert.Contains("`2026-07-20T14:32:00Z`", block, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyZone_staysUtc()
    {
        var block = CurrentTimeContext.Describe(Utc("2026-01-20T14:32:00Z"), "   ");

        Assert.Contains("- **Local time now:** 14:32 (UTC — the user's time zone is not known", block, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvableZone_staysUtc_andDoesNotEchoTheBogusId()
    {
        var block = CurrentTimeContext.Describe(Utc("2026-07-20T14:32:00Z"), "Mars/Olympus_Mons");

        Assert.Contains("- **Local time now:** 14:32 (UTC — the user's time zone is not known", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Mars/Olympus_Mons", block, StringComparison.Ordinal);
    }

    [Fact]
    public void NullZoneRendering_isIdenticalToRenderingTheUtcZoneExplicitly()
    {
        // Pins "degrades to UTC" as a VALUE, not just as prose: whatever the host's own zone is,
        // the local clock for an unknown viewer is the UTC clock.
        var instant = Utc("2026-07-20T14:32:00Z");
        var unknown = CurrentTimeContext.Describe(instant, null);
        var explicitUtc = CurrentTimeContext.Describe(instant, "UTC");

        Assert.Contains("- **Today (the user's local date):** Monday, 2026-07-20", unknown, StringComparison.Ordinal);
        Assert.Contains("- **Today (the user's local date):** Monday, 2026-07-20", explicitUtc, StringComparison.Ordinal);
        Assert.Contains("14:32", unknown, StringComparison.Ordinal);
        Assert.Contains("14:32", explicitUtc, StringComparison.Ordinal);
    }

    // ── Machine-facing output stays UTC and says so ──────────────────────────────────────

    [Fact]
    public void Iso_normalisesToUtc_withTheTrailingZ()
    {
        // A non-UTC offset in → the same INSTANT out, in UTC, with an explicit Z. A zone-less
        // string would re-parse in the server's zone on the way back.
        Assert.Equal("2026-07-20T14:32:00Z",
            CurrentTimeContext.Iso(new DateTimeOffset(2026, 7, 20, 16, 32, 0, TimeSpan.FromHours(2))));
        Assert.Equal("2026-01-20T14:32:00Z",
            CurrentTimeContext.Iso(new DateTimeOffset(2026, 1, 20, 9, 32, 0, TimeSpan.FromHours(-5))));
    }

    [Fact]
    public void Describe_normalisesANonUtcInstant_onBothClocks()
    {
        // 16:32+02:00 IS 14:32Z — the caller's offset must not leak into either rendering.
        var block = CurrentTimeContext.Describe(
            new DateTimeOffset(2026, 7, 20, 16, 32, 0, TimeSpan.FromHours(2)), NewYork);

        Assert.Contains("- **Local time now:** 10:32 (America/New_York, UTC-04:00)", block, StringComparison.Ordinal);
        Assert.Contains("`2026-07-20T14:32:00Z`", block, StringComparison.Ordinal);
    }

    // ── Model-facing text: invariant English, never the ambient culture ──────────────────

    [Fact]
    public void Rendering_isInvariantEnglish_underAGermanAmbientCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            // de-DE would otherwise give "Montag", "20.07.2026" and a comma decimal — and a round
            // hops schedulers, so an ambient culture is never a legitimate source anyway.
            var german = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;

            var block = CurrentTimeContext.Describe(Utc("2026-07-20T14:32:00Z"), Zurich);

            Assert.Contains("Monday, 2026-07-20", block, StringComparison.Ordinal);
            Assert.DoesNotContain("Montag", block, StringComparison.Ordinal);
            Assert.Contains("16:32 (Europe/Zurich, UTC+02:00)", block, StringComparison.Ordinal);
            Assert.Equal("2026-07-20T14:32:00Z", CurrentTimeContext.Iso(Utc("2026-07-20T14:32:00Z")));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    // ── Shape: the heading and the standing guidance the agent acts on ───────────────────

    [Fact]
    public void Block_opensWithItsHeading_andCarriesTheAnchoringGuidance()
    {
        var block = CurrentTimeContext.Describe(Utc("2026-08-14T10:46:00Z"), Zurich);

        Assert.StartsWith(CurrentTimeContext.Heading, block, StringComparison.Ordinal);
        // Ends on a blank line so it cannot run into the next context block. Compared line-ending
        // agnostically: the raw string literal picks up whatever the checkout wrote.
        Assert.EndsWith("\n\n", block.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("against the user's LOCAL date above", block, StringComparison.Ordinal);
        Assert.Contains("Never answer the date from memory", block, StringComparison.Ordinal);
        Assert.Contains("ISO-8601", block, StringComparison.Ordinal);
        // Calendar facts are NOT instants — converting one shifts data and corrupts joins.
        Assert.Contains("Do NOT convert dates that are not instants", block, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportedIssue_wouldNowBeAnswerable()
    {
        // #1651 verbatim: the round was submitted 2026-08-14T10:46Z — a FRIDAY — and the agent
        // answered "Mittwoch, der 6. August 2026". With the block in context the correct answer is
        // sitting in the prompt, in the submitter's own zone.
        var block = CurrentTimeContext.Describe(Utc("2026-08-14T10:46:00Z"), Zurich);

        Assert.Contains("Friday, 2026-08-14", block, StringComparison.Ordinal);
        Assert.Contains("12:46 (Europe/Zurich, UTC+02:00)", block, StringComparison.Ordinal);
    }
}
