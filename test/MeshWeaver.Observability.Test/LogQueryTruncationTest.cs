using MeshWeaver.LogWatcher;
using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// 🚨 <b>5000 IS A CAP, NOT A COUNT.</b>
///
/// <para>On 2026-08-17 the watcher logged <c>"memex-cloud: 1 distinct fingerprint(s) from 5000 red
/// line(s)"</c> for several consecutive windows. Exactly 5000 every time, because that is
/// <c>QueryLimit</c> — the window was never fully read. The truncation was handled correctly on the
/// cursor (it resumes at the last line actually seen, so the remainder is deferred rather than
/// dropped) and reported NOWHERE a verdict is read: one <c>LogWarning</c> in the watcher's own pod
/// log, which is precisely the thing this subsystem exists to stop humans from having to grep.</para>
///
/// <para>These tests pin the two conditions as reportable facts. Raising the limit is not the fix
/// and never was: while one namespace out-talks its watcher, a dominant noisy source crowds every
/// quieter error out of the prefix that gets read, and that is the case where the quiet defect is
/// the one that matters.</para>
/// </summary>
public class LogQueryTruncationTest
{
    private static readonly DateTimeOffset Start = new(2026, 8, 17, 19, 27, 30, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 8, 17, 19, 28, 37, TimeSpan.Zero);

    /// <summary>At the limit the window was not fully read — the measured 5000/5000 shape.</summary>
    [Fact]
    public void AtTheQueryLimit_IsTruncated()
    {
        LogPipelineGap.IsTruncated(5000, 5000).Should().BeTrue(
            "Loki stops at the limit, so a result AT the limit is a prefix of the window, not the "
            + "whole of it");
        LogPipelineGap.IsTruncated(4752, 5000).Should().BeFalse(
            "4752 of a possible 5000 is the whole window — the same production log shows both, and "
            + "only one of them is a truncation");
    }

    /// <summary>A limit of zero means "unbounded"; it must not report every window as truncated.</summary>
    [Fact]
    public void NoLimit_IsNeverTruncated()
    {
        LogPipelineGap.IsTruncated(9_999, 0).Should().BeFalse();
    }

    /// <summary>
    /// The report has to name the backlog, because the backlog is the actionable number: a backlog
    /// that keeps growing ends at the <c>MaxCatchUp</c> floor, where the skipped stretch IS lost.
    /// </summary>
    [Fact]
    public void TruncatedReport_NamesTheLimitAndTheBacklog()
    {
        // The read got as far as 19:27:52 — 45 s of the window never came back.
        var reached = Start.AddSeconds(22);
        var report = LogPipelineGap.TruncatedReport("memex-cloud", Start, End, reached, 5000);

        report.Severity.Should().Be(LogSeverity.Error);
        report.Namespace.Should().Be("memex-cloud");
        report.NormalizedMessage.Should().Contain("5000")
            .And.Contain("NOT fully read");
        report.NormalizedMessage.Should().Contain("raising the limit only moves the cap",
            "the ticket has to say what the fix is NOT, or the first responder raises QueryLimit");
        report.Samples.Should().ContainSingle().Which.Line.Should().Contain("45s backlog");
    }

    /// <summary>
    /// Per-namespace and timestamp-free, like every other judgement the watcher makes about its own
    /// input: a namespace that truncates every minute is one rising incident, not one per minute.
    /// </summary>
    [Fact]
    public void TruncatedReport_DedupsPerNamespace()
    {
        var first = LogPipelineGap.TruncatedReport("memex-cloud", Start, End, Start.AddSeconds(20), 5000);
        var later = LogPipelineGap.TruncatedReport("memex-cloud", End, End.AddMinutes(1), End, 5000);

        later.Fingerprint.Should().Be(first.Fingerprint);
        LogPipelineGap.TruncatedReport("memex", Start, End, Start, 5000).Fingerprint
            .Should().NotBe(first.Fingerprint, "a different namespace out-talks its watcher for its "
                                               + "own reasons");
        first.Fingerprint.Should().NotBe(LogPipelineGap.Report("memex-cloud", Start, End).Fingerprint,
            "a truncated window and a lost window are different findings with different fixes");
    }

    /// <summary>
    /// The one path that genuinely LOSES evidence: a cursor older than <c>MaxCatchUp</c> is dragged
    /// forward and the skipped stretch is never read. It used to exist only as a <c>LogWarning</c>.
    /// </summary>
    [Fact]
    public void SkippedWindowReport_IsCritical_AndNamesTheStretch()
    {
        var oldCursor = new DateTimeOffset(2026, 8, 17, 6, 0, 0, TimeSpan.Zero);
        var floor = new DateTimeOffset(2026, 8, 17, 13, 30, 0, TimeSpan.Zero);

        var report = LogPipelineGap.SkippedWindowReport("memex-cloud", oldCursor, floor);

        report.Severity.Should().Be(LogSeverity.Critical,
            "this stretch is unrecoverable — unlike a truncation, no later poll will read it");
        report.NormalizedMessage.Should().Contain("never ticketed");
        report.Samples.Should().ContainSingle().Which.Line.Should().Contain("450 minute");
        report.Fingerprint.Should()
            .NotBe(LogPipelineGap.TruncatedReport("memex-cloud", oldCursor, floor, floor, 5000).Fingerprint);
    }
}
