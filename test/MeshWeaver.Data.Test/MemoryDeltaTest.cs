using System;
using System.Runtime;
using MeshWeaver.Mesh.Diagnostics;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the two properties that make <see cref="MemoryDelta"/> usable in a log line: it must never
/// provoke a collection (it is called on hot-ish paths and inside a bake), and its rendering must be
/// SIGNED — a negative managed delta beside a positive working set is the signature of memory that
/// left the managed heap and did not leave the process, which is precisely the shape the memex-cloud
/// growth turned out to have (RSS 23.6 GB with the managed heap far smaller).
/// </summary>
[Collection(nameof(GcSensitive))]
public class MemoryDeltaTest
{
    /// <summary>
    /// It must read the heap WITHOUT forcing a collection. `GC.GetTotalMemory(true)` would make this
    /// probe change the thing it measures and add a full GC to every import and bake.
    /// </summary>
    [Fact]
    public void Start_DoesNotForceACollection()
    {
        var collected = FullCollectionsDuring(() =>
        {
            var probe = MemoryDelta.Start();
            _ = probe.ManagedGrowth;
            _ = probe.WorkingSetGrowth;
            _ = probe.ToString();
        });

        collected.Should().Be(0L,
            "the probe must observe memory, never collect it — otherwise measuring an import would "
            + "itself cost a full gen-2 GC on every partition");
    }

    /// <summary>
    /// The positive control for the instrument above: a forced collection inside the measured window
    /// IS counted. Without it, a <see cref="FullCollectionsDuring"/> that could never see a
    /// collection would pass <see cref="Start_DoesNotForceACollection"/> having checked nothing.
    /// </summary>
    [Fact]
    public void TheInstrument_SeesAForcedCollection()
    {
        FullCollectionsDuring(() => GC.Collect()).Should().BeGreaterThan(0L,
            "a GC.Collect() inside the window must be counted, or the probe's zero proves nothing");
    }

    // Room for everything the window allocates (a few small strings) many times over.
    private const long NoGcBudget = 16L * 1024 * 1024;

    /// <summary>
    /// 🚨 Gen-2 collections that happened while <paramref name="action"/> ran — and ONLY those it
    /// caused. <c>GC.CollectionCount</c> is PROCESS-wide, so a bare before/after read also counts a
    /// collection the runtime ran for anyone else in that instant; that is how this test failed a
    /// merge-queue group with "expected 1 … but found 2" over a probe that forces nothing (core
    /// run 34999148527, 2026-09-15). Two things take everyone else out of the window: the class runs
    /// ALONE (<see cref="GcSensitive"/>), and the window is a no-GC region, in which the runtime
    /// collects only when something INDUCES it. What is left to count is the action.
    /// </summary>
    private static long FullCollectionsDuring(Action action)
    {
        Assert.True(GC.TryStartNoGCRegion(NoGcBudget),
            "the runtime could not reserve a no-GC region, so this window cannot be measured");
        var before = GC.CollectionCount(GC.MaxGeneration);
        try
        {
            action();
            return GC.CollectionCount(GC.MaxGeneration) - before;
        }
        finally
        {
            // An induced collection ends the region on its own; EndNoGCRegion would then throw.
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                GC.EndNoGCRegion();
        }
    }

    /// <summary>Growth is measured from the captured start, so a real allocation shows up.</summary>
    [Fact]
    public void ManagedGrowth_CountsAllocationsSinceStart()
    {
        var probe = MemoryDelta.Start();

        // ~8 MB, held live across the measurement so no collection can hide it.
        var held = new byte[8 * 1024 * 1024];
        held[0] = 1;
        var grown = probe.ManagedGrowth;

        grown.Should().BeGreaterThan(4 * 1024 * 1024,
            "an 8 MB live allocation must be visible; a probe that cannot see one is not worth the "
            + "log line");
        GC.KeepAlive(held);
    }

    /// <summary>
    /// The rendering must carry SIGNS and name both figures — the whole diagnostic value is in
    /// comparing them, and an unsigned number silently reads as growth when it may be a release.
    /// </summary>
    [Fact]
    public void ToString_IsSignedAndNamesBothFigures()
    {
        var rendered = MemoryDelta.Start().ToString();

        rendered.Should().Contain("managed");
        rendered.Should().Contain("working set");
        rendered.Should().Contain("MB");
        rendered.Should().MatchRegex(@"managed [+-]?\d+ MB, working set [+-]?\d+ MB");

        // A delta under 1 MB must render a bare "0", never "-0" — a sign on a zero reads as a
        // measurement rather than as rounding. (Copilot review, #1321.)
        new MemoryDelta(GC.GetTotalMemory(false) + 512 * 1024, Environment.WorkingSet + 512 * 1024)
            .ToString().Should().NotContain("-0 MB");
    }

    /// <summary>
    /// A delta taken against a HIGHER start renders negative rather than wrapping or throwing — the
    /// release case has to be legible, since "managed went down while working set went up" is the
    /// finding that distinguishes a native/unreturned-to-OS problem from a managed leak.
    /// </summary>
    [Fact]
    public void NegativeGrowth_RendersWithAMinus()
    {
        var impossiblyHighStart = new MemoryDelta(long.MaxValue / 2, long.MaxValue / 2);

        impossiblyHighStart.ManagedGrowth.Should().BeLessThan(0);
        impossiblyHighStart.ToString().Should().MatchRegex(@"managed -\d+ MB, working set -\d+ MB");
    }
}

/// <summary>
/// Tests that measure the PROCESS-wide GC run alone: a collection another test causes in the same
/// instant is otherwise indistinguishable from one the code under test caused.
/// </summary>
[CollectionDefinition(nameof(GcSensitive), DisableParallelization = true)]
public sealed class GcSensitive;
