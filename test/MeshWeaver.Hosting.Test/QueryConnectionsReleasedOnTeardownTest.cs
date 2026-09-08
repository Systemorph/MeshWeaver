using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A teardown releases every synced-query connection the cache opened, and a query opened
/// after teardown terminates instead of parking.</b>
///
/// <para><b>The live defect.</b> <c>MeshNodeStreamCache.GetQueryRaw</c> builds each synced query as
/// <c>Defer(…).SubscribeOn(TaskPoolScheduler.Default).Replay(1).AutoConnect(1)</c>. The first
/// subscriber's <c>Connect()</c> only QUEUES the upstream subscribe on the pool, and the handle it
/// returns stayed inside <c>AutoConnect</c> — so the cache's <c>Dispose()</c>, which detaches every
/// per-path read stream, every update queue and every in-flight write, could not reach the query
/// chains at all. Nothing else in the teardown joins a pool-queued Rx subscribe either:
/// <c>DisposalCompleted</c> covers the action blocks, <c>IoPoolRegistry.DrainAll</c> covers
/// <c>IIoPool</c> leaves, the <c>AsyncDisposeQueue</c> covers enqueued cleanup. The item therefore
/// ran whenever the pool reached it, and when that was after the mesh had reported
/// <c>DISPOSE_DONE … teardown clean</c> and closed its scopes, the Defer resolved
/// <c>cacheHub.GetWorkspace()</c> from a disposed Autofac scope.</para>
///
/// <para><b>Measured</b> on Plugins run 34222933802 (2026-09-08, shard 1): every one of the 11
/// disposed-scope stragglers captured across <c>MeshWeaver.FutuRe.Test</c>,
/// <c>MeshWeaver.Blazor.Views.Test</c> and <c>MeshWeaver.ContentCollections.Indexing.Graph.Test</c>
/// is this Defer — <c>GetQueryRaw</c> → <c>GetWorkspace</c>, or its inner
/// <c>SyncedQueryMeshNodes.BuildReadStreamCore</c> — on a <c>.NET TP Worker</c>; the FutuRe pair
/// landed 4 ms after <c>DISPOSE_DONE</c>. Rx's <c>AnonymousSafeObserver</c> rethrows out of a
/// subscriber with no error arm, so the same straggler on a bare pool thread is the
/// "Catastrophic failure … Failed: 0, exit 1" shard (Plugins#870, #1390).</para>
///
/// <para><b>The contract.</b> The connection is REGISTERED with the cache, the cache's
/// <c>Dispose()</c> releases the registry — unsubscribing every connected upstream and cancelling
/// every connect still queued — and a registration arriving after that is disposed on the spot. A
/// query opened on a disposed cache terminates with <see cref="ObjectDisposedException"/> rather
/// than waiting on a Replay(1) nothing will feed. The control arm on the live mesh is what makes
/// the released count mean anything: a cache that never registered a connection would read
/// zero before AND after.</para>
/// </summary>
public class QueryConnectionsReleasedOnTeardownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    private static string Query => $"nodeType:Markdown namespace:{TestPartition}";

    // 240_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant,
    // so the property cannot be written here. The value must still DOMINATE it — 216 s at the
    // CI factor (Convergence 108 s x OuterMargin 2) — or the xunit kill pre-empts the inner
    // wait and the failure cannot say what it was waiting for.
    [Fact(Timeout = 240_000)]
    public async Task MeshDispose_ReleasesEveryConnectedSyncedQuery()
    {
        // Resolve BEFORE the teardown — after it the provider is the object being closed.
        var cache = Cache;
        var query = cache.GetQuery($"teardown-query-{Guid.NewGuid():N}", Mesh.JsonSerializerOptions, Query);

        await query.FirstAsync().Should().Within(TestTimeouts.Convergence).Emit(
            "precondition: the synced query connects its upstream and answers on the live mesh");
        cache.LiveQueryConnections.Should().Be(1,
            "CONTROL ARM: the first subscriber's Connect() must be registered with the cache — a zero "
            + "here means the registry sees nothing, and the released count below would be vacuous");
        cache.QueryConnectionsReleased.Should().BeFalse(
            "precondition: the registry is live for as long as the cache is");

        Mesh.Dispose();
        await Mesh.DisposalCompleted.ObserveCompletion(
            ex => FileOutput.WriteLine($"late disposal fault: {ex}"),
            TestContext.Current.CancellationToken);

        cache.QueryConnectionsReleased.Should().BeTrue(
            "the cache's Dispose() runs at the cache hub's ShutDown, strictly inside the mesh's "
            + "DisposalCompleted — the registry must be released by then, so that any connect still "
            + "queued on the pool is cancelled BEFORE the drains finish and the scopes close");
        cache.LiveQueryConnections.Should().Be(0,
            "the connected upstream must have been unsubscribed with the cache, not left to run its "
            + "Defer against a scope the teardown has since closed (the GetQueryRaw → GetWorkspace "
            + "disposed-scope straggler)");
    }

    [Fact(Timeout = 240_000)]
    public async Task AfterMeshDispose_ANewQueryTerminates_AndOpensNoUpstream()
    {
        var cache = Cache;
        var options = Mesh.JsonSerializerOptions;

        Mesh.Dispose();
        await Mesh.DisposalCompleted.ObserveCompletion(
            ex => FileOutput.WriteLine($"late disposal fault: {ex}"),
            TestContext.Current.CancellationToken);
        cache.QueryConnectionsReleased.Should().BeTrue("precondition: the teardown released the registry");

        var late = cache.GetQuery($"late-query-{Guid.NewGuid():N}", options, Query);

        var terminal = await late.Materialize().FirstAsync().Should().Within(TestTimeouts.Convergence).Emit(
            "a query opened on a disposed cache must TERMINATE — a subscriber left waiting on a "
            + "Replay(1) whose connect was cancelled is the burst-then-silence hang");
        terminal.Kind.Should().Be(NotificationKind.OnError,
            "the honest answer is a fault naming the cache, never a value from a scope that is gone");
        terminal.Exception.Should().BeOfType<ObjectDisposedException>();
        cache.LiveQueryConnections.Should().Be(0,
            "no upstream may be opened on a disposed cache — a late registration is disposed as it "
            + "is added, and the refusal above means no chain was built to register at all");
    }
}
