using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The collection for tests that assert PROCESS-WIDE GC reachability — "after disposing X,
/// nothing still roots it" — via <c>WeakReference</c> + a forced full <c>GC.Collect</c>.
///
/// <para>🚨 <b>This is scoping, not a band-aid.</b> Those assertions are statements about the
/// whole process, so they are only meaningful when the rest of the process is quiet. With other
/// classes concurrently allocating and holding live object graphs, a forced collect cannot be
/// relied on to reclaim the target, and the test reports a "leak" that is really just a busy heap.
/// Under parallelism such a test is not flaky — it is WRONG, because its precondition no longer
/// holds. Declaring that precondition is the fix; suppressing, retrying or widening a timeout
/// would be the band-aid.</para>
///
/// <para><c>DisableParallelization</c> makes xUnit run this collection on its own rather than
/// alongside the parallel ones, which restores the quiet process these tests require. Everything
/// else in the assembly keeps running at <c>maxParallelThreads</c> (see
/// <c>test/MeshWeaver.Hosting.Monolith.Test/xunit.runner.json</c>).</para>
///
/// <para>Measured: enabling parallelism on this assembly without this collection reds
/// <c>NodeTypeAssemblyLeakTest.NodeTypeAssemblyContext_IsCollected_AfterMeshDisposal</c> and
/// <c>MeshHubDisposalLeakTest.MeshHub_IsCollected_AfterMeshAndServiceProviderDisposal</c> —
/// both purely because a neighbour was allocating.</para>
///
/// <para>Add a class here ONLY if it asserts reachability/collection of an object after disposal.
/// A test that is merely slow, or that races on shared STATE, does not belong — that is a real
/// defect and serialising it would hide it.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GcReachabilityCollection
{
    /// <summary>The collection name; referenced by <c>[Collection(GcReachabilityCollection.Name)]</c>.</summary>
    public const string Name = "GC reachability (must run without parallel neighbours)";
}
