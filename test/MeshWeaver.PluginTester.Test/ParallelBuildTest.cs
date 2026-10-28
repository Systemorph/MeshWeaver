using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Messaging;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// <c>mw-plugin-test build</c> compiles the NodeTypes INSIDE a package in parallel, through one
/// <see cref="WorkGate"/> shared by the whole build. Three claims carry that change, each with a
/// negative control:
/// <list type="number">
///   <item>the gate's cap holds ACROSS callers — two packages handing it work never run more than
///     the cap between them — and it really runs items side by side (the same work through a
///     one-slot gate never overlaps);</item>
///   <item>results come back in INPUT order whatever order they finished in, and one failing item
///     neither wedges the gate nor hides the others;</item>
///   <item>a parallel build reports what a serial one reports — every type's verdict, every log
///     line (times masked), every bundle byte (per-emit fields masked, as the bake's own
///     equivalence test does) — and a tree with one changed source IS seen as different.</item>
/// </list>
/// </summary>
public partial class ParallelBakeTest
{
    // ── 1. the gate's cap is shared, and it is parallel ────────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task WorkGate_CapHoldsAcrossCallers_AndItemsRunSideBySide()
    {
        var ct = TestContext.Current.CancellationToken;

        var (peak, _) = await PeakConcurrency(gateSlots: 3, callers: 2, itemsPerCaller: 4, ct);
        Output.WriteLine($"3-slot gate, 2 callers × 4 items: peak {peak}");
        Assert.Equal(3, peak); // never above the cap although two callers each had 4 items ready

        // NEGATIVE CONTROL — the same work through one slot never overlaps, so a peak above 1 is
        // the gate running items side by side, not an artefact of how the peak is measured.
        var (serialPeak, _) = await PeakConcurrency(gateSlots: 1, callers: 2, itemsPerCaller: 4, ct);
        Output.WriteLine($"1-slot gate: peak {serialPeak}");
        Assert.Equal(1, serialPeak);
    }

    /// <summary>
    /// Every item parks until <c>gateSlots</c> items are running at once (or a bound lapses — the
    /// one-slot case, where that can never happen), so the peak is the cap whenever the gate lets
    /// that many through, independent of how fast the pool dispatches.
    /// </summary>
    private static async Task<(int Peak, ImmutableArray<ImmutableArray<int>> Results)> PeakConcurrency(
        int gateSlots, int callers, int itemsPerCaller, CancellationToken ct)
    {
        var running = 0;
        var peak = 0;
        using var gate = new WorkGate(gateSlots);
        Func<int> Item(int value) => () =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            try
            {
                SpinWait.SpinUntil(() => Volatile.Read(ref running) >= gateSlots, gateSlots == 1 ? TimeSpan.FromMilliseconds(20) : SignalBound);
                // Hold the slot a moment longer so a gate that over-admits would be caught over the cap.
                SpinWait.SpinUntil(() => false, TimeSpan.FromMilliseconds(30));
                return value;
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        };
        var runs = Enumerable.Range(0, callers)
            .Select(c => gate.Run(Enumerable.Range(0, itemsPerCaller).Select(i => Item((c * 100) + i)).ToArray()))
            .ToArray();
        var results = await runs.Merge().ToList().Await(ct);
        return (Volatile.Read(ref peak), [.. results]);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        for (var seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
                return;
        }
    }

    // ── 2. input order, and a failure is that item's alone ─────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task WorkGate_ResultsComeBackInInputOrder_WhateverOrderTheyFinishedIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var finished = ImmutableList<int>.Empty;
        using var gate = new WorkGate(4);
        // Item i waits until every LATER item has finished, so they finish in reverse order.
        Func<int> Item(int i) => () =>
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref finished).Count(f => f > i) == 3 - i, SignalBound);
            ImmutableInterlocked.Update(ref finished, l => l.Add(i));
            return i;
        };

        var results = await gate.Run<int>([Item(0), Item(1), Item(2), Item(3)]).Await(ct);

        Assert.Equal(new[] { 3, 2, 1, 0 }, Volatile.Read(ref finished)); // the finishing order really was reversed
        Assert.Equal(new[] { 0, 1, 2, 3 }, results);                    // and the result is in input order
        Assert.Empty(await gate.Run(Array.Empty<Func<int>>()).Await(ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task WorkGate_AFailingItemFailsItsOwnRun_AndTheGateKeepsServing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var gate = new WorkGate(1);

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.Run<int>([() => 1, () => throw new InvalidOperationException("item 2 broke"), () => 3]).Await(ct));
        Assert.Equal("item 2 broke", failed.Message);

        // The one slot was freed by the failure: the next caller is served, not wedged.
        Assert.Equal(new[] { 7, 8 }, await gate.Run<int>([() => 7, () => 8]).Await(ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task Cascade_RunObservable_HoldsTheSlotUntilTheStreamYields_AndAnEmptyOrFailedStreamIsFaulted()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = await Cascade.RunObservable<string>(
            ["Ok", "Red", "Empty", "Error", "AfterOk"],
            id => id == "AfterOk" ? ["Ok"] : [],
            (id, _) => id switch
            {
                "Ok" => Observable.Timer(TimeSpan.FromMilliseconds(50)).Select(_ => ("ok", true)),
                "Red" => Observable.Return(("red", false)),
                "Empty" => Observable.Empty<(string, bool)>(),
                "Error" => Observable.Throw<(string, bool)>(new InvalidOperationException("boom")),
                _ => Observable.Return(("after", true)),
            },
            maxParallel: 2).Await(ct);

        var byId = results.ToDictionary(r => r.Id, StringComparer.Ordinal);
        Assert.Equal(Cascade.NodeOutcome.Green, byId["Ok"].Outcome);
        Assert.True(byId["Ok"].Work >= TimeSpan.FromMilliseconds(40), "the node's work spans its stream, not just the call that returned it");
        Assert.True(byId["AfterOk"].Started >= byId["Ok"].Finished, "a dependent starts only after the stream yielded");
        Assert.Equal(Cascade.NodeOutcome.Red, byId["Red"].Outcome);
        Assert.Equal(Cascade.NodeOutcome.Faulted, byId["Empty"].Outcome);
        Assert.Contains("without a result", byId["Empty"].Error);
        Assert.Equal(Cascade.NodeOutcome.Faulted, byId["Error"].Outcome);
        Assert.Equal("InvalidOperationException: boom", byId["Error"].Error);
    }

    // ── 3. a parallel build reports what a serial build reports ────────────────────────────────

    [Fact(Timeout = 600_000)]
    public async Task AParallelBuildReportsTheSerialBuild_AndADifferentSourceIsSeen()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = TempDirectory("mw-pbuild-repo");
        var changedRepo = TempDirectory("mw-pbuild-repo-changed");
        var serialOut = TempDirectory("mw-pbuild-serial");
        var serialAgainOut = TempDirectory("mw-pbuild-serial-again");
        var parallelOut = TempDirectory("mw-pbuild-parallel");
        var changedOut = TempDirectory("mw-pbuild-changed");
        try
        {
            WriteTree(repo);
            WriteTree(changedRepo, leafSevenBody: "    /// <summary>A field only this tree has.</summary>\n    public int Extra { get; init; }");

            var serial = await Build(repo, serialOut, maxParallel: 1, ct);
            var serialAgain = await Build(repo, serialAgainOut, maxParallel: 1, ct);
            var parallel = await Build(repo, parallelOut, maxParallel: 6, ct);

            // The build really happened, with every shape present: 11 types attempted, one RED.
            Assert.Null(serial.Report.FatalError);
            var types = serial.Report.Packages.Single().Result!.Types;
            Assert.Equal(11, types.Length);
            Assert.Equal(new[] { "Ctrl/Broken" }, types.Where(t => t.CompileError is not null).Select(t => t.NodePath));
            Assert.Contains("max-parallel=6", parallel.Log);

            // THE BASELINE: two serial builds agree once the per-emit fields are masked.
            var serialDrift = DifferingFiles(serialOut, serialAgainOut);
            Assert.True(serialDrift.IsEmpty, "two serial builds differ beyond the per-emit fields: " + string.Join(", ", serialDrift));

            Assert.Equal(BuildVerdicts(serial.Report), BuildVerdicts(serialAgain.Report));
            Assert.Equal(BuildVerdicts(serial.Report), BuildVerdicts(parallel.Report));
            Assert.Equal(serial.Report.Warnings.Pairs, parallel.Report.Warnings.Pairs);
            Assert.Equal(serial.Report.ExitCode, parallel.Report.ExitCode);
            Assert.Equal(BuildLogLines(serial.Log, serialOut), BuildLogLines(parallel.Log, parallelOut));
            var differences = DifferingFiles(serialOut, parallelOut);
            Assert.True(differences.IsEmpty, "the parallel build wrote different output: " + string.Join(", ", differences));

            // NEGATIVE CONTROL — a build whose Leaf7 source differs must be SEEN as different, in that
            // bundle entry and no other.
            await Build(changedRepo, changedOut, maxParallel: 6, ct);
            var changed = DifferingFiles(serialOut, changedOut);
            Output.WriteLine("changed tree differs in: " + string.Join(", ", changed));
            Assert.Contains("Ctrl.zip!meshweaver/assemblies/Ctrl/Leaf7.dll", changed);
            Assert.DoesNotContain("Ctrl.zip!meshweaver/assemblies/Ctrl/Leaf6.dll", changed);
        }
        finally
        {
            foreach (var d in new[] { repo, changedRepo, serialOut, serialAgainOut, parallelOut, changedOut })
                Cleanup(d);
        }
    }

    private sealed record BuildRun(CascadeBuild.Report Report, string Log);

    private async Task<BuildRun> Build(string repo, string outDir, int maxParallel, CancellationToken ct)
    {
        var log = new StringWriter();
        var report = await CascadeBuild.Run(new CascadeBuild.Options
        {
            RepoRoot = repo,
            OutputDirectory = outDir,
            SourceSha = "deadbeef",
            Output = log,
            MaxParallel = maxParallel,
            RunTests = false,
        }).Await(ct);
        Output.WriteLine($"── build max-parallel={maxParallel}\n{log}");
        return new BuildRun(report, log.ToString());
    }

    private static ImmutableArray<string> BuildVerdicts(CascadeBuild.Report report) =>
    [
        .. report.Packages.Select(p => $"{p.Id}|{p.Outcome}|{p.BlockedBy}|{p.Error}"),
        .. report.Packages.Where(p => p.Result is not null).SelectMany(p => p.Result!.Types).Select(t =>
            $"{t.NodePath}|{t.Package}|{t.CompileError}|{t.SourceCount}|{t.DllPath is not null}|{t.Warnings.Length}"),
    ];

    /// <summary>The log with every time, thread id, slot count and per-run directory masked, and
    /// the timing table dropped — what is left must be identical line for line.</summary>
    private static ImmutableArray<string> BuildLogLines(string log, string outDir) =>
    [
        .. log.Split('\n')
            .Select(l => Stamp().Replace(l, ""))
            .Select(l => l.Replace(outDir, "<out>", StringComparison.Ordinal))
            .Select(l => WorkDir().Replace(l, "<work>"))
            .Select(l => Duration().Replace(l, "<t>"))
            .Select(l => MaxParallelSetting().Replace(l, "max-parallel=<p>"))
            // The per-package timing table: header and rows carry bare seconds columns.
            .Where(l => !l.StartsWith("package ", StringComparison.Ordinal) && !l.StartsWith("Ctrl ", StringComparison.Ordinal)),
    ];

    [GeneratedRegex(@"^\d\d:\d\d:\d\d\.\d{3} \[T\d+\] ")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"mw-cascade-build-[^/\\\s]+")]
    private static partial Regex WorkDir();

    [GeneratedRegex(@"\d+(\.\d+)?\s?(ms|s|×)\b|\d+(\.\d+)?×")]
    private static partial Regex Duration();

    [GeneratedRegex(@"max-parallel=\d+")]
    private static partial Regex MaxParallelSetting();
}
