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
/// <para>What makes this decidable without a heuristic is the WINDOW'S LENGTH, and that is what these
/// tests pin. The Loki query is UNFILTERED (see <c>LokiQueryShapeTest</c>), so a running portal cannot
/// legitimately return nothing — but in steady state the window is only about one poll interval wide,
/// and a namespace with nothing deployed does legitimately return zero for those. A LONG window
/// exists only because the cursor could not advance, i.e. every poll during an outage threw.</para>
/// </summary>
public class LogPipelineGapTest
{
    private static readonly TimeSpan Alarm = TimeSpan.FromMinutes(5);

    /// <summary>The 2026-08-12 shape: a long window, nothing in it.</summary>
    [Fact]
    public void LongEmptyWindow_IsLost()
    {
        LogPipelineGap.IsLostWindow(0, TimeSpan.FromMinutes(51), Alarm).Should().BeTrue(
            "a window that long only exists because the cursor could not advance — an unfiltered "
            + "query returning nothing for it means the store cannot show that stretch");
    }

    /// <summary>Steady state: a quiet minute is just a quiet minute.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ShortEmptyWindow_IsNotLost(int minutes)
    {
        LogPipelineGap.IsLostWindow(0, TimeSpan.FromMinutes(minutes), Alarm).Should().BeFalse(
            "in steady state the window is about one poll wide, and a namespace with nothing "
            + "deployed returns zero for those — ticketing it would cry wolf every minute");
    }

    /// <summary>A long window that DID return lines is a normal catch-up, not a loss.</summary>
    [Fact]
    public void LongWindowWithLines_IsNotLost()
    {
        LogPipelineGap.IsLostWindow(1, TimeSpan.FromHours(6), Alarm).Should().BeFalse(
            "the store answered — a long window is exactly what catching up after downtime looks "
            + "like, and one line is proof the stretch is readable");
    }

    /// <summary>Exactly at the threshold counts — the bound is inclusive, so it cannot be skipped.</summary>
    [Fact]
    public void AtTheThreshold_IsLost()
    {
        LogPipelineGap.IsLostWindow(0, Alarm, Alarm).Should().BeTrue();
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
