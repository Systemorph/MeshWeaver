#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>#3704 — the discriminating measurement, and it has to be DELIVERED.</b>
///
/// <para>On 2026-09-08 00:31:22Z one boot of memex resolved a source-discovery set <b>91 Code nodes
/// short</b> (1145 against 1236 / 1237 / 1241 on the same portal, three of them on the same image),
/// and content grew monotonically across that window — a truncation, not a smaller mesh. Two
/// mechanisms could produce it and nothing recorded enough to tell them apart: the fold's
/// completion rule (a one-second quiet window, which is the ONLY completion signal
/// <c>QueryResultChange&lt;T&gt;</c> offers a reader — the protocol carries no terminal marker), or
/// an upstream shortfall in what the providers returned.</para>
///
/// <para>The largest inter-chunk gap separates them: approaching the window indicts the rule; all
/// gaps small exonerates it and moves the search upstream. <c>NodeTypeBatchBake.ChunkTiming</c>
/// records it, and this suite pins the rule as a truth table with an injected clock — a test that
/// slept through a real half-second gap would measure the runner as much as the rule.</para>
///
/// <para>🚨 Every case asserts the LOG LINE, never a return value. A verdict nothing emits is worth
/// exactly what the silence it replaced was worth — the lesson of #2553 and #3625, one diagnostic
/// over, in this same repository.</para>
/// </summary>
public class SourceDiscoveryChunkTimingTest
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>Ticks for <paramref name="ms"/> on this machine's Stopwatch scale.</summary>
    private static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    /// <summary>
    /// The measurement itself: chunk count, items delivered and the per-chunk shape all reach the
    /// line. Without the shape a reader cannot tell "one big answer" from "forty dribbles", which
    /// is the difference between the two mechanisms.
    /// </summary>
    [Fact]
    public void TheReportCarriesTheChunkCountTheItemTotalAndTheShape()
    {
        var log = new Recorder();
        var clock = new FakeClock();
        var timing = new NodeTypeBatchBake.ChunkTiming(clock.Now);

        clock.Advance(0);
        timing.Observe(Change(3));
        clock.Advance(10);
        timing.Observe(Change(4));
        clock.Advance(10);
        timing.Observe(Change(2));

        timing.Report(log, "nodeType:Code", settled: 9, Window);

        var line = log.Single(LogLevel.Information);
        line.Should().Contain("3 change(s)", "the number of folded changes is half the shape");
        line.Should().Contain("9 item(s) delivered");
        line.Should().Contain("[3,4,2]", "the per-chunk counts are what distinguish one answer from many");
        line.Should().Contain("settled at 9 node(s)");
        line.Should().Contain("nodeType:Code", "a reader has to know WHICH of the three global fetches this was");
    }

    /// <summary>
    /// 🚨 The acceptance criterion, indicting side: a gap that is a large share of the completion
    /// window is the mechanism #3704 names, and it is said at Warning — the level an operator's
    /// filter actually keeps.
    /// </summary>
    [Fact]
    public void AGapNearTheCompletionWindow_IsReportedAsTheNamedMechanism()
    {
        var log = new Recorder();
        var clock = new FakeClock();
        var timing = new NodeTypeBatchBake.ChunkTiming(clock.Now);

        timing.Observe(Change(500));
        clock.Advance(900);           // 90% of the 1000 ms window
        timing.Observe(Change(645));

        timing.Report(log, "nodeType:Code", settled: 1145, Window);

        log.Single(LogLevel.Information).Should().Contain("900ms = 90%");
        var warning = log.Single(LogLevel.Warning);
        warning.Should().Contain("900ms");
        warning.Should().Contain("#3704", "the line must carry the issue that explains it");
        warning.Should().Contain("1145 node(s)",
            "the settled count is what a reader compares against the neighbouring boots");
    }

    /// <summary>
    /// 🚨 The control, and the half that keeps the diagnostic honest: small gaps must NOT be
    /// reported as the mechanism. Without this, a rule that warned unconditionally would satisfy
    /// the test above while telling every reader the same thing on every pass — which is the same
    /// as telling them nothing.
    /// </summary>
    [Fact]
    public void SmallGaps_AreMeasuredAndExonerateTheCompletionRule()
    {
        var log = new Recorder();
        var clock = new FakeClock();
        var timing = new NodeTypeBatchBake.ChunkTiming(clock.Now);

        timing.Observe(Change(600));
        clock.Advance(12);
        timing.Observe(Change(636));

        timing.Report(log, "nodeType:Code", settled: 1236, Window);

        log.Single(LogLevel.Information).Should().Contain("12ms = 1%",
            "the number is printed on EVERY pass — a reader never depends on where the threshold sits");
        log.Lines(LogLevel.Warning).Should().BeEmpty(
            "all gaps small means the completion rule did not end this fold early, so the search "
            + "for a short read moves upstream to what the providers returned");
    }

    /// <summary>
    /// The FIRST change has no predecessor, so the interval before it is not a gap. Counting it
    /// would make every cold query — where the first answer legitimately takes hundreds of
    /// milliseconds to arrive — indict the completion rule, and the diagnostic would be noise from
    /// its first day.
    /// </summary>
    [Fact]
    public void TheWaitForTheFIRSTChangeIsNotAnInterChunkGap()
    {
        var log = new Recorder();
        var clock = new FakeClock();
        var timing = new NodeTypeBatchBake.ChunkTiming(clock.Now);

        clock.Advance(4000);          // four seconds of cold start before anything answers
        timing.Observe(Change(1236));

        timing.Report(log, "nodeType:Code", settled: 1236, Window);

        log.Single(LogLevel.Information).Should().Contain("0ms = 0%",
            "one change means no INTERVAL between changes; the wait for the first answer is the "
            + "query's latency, not evidence about the fold's completion rule");
        log.Lines(LogLevel.Warning).Should().BeEmpty();
    }

    /// <summary>A pass with more chunks than the line will print says so, and keeps the two figures
    /// the verdict rests on — the count and the largest gap.</summary>
    [Fact]
    public void AVeryChunkyPass_TruncatesTheShapeAndSaysSo()
    {
        var log = new Recorder();
        var clock = new FakeClock();
        var timing = new NodeTypeBatchBake.ChunkTiming(clock.Now);

        for (var i = 0; i < 60; i++)
        {
            clock.Advance(5);
            timing.Observe(Change(1));
        }

        timing.Report(log, "nodeType:Code", settled: 60, Window);

        var line = log.Single(LogLevel.Information);
        line.Should().Contain("60 change(s)");
        line.Should().Contain("more)", "the shape is capped, and a capped shape must announce itself");
    }

    private static QueryResultChange<MeshNode> Change(int count) =>
        new()
        {
            ChangeType = QueryChangeType.Initial,
            Items = [.. Enumerable.Range(0, count).Select(i => new MeshNode($"n{i}", "p"))],
        };

    /// <summary>A monotonic tick source the test advances by hand. No timer, no delay, no gate.</summary>
    private sealed class FakeClock
    {
        private long ticks;

        public long Now() => Volatile.Read(ref ticks);

        public void Advance(double ms) => Interlocked.Add(ref ticks, Ticks(ms));
    }

    /// <summary>Captures what was actually logged, which is the property under test.</summary>
    private sealed class Recorder : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue((logLevel, formatter(state, exception)));

        public IReadOnlyList<string> Lines(LogLevel level) =>
            [.. lines.Where(l => l.Level == level).Select(l => l.Message)];

        public string Single(LogLevel level)
        {
            var found = Lines(level);
            found.Count.Should().Be(1,
                $"exactly one {level} line is expected; a diagnostic that fires zero times has "
                + "asserted nothing and one that fires twice is noise. Captured: "
                + string.Join(" | ", found));
            return found[0];
        }
    }
}
