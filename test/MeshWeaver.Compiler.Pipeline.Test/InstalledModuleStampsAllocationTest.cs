using System;
using System.Reflection;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins that a module's version stamps are read from its file ONCE per loaded assembly, not on every
/// build-guard evaluation (MeshWeaver#5799).
///
/// <para><b>What went wrong.</b> <c>NodeTypeCompilationHelpers.GuardsOf</c> reads
/// <see cref="InstalledModuleAssembly.Version"/> and <see cref="InstalledModuleAssembly.PackageVersion"/>
/// of every installed module. Each read opened the file and built a <c>PEReader</c> (a
/// <c>PEHeaders</c> with its <c>CoffHeader</c>), and the guards run several times per hub activation
/// and on content writes. The heap-step sampler on a production portal named <c>CoffHeader</c> among
/// the heaviest allocations of the steps that preceded the disposal stalls.</para>
///
/// <para>The negative control measures the per-call file read with the same instrument and shows it
/// fails the same bound — so the instrument can fail.</para>
/// </summary>
public class InstalledModuleStampsAllocationTest(ITestOutputHelper output)
{
    private const int Reads = 100;
    private const long BytesPerReadBound = 256;

    // File-backed and stamped with an informational version by the build, like a module.
    private static readonly Assembly Module = typeof(InstalledModuleAssembly).Assembly;

    [Fact]
    public void StampsOf_ReadsTheFileOnce_AndAllocatesNothingAfter()
    {
        using var contexts = new ModuleContexts();
        var modules = new InstalledModules(contexts);
        var first = modules.StampsOf(Module);
        first.Version.Should().Be(InstalledModuleAssembly.VersionOf(Module),
            "the held stamps are exactly what the file read answers");
        first.PackageVersion.Should().Be(InstalledModuleAssembly.PackageVersionOf(Module));

        var perRead = Settled(() =>
        {
            var hits = 0;
            for (var i = 0; i < Reads; i++)
                if (ReferenceEquals(modules.StampsOf(Module), first))
                    hits++;
            return hits;
        }) / Reads;

        output.WriteLine($"Held: {perRead:N0} bytes per StampsOf read.");
        perRead.Should().BeLessThan(BytesPerReadBound, "the stamps are held per loaded assembly");
        modules.StampsOf(Module).Should().BeSameAs(first);
    }

    [Fact]
    public void NegativeControl_ThePerCallFileRead_FailsTheSameBound()
    {
        // The shape this fix removed from the build guards: open the file and read the metadata
        // on every call.
        var perRead = Settled(() =>
        {
            var hits = 0;
            for (var i = 0; i < Reads / 10; i++)
                if (InstalledModuleAssembly.ReadStamps(Module).Version is not null)
                    hits++;
            return hits;
        }) / (Reads / 10);

        output.WriteLine($"Read per call: {perRead:N0} bytes per read.");
        perRead.Should().BeGreaterThan(BytesPerReadBound * 4,
            "the instrument must be able to see the per-call PE read it exists to catch");
    }

    private static long Settled(Func<int> action)
    {
        var best = long.MaxValue;
        for (var sample = 0; sample < 10; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(action());
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return best;
    }
}
