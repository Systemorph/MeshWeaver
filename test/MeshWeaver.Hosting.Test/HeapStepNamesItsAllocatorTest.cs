using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 MeshWeaver#5555: memex-cloud replicas take multi-GiB LIVE heap steps (+2 to +10&#160;GiB inside
/// one 100&#160;s <c>[LIVENESS]</c> sample, on several replicas within seconds) and one of them died of
/// <c>OutOfMemoryException</c> — and no reading anybody could take named WHAT was allocated. A heap
/// dump restarts a replica this size, and nobody is at the pod when a step lands. These tests pin the
/// in-band instrument: a tick whose heap grew by <see cref="ProcessLiveness.HeapStepThresholdBytes"/>
/// or more names the types the runtime sampled as allocated in that window.
/// </summary>
public class HeapStepNamesItsAllocatorTest
{
    private static ProcessLivenessSample Sample(long tick, double elapsedSeconds, long heapBytes) =>
        new(tick, TimeSpan.FromSeconds(elapsedSeconds), 1, 1, 1, TimeSpan.Zero, heapBytes, false, 8, 0, 100);

    private const long Gib = 1L << 30;

    private static readonly AllocationWindow AStep = new(
        6 * Gib,
        ImmutableList.Create(
            new TypeAllocation("System.Byte[]", 5 * Gib),
            new TypeAllocation("System.String", Gib)));

    [Fact]
    public void AStepTick_NamesTheHeaviestTypesAllocatedInIt()
    {
        var line = ProcessLiveness.DescribeHeapStep(
            Sample(40, 400, 2 * Gib), Sample(41, 410, 8 * Gib), AStep);

        line.Should().NotBeNull("the heap grew by 6 GiB inside one tick");
        line.Should().StartWith("[HEAPSTEP] tick=41 heap=2.00GiB→8.00GiB (+6.00GiB) in 10.00s");
        line.Should().Contain("System.Byte[]=5.00GiB (83%)");
        line.Should().Contain("System.String=1.00GiB (17%)");
        line!.IndexOf("System.Byte[]", StringComparison.Ordinal)
            .Should().BeLessThan(line.IndexOf("System.String", StringComparison.Ordinal),
                "the heaviest type is named first");
    }

    /// <summary>
    /// The NEGATIVE control for the line above: the same window over a tick whose heap did NOT step
    /// reports nothing — the line is rare by construction, never a second heartbeat.
    /// </summary>
    [Fact]
    public void AnOrdinaryTick_ReportsNothing_EvenWithHeavyAllocation()
    {
        ProcessLiveness.DescribeHeapStep(
                Sample(40, 400, 2 * Gib), Sample(41, 410, 2 * Gib + ProcessLiveness.HeapStepThresholdBytes - 1), AStep)
            .Should().BeNull("growth one byte short of the threshold is not a step");
        ProcessLiveness.DescribeHeapStep(null, Sample(1, 10, 8 * Gib), AStep)
            .Should().BeNull("the first tick has nothing to have grown from");
    }

    /// <summary>
    /// A host whose sampler did not start still reports the step — and says it cannot name the
    /// allocator, rather than printing an empty list that reads as "nothing was allocated".
    /// </summary>
    [Fact]
    public void AStepWithoutSamples_SaysItCannotNameTheAllocator()
    {
        ProcessLiveness.DescribeHeapStep(
                Sample(40, 400, Gib), Sample(41, 410, 3 * Gib), AllocationWindow.Empty)
            .Should().Contain("NO allocation was sampled in this window");
    }

    /// <summary>
    /// The live half: the runtime's own <c>GCAllocationTick</c> events reach the sampler and are
    /// attributed to the type that was allocated. Positive control for the pure tests above — a
    /// sampler that received nothing would make every real <c>[HEAPSTEP]</c> line say "cannot name".
    /// </summary>
    [Fact]
    public async Task TheSampler_AttributesSampledAllocationToTheTypeAllocated()
    {
        using var sampler = new AllocationByTypeSampler();
        // The runtime prints a NESTED type by its own name (measured: "HeapStepMarker[]", no
        // namespace and no enclosing type), so match on the suffix the runtime is certain to print.
        var marker = nameof(HeapStepMarker) + "[]";
        static bool IsMarker(TypeAllocation t) => t.TypeName.EndsWith(nameof(HeapStepMarker) + "[]", StringComparison.Ordinal);

        // Before allocating: whatever the process sampled so far, the marker is not in it.
        sampler.Drain().Top.Should().NotContain(IsMarker,
            "nothing has allocated a HeapStepMarker array yet");

        // ~160 MiB of the marker type, kept reachable until the reading is taken.
        var held = Enumerable.Range(0, 200).Select(_ => new HeapStepMarker[100_000]).ToArray();

        // Samples arrive on the runtime's event dispatch thread, so drain until they have landed.
        // Each drain starts a new window, so the per-type totals are summed across drains.
        var attributed = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Select(_ => sampler.Drain())
            .Scan(0L, (sum, w) => sum + w.Top.Where(IsMarker).Sum(t => t.Bytes))
            .Where(sum => sum >= 64L * 1024 * 1024)
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"~160 MiB of {marker} was allocated, so the runtime's sampled allocation events must attribute a large share of it to that type");

        attributed.Should().BeGreaterThanOrEqualTo(64L * 1024 * 1024);
        GC.KeepAlive(held);
    }

    private sealed class HeapStepMarker
    {
    }

}
