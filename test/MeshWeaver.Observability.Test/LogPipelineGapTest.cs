using System;
using MeshWeaver.LogWatcher;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// 🚨 SILENCE FROM THE LOG STORE IS NOT GOOD NEWS.
///
/// <para>The watcher's outage handling was already half-right: while Loki is unreachable the query
/// throws, the pass logs loudly, and the cursor does not advance — so the window is re-read. The hole
/// was the RECOVERY. Loki came back on 2026-08-12 with an EMPTY store (it had been evicted, and its
/// store was an <c>emptyDir</c>), the re-read of the outage window returned zero lines, the cursor
/// advanced, and the watcher reported nothing. The incident that destroyed the evidence was itself
/// never ticketed — and `mw-log-watcher` reads FROM Loki, so it was blind for exactly as long as it
/// mattered.</para>
///
/// <para>What makes this decidable without a heuristic is the window's LENGTH plus whether we were
/// CONTINUOUSLY WATCHING it, and that pair is what these tests pin. The Loki query is UNFILTERED (see
/// <c>LokiQueryShapeTest</c>), so a running portal cannot legitimately return nothing — but in steady
/// state the window is only about one poll interval wide, and a namespace with nothing deployed does
/// legitimately return zero for those. Long windows arise three ways and only one is evidence: the
/// cursor could not advance because every poll threw (we watched, the store lost it) versus a cold
/// start's synthesised lookback or a cursor floored after watcher downtime (we were never there).</para>
/// </summary>
public class LogPipelineGapTest
{
    private static readonly TimeSpan Alarm = TimeSpan.FromMinutes(5);

    /// <summary>The 2026-08-12 shape: a long window, nothing in it.</summary>
    [Fact]
    public void LongEmptyWindow_IsLost()
    {
        LogPipelineGap.IsLostWindow(0, TimeSpan.FromMinutes(51), Alarm, continuouslyWatched: true).Should().BeTrue(
            "a window that long only exists because the cursor could not advance — an unfiltered "
            + "query returning nothing for it means the store cannot show that stretch");
    }

    /// <summary>Steady state: a quiet minute is just a quiet minute.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ShortEmptyWindow_IsNotLost(int minutes)
    {
        LogPipelineGap.IsLostWindow(0, TimeSpan.FromMinutes(minutes), Alarm, continuouslyWatched: true).Should().BeFalse(
            "in steady state the window is about one poll wide, and a namespace with nothing "
            + "deployed returns zero for those — ticketing it would cry wolf every minute");
    }

    /// <summary>A long window that DID return lines is a normal catch-up, not a loss.</summary>
    [Fact]
    public void LongWindowWithLines_IsNotLost()
    {
        LogPipelineGap.IsLostWindow(1, TimeSpan.FromHours(6), Alarm, continuouslyWatched: true).Should().BeFalse(
            "the store answered — a long window is exactly what catching up after downtime looks "
            + "like, and one line is proof the stretch is readable");
    }

    /// <summary>Exactly at the threshold counts — the bound is inclusive, so it cannot be skipped.</summary>
    [Fact]
    public void AtTheThreshold_IsLost()
    {
        LogPipelineGap.IsLostWindow(0, Alarm, Alarm, continuouslyWatched: true).Should().BeTrue();
    }


    /// <summary>
    /// 🚨 THE FALSE POSITIVE THIS NEARLY SHIPPED WITH (Copilot review, #1265). A long window is not
    /// by itself evidence: <c>CursorFor</c> synthesises one <c>ColdStartLookback</c> wide (15 min —
    /// already past the 5 min alarm) when there is no persisted cursor, and floors a stale one to
    /// <c>MaxCatchUp</c> after watcher downtime. In both the watcher simply was not there, so
    /// emptiness says nothing about the store — a fresh watcher pointed at a quiet namespace would
    /// otherwise file a bogus "lost window" incident on its very first tick.
    /// </summary>
    [Fact]
    public void LongEmptyWindow_WeWereNotWatching_IsNotLost()
    {
        LogPipelineGap.IsLostWindow(0, TimeSpan.FromMinutes(15), Alarm, continuouslyWatched: false)
            .Should().BeFalse(
                "a cold start's synthesised lookback window is wider than the alarm threshold, but "
                + "we were not polling that stretch — its emptiness is not evidence of loss");

        LogPipelineGap.IsLostWindow(0, TimeSpan.FromHours(6), Alarm, continuouslyWatched: false)
            .Should().BeFalse(
                "a cursor floored by MaxCatchUp is precisely the case where we KNOW we were not "
                + "watching — the skipped window is already reported by CursorFor's own warning");
    }

    /// <summary>
    /// The report's identity must be per-NAMESPACE and free of timestamps: repeats are the same
    /// incident (this namespace's pipeline is losing windows), so they dedup into one node that counts
    /// occurrences instead of one node per outage.
    /// </summary>
    [Fact]
    public void Report_DedupsPerNamespace_AndCarriesTheWindow()
    {
        var start = new DateTimeOffset(2026, 8, 12, 4, 45, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 8, 12, 5, 36, 0, TimeSpan.Zero);

        var first = LogPipelineGap.Report("memex-cloud", start, end);
        var later = LogPipelineGap.Report("memex-cloud", end, end.AddHours(1));

        first.Fingerprint.Should().Be(later.Fingerprint,
            "a second lost window in the same namespace is the SAME incident — one node, rising "
            + "occurrence count, not a node per outage");
        first.Fingerprint.Should().NotBe(LogPipelineGap.Report("memex", start, end).Fingerprint,
            "…but a different namespace is a different pipeline");

        first.Severity.Should().Be(LogSeverity.Critical);
        first.FirstSeen.Should().Be(start);
        first.LastSeen.Should().Be(end);
        first.Samples.Should().ContainSingle().Which.Line.Should().Contain("51 minute",
            "the evidence line has to name the window that was lost — that is the whole finding");
    }
}
