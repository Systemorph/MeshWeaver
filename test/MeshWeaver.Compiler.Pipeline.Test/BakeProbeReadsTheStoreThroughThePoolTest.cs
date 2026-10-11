using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The bake probe's per-type store read is blocking file I/O against what is a network share on
/// a deployed portal: a directory listing, an open and — since the probe resolves by identity
/// (Systemorph/MeshWeaver.Plugins#2799) — a PE-header read of the file the store answered with.
/// Given a pool, the probe runs that whole read inside it rather than on the thread that delivered
/// the enumeration.
///
/// <para>The witness is the pool's own in-flight count, read by the store at the moment it is
/// asked. <b>Should fail if</b> the pooled overload reads inline (the count is 0). The control is
/// the public, pool-less overload over the same store: 0, by construction.</para>
/// </summary>
public class BakeProbeReadsTheStoreThroughThePoolTest
{
    private const string Live = "03d6f01eb6654e199d31fc59668d7b62";
    private static TimeSpan Budget => TestTimeouts.Convergence;

    private static ImmutableDictionary<string, NodeTypeDefinition?> OneBakedType => ImmutableDictionary<string, NodeTypeDefinition?>.Empty
        .Add("Store/Plugin", new NodeTypeDefinition
        {
            Configuration = "config => config",
            CompilationStatus = CompilationStatus.Ok,
            CompiledFrameworkVersion = Live,
            LastCompiledVersion = 845,
            LatestAssemblyCollection = "local",
            LatestAssemblyPath = "Store_Plugin/v845-03d6f01e-fef027803a74.dll",
        });

    [Fact]
    public async Task GivenAPool_TheStoreIsReadInsideIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var pool = new IoPool(1);

        var pooledStore = new PoolWitnessStore(pool);
        var report = await NodeTypeBakeStatus.ProbeThrough(pool, OneBakedType, pooledStore, Live)
            .Take(1)
            .Should().Within(Budget).Emit("the pooled probe answers", ct);
        report!.Entries.Single().State.Should().Be(BakeState.Baked, "the verdict is the same either way");
        pooledStore.InFlightWhenAsked.Should().Be(1, "the store was asked from inside a pool slot");

        var inlineStore = new PoolWitnessStore(pool);
        await NodeTypeBakeStatus.Probe(OneBakedType, inlineStore, Live)
            .Take(1)
            .Should().Within(Budget).Emit("the inline probe answers", ct);
        inlineStore.InFlightWhenAsked.Should().Be(0,
            "CONTROL — without a pool the read is inline, so the witness can tell the two apart");
    }

    /// <summary>A store that holds every build and records how many pool slots were taken when it
    /// was asked.</summary>
    private sealed class PoolWitnessStore(IoPool pool) : IAssemblyStore
    {
        public int? InFlightWhenAsked { get; private set; }

        public IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version)
        {
            // Read when CALLED, as a file-system store lists and opens when called.
            InFlightWhenAsked = pool.CurrentInFlight;
            return Observable.Return<string?>($"/data/assembly-cache/{nodeTypePath.Replace('/', '_')}/v{version}.dll");
        }

        public IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes) =>
            Observable.Return(string.Empty);
    }
}
