using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Issue #1316 — a TRANSIENT upstream fault must not poison a synced query for the life of
/// the process.
///
/// <para>The cached synced query is <c>Replay(1).AutoConnect(1)</c> held in
/// <c>MeshNodeStreamCache._queries</c>. All three of those properties conspire on a fault:
/// <c>ReplaySubject</c> LATCHES the terminal and replays it to every later subscriber,
/// <c>AutoConnect(1)</c> never reconnects, and the dictionary entry never expired. So a single
/// upstream error made the id permanently unreadable — recoverable only by restarting the pod.</para>
///
/// <para>In production this fired as an Npgsql CONNECT timeout inside
/// <c>PostgreSqlStorageAdapter.QueryNodesUnionInnerAsync</c> while a per-NodeType hub read its
/// source set through <c>NodeSources.GetSources</c> (id <c>nodetype-sources:Edu/Module</c>). The
/// sources watcher then re-established once a second straight onto the replayed terminal,
/// <c>NodeTypeDefinition.CurrentSourceVersions</c> was never written, and a NULL snapshot
/// classifies as a gating <c>PreWarmStatus.CompileError</c>
/// (<c>DynamicTypePreWarmer.ClassifyCompileFailure</c>) — a momentary database blip blocking the
/// rollout of a type whose sources had never been read at all.</para>
///
/// <para>The per-PATH sibling cache already learned this from the same error class
/// (<c>EvictFaultedEntry</c>: "an Npgsql connect failure … replayed forever until a manual
/// recycle"); the query cache had no equivalent. These tests pin the eviction, and — the part
/// that actually matters — that the eviction is visible through the PUBLIC surface, which routes
/// through a separately-memoised options wrapper that used to keep pointing at the dead chain.</para>
/// </summary>
public class SyncedQueryFaultRecoveryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// A real <see cref="IMeshQueryProvider"/> (no mocking — see the testing rules) that faults
    /// the FIRST subscription for queries carrying <see cref="Marker"/> and answers healthily on
    /// every later one. That is the exact shape of a transient connect timeout: the upstream is
    /// fine, the one attempt was not.
    ///
    /// <para>Every non-marker query gets the well-behaved "no matches" answer — an empty
    /// <c>Initial</c> followed by a live stream that never completes — so registering this
    /// provider cannot disturb the rest of the mesh's own queries.</para>
    /// </summary>
    private sealed class FaultOnceQueryProvider : IMeshQueryProvider
    {
        public const string Marker = "SyncedQueryFaultProbe";

        private int subscribeCount;

        /// <summary>Upstream subscriptions opened for the marker query. 2 ⇒ the cache re-probed.</summary>
        public int MarkerSubscribeCount => Volatile.Read(ref subscribeCount);

        public string Name => nameof(FaultOnceQueryProvider);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        {
            if (!request.EffectiveQueries.Any(q => q?.Contains(Marker, StringComparison.Ordinal) == true))
                return LiveEmpty<T>();

            // Defer so the counter advances per SUBSCRIPTION, not per call: the whole point is to
            // distinguish "a new upstream was opened" from "the cached terminal was replayed".
            return Observable.Defer(() =>
                Interlocked.Increment(ref subscribeCount) == 1
                    ? Observable.Throw<QueryResultChange<T>>(
                        new TimeoutException("simulated transient Npgsql connect timeout (#1316)"))
                    : LiveEmpty<T>());
        }

        // An empty Initial then never-completing: what a healthy provider with no matches emits.
        private static IObservable<QueryResultChange<T>> LiveEmpty<T>()
            => Observable
                .Return(new QueryResultChange<T> { ChangeType = QueryChangeType.Initial, Items = [] })
                .Concat(Observable.Never<QueryResultChange<T>>());

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }

    private readonly FaultOnceQueryProvider provider = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMeshQueryProvider>(provider);
                return services;
            });

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The regression. Subscriber 1 observes the transient fault; subscriber 2 must get a real
    /// re-probe, not the latched terminal.
    ///
    /// <para><c>MarkerSubscribeCount == 2</c> is the load-bearing assertion — without it a cache
    /// that merely happened to answer from some other layer would pass. Two upstream
    /// subscriptions means a genuinely fresh <c>SyncedQueryMeshNodes</c> was built.</para>
    /// </summary>
    [Fact]
    public async Task TransientUpstreamFault_IsNotReplayedToTheNextSubscriber()
    {
        var workspace = Mesh.GetWorkspace();
        var id = $"fault-recovery:{Guid.NewGuid():N}";
        var query = $"namespace:{FaultOnceQueryProvider.Marker} scope:subtree";

        // 1. The transient fault reaches the subscriber — it is surfaced, never swallowed.
        var first = await Record.ExceptionAsync(() =>
            workspace.GetQuery(id, query).FirstAsync().Timeout(Budget).ToTask());

        first.Should().NotBeNull("the transient upstream fault must be surfaced to the subscriber");
        provider.MarkerSubscribeCount.Should().Be(1);

        // 2. The next caller must reach the (now healthy) upstream. Before the fix this replayed
        //    the latched TimeoutException instantly and MarkerSubscribeCount stayed at 1 forever.
        var second = await workspace.GetQuery(id, query).FirstAsync().Timeout(Budget).ToTask();

        second.Should().NotBeNull();
        provider.MarkerSubscribeCount.Should().Be(2,
            "the faulted cache entry must be evicted so the next GetQuery opens a FRESH upstream "
            + "instead of replaying the latched terminal");
    }

    /// <summary>
    /// The half that the eviction alone does not buy. <c>GetQuery(id, options, …)</c> memoises the
    /// content-deserialising wrapper separately, keyed by <c>(id, options)</c>. If that wrapper
    /// keeps closing over the EVICTED chain, every caller on the public surface still gets the
    /// dead stream and the eviction is unobservable — the query stays broken exactly as before.
    ///
    /// <para>Same shape as the test above but asserting on the cache surface the framework's own
    /// callers use, with the caller's <c>JsonSerializerOptions</c> in play.</para>
    /// </summary>
    [Fact]
    public async Task OptionsWrappedQuery_ReWrapsAfterEviction_RatherThanServingTheDeadChain()
    {
        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var options = Mesh.JsonSerializerOptions;
        var id = $"fault-recovery-wrapped:{Guid.NewGuid():N}";
        var query = $"namespace:{FaultOnceQueryProvider.Marker} scope:subtree";

        var before = provider.MarkerSubscribeCount;

        var first = await Record.ExceptionAsync(() =>
            cache.GetQuery(id, options, query).FirstAsync().Timeout(Budget).ToTask());
        first.Should().NotBeNull();

        var second = await cache.GetQuery(id, options, query).FirstAsync().Timeout(Budget).ToTask();

        second.Should().NotBeNull();
        provider.MarkerSubscribeCount.Should().Be(before + 2,
            "the memoised options wrapper must be rebuilt over the fresh raw chain — otherwise the "
            + "eviction is invisible from the surface every framework caller actually uses");
    }
}
