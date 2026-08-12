using System;
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
public class MemoryDeltaTest
{
    /// <summary>
    /// It must read the heap WITHOUT forcing a collection. `GC.GetTotalMemory(true)` would make this
    /// probe change the thing it measures and add a full GC to every import and bake.
    /// </summary>
    [Fact]
    public void Start_DoesNotForceACollection()
    {
        var before = GC.CollectionCount(GC.MaxGeneration);

        var probe = MemoryDelta.Start();
        _ = probe.ManagedGrowth;
        _ = probe.WorkingSetGrowth;
        _ = probe.ToString();

        GC.CollectionCount(GC.MaxGeneration).Should().Be(before,
            "the probe must observe memory, never collect it — otherwise measuring an import would "
            + "itself cost a full gen-2 GC on every partition");
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
        rendered.Should().MatchRegex(@"managed [+-]\d+ MB, working set [+-]\d+ MB");
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
