using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A snapshot NOBODY ANSWERED must not become the cached answer</b> (MeshWeaver#4557).
///
/// <para><b>The property.</b> <c>MeshNodeStreamCache.GetQueryRaw</c> builds every synced query as
/// <c>Replay(1)</c> + <c>AutoConnect</c> and keeps it in a registry it never rebuilds, so the FIRST
/// frame a chain produces is what every later caller in the process replays. That is the point of
/// the cache — for a real answer. But <c>MeshQuery.MergeProviderObservables</c> counts a provider
/// that COMPLETES WITHOUT an Initial as an EMPTY Initial, deliberately (the alternative starved the
/// gate and hung every real-user search for 300 s), so a cold moment produces a frame that says
/// "there is nothing there" when what happened is "nobody answered yet" — and the cache then made
/// that permanent. Nothing corrected it either: the one event that refreshes a chain is a change
/// notification for a matching path, and a reconcile re-writing an unchanged node is a NO-OP at the
/// store, which publishes nothing.</para>
///
/// <para><b>Measured</b> on memex-cloud 2026-09-16, in the 24 minutes after a restart: the plugin
/// gate read durably present <c>_Access</c> grants (version 1, three days old) and <c>_Policy</c>
/// nodes as MISSING and reported them at Error as writes that did not become durable — nine of the
/// ten samples retained on incident <c>d1cd36f53a5f3a6c</c> (11,927 occurrences, MeshWeaver#1246).
/// </para>
///
/// <para><b>The provider here is a test PROVIDER, not a mock of a core service.</b>
/// <see cref="IMeshQueryProvider"/> is the extension point every backend plugs into (the same seam
/// <c>MeshQueryMergeContractTest</c> and <c>PagedQueryTotalOrderTest</c> drive), and the defect
/// being reproduced is precisely a provider that violates the one-Initial contract — which is an
/// observed production shape, not an invented one. Everything else in this test is the real mesh:
/// the real <c>MeshQuery</c> merge, the real <see cref="IMeshNodeStreamCache"/>, the real synced
/// collection.</para>
/// </summary>
public class ColdEmptyInitialIsNotCachedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ColdPartition = "ColdProbe";
    private static readonly string NodePath = $"{ColdPartition}/Present";

    private readonly ColdThenAnsweringProvider provider = new(NodePath);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .ConfigureServices(services => services.AddSingleton<IMeshQueryProvider>(provider));

    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    [Fact(Timeout = 240_000)]
    public async Task AFrameNobodyAnswered_IsNotReplayedAsTheAnswerForever()
    {
        var cache = Cache;
        var id = $"cold-initial-{Guid.NewGuid():N}";
        var query = $"path:{NodePath}";

        // FIRST read — the provider completes without an Initial (the cold moment). The merge
        // counts that as empty so nothing hangs, which is the behaviour this test must NOT break.
        var cold = await cache.GetQuery(id, Mesh.JsonSerializerOptions, query)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Empty(cold);
        Assert.Equal(1, provider.Subscriptions);

        // SECOND read — the same id and query set, and the provider now answers. Before #4557 this
        // replayed the cold empty: the registry still held the chain, Replay(1) served its first
        // frame, and the provider was never asked again — a node that exists reads as absent for
        // the life of the process.
        var answered = await cache.GetQuery(id, Mesh.JsonSerializerOptions, query)
            .Where(nodes => nodes.Any())
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Equal(NodePath, answered.Single().Path);
        Assert.True(provider.Subscriptions >= 2,
            "the cold chain must not be kept: the second read has to ASK the provider again, "
            + $"and it was subscribed {provider.Subscriptions} time(s)");
    }

    [Fact(Timeout = 240_000)]
    public async Task AnAnsweredFrame_IsStillCachedAndReplayed()
    {
        // The control arm, and the property this fix must preserve: a snapshot every provider
        // answered keeps its Replay(1) — one upstream subscription, replayed to later callers.
        // Without this arm the fix could "pass" by never caching anything.
        var cache = Cache;
        var id = $"warm-initial-{Guid.NewGuid():N}";
        var query = $"path:{NodePath}";
        provider.AnswerFromTheStart();

        var first = await cache.GetQuery(id, Mesh.JsonSerializerOptions, query)
            .Where(nodes => nodes.Any()).FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Equal(NodePath, first.Single().Path);
        var afterFirst = provider.Subscriptions;

        var second = await cache.GetQuery(id, Mesh.JsonSerializerOptions, query)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Equal(NodePath, second.Single().Path);
        Assert.Equal(afterFirst, provider.Subscriptions);
    }

    /// <summary>
    /// The production shape, made deterministic: the FIRST subscription completes without emitting
    /// an <see cref="QueryChangeType.Initial"/> — a provider that gave up under load, an
    /// <c>Observable.Empty</c>-shaped branch, a swallowed fault — and every later subscription
    /// answers properly. It only claims the queries this test asks about, so the rest of the mesh
    /// is untouched.
    /// </summary>
    private sealed class ColdThenAnsweringProvider(string nodePath) : IMeshQueryProvider
    {
        private int subscriptions;
        private int answerFromTheStart;

        public string Name => nameof(ColdThenAnsweringProvider);

        /// <summary>How many times a query of this provider's was actually subscribed.</summary>
        public int Subscriptions => Volatile.Read(ref subscriptions);

        /// <summary>Makes the provider answer on its very first subscription (the control arm).</summary>
        public void AnswerFromTheStart() => Interlocked.Exchange(ref answerFromTheStart, 1);

        public bool Matches(IReadOnlyList<string> queryNamespaces) =>
            queryNamespaces.Any(ns => ns.StartsWith(ColdPartition, StringComparison.Ordinal));

        public IObservable<QueryResultChange<T>> Query<T>(
            MeshQueryRequest request, JsonSerializerOptions options) =>
            Observable.Defer(() =>
            {
                var mine = request.EffectiveQueries.Any(q => q.Contains(nodePath, StringComparison.Ordinal));
                if (!mine)
                    return Observable.Return(Frame<T>(Array.Empty<T>()));
                var n = Interlocked.Increment(ref subscriptions);
                if (n == 1 && Volatile.Read(ref answerFromTheStart) == 0)
                    // THE COLD MOMENT: completes, having said nothing at all.
                    return Observable.Empty<QueryResultChange<T>>();
                var node = new MeshNode("Present", ColdPartition)
                {
                    Name = "Present", NodeType = "Markdown", State = MeshNodeState.Active,
                };
                return Observable.Return(Frame<T>(node is T typed ? [typed] : Array.Empty<T>()));
            });

        private static QueryResultChange<T> Frame<T>(IReadOnlyList<T> items) => new()
        {
            ChangeType = QueryChangeType.Initial,
            Items = items,
            Timestamp = DateTimeOffset.UtcNow,
        };

        public IObservable<IReadOnlyCollection<QueryResult>> Query(
            MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }
}
