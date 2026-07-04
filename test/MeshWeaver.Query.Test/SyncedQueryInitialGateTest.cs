using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Pins the <b>Initial gate</b> of <see cref="SyncedQueryMeshNodes"/> (issue #201,
/// regression a777a87bd): the class contract promises NO downstream emission until the
/// upstream query has produced its first <c>Initial</c>/<c>Reset</c>. Two side-channels
/// are merged into the pipeline UNFILTERED by the query — the
/// <see cref="SyncedQueryMeshNodes.NotifyDeleted"/> subject and the process-wide
/// <see cref="IMeshChangeFeed"/> deletion fast-path. If either fires in the
/// subscribe→Initial window, an un-gated Scan emits an EMPTY dictionary as its FIRST
/// emission, which <c>Replay(1)</c> consumers (<c>MeshNodeStreamCache.GetQueryRaw</c>,
/// <c>AgentChatClient</c>'s agent dropdown) cache and replay — the production symptom
/// was "Selected agent 'X' was not found among the available agents ([])".
///
/// <para><b>Determinism:</b> the subscribe→Initial race is made deterministic by a
/// delaying DECORATOR over the REAL <see cref="IMeshQueryCore"/> (no mock): it defers
/// the inner <c>Query</c> subscription for exactly the gated query string until the
/// test fires <see cref="initialGate"/>. Both side-channels deliver synchronously
/// (Rx <c>Subject</c> / in-process change feed), so "fire the side-channel while
/// Initial is held" is exact, not timing-dependent.</para>
/// </summary>
public class SyncedQueryInitialGateTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string SubjectsNamespace = $"{TestPartition}/InitialGateSubjects";

    /// <summary>
    /// The exact query string of the gated synced query. The decorator delays ONLY
    /// requests carrying this exact string, so framework synced queries (SecurityService
    /// _Access walks, …) and this test's un-gated probe query (which appends
    /// <c>state:Active</c>) flow through undisturbed.
    /// </summary>
    private const string GatedQuery = $"namespace:{SubjectsNamespace} scope:subtree nodeType:Markdown";

    /// <summary>
    /// Test-controlled release for the held Initial. The decorator subscribes the
    /// REAL query only after this fires.
    /// </summary>
    private readonly Subject<Unit> initialGate = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                // Decorate the REAL IMeshQueryCore registered by the persistence
                // layer — wrap its factory, don't mock the interface.
                var innerDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IMeshQueryCore))
                    ?? throw new InvalidOperationException(
                        "IMeshQueryCore must already be registered by the persistence layer "
                        + "before the delaying decorator can wrap it.");
                var innerFactory = innerDescriptor.ImplementationFactory
                    ?? throw new InvalidOperationException(
                        "Expected a factory-based IMeshQueryCore registration to decorate.");
                services.RemoveAll<IMeshQueryCore>();
                services.AddSingleton<IMeshQueryCore>(sp => new DelayingQueryCore(
                    (IMeshQueryCore)innerFactory(sp),
                    initialGate,
                    request => request.EffectiveQueries.Any(q => q == GatedQuery)));
                return services;
            });

    /// <summary>
    /// Decorator over the real <see cref="IMeshQueryCore"/>: for requests matching
    /// <paramref name="shouldDelay"/>, the inner <c>Query</c> subscription is deferred
    /// until <paramref name="gate"/> emits — holding the upstream Initial open while the
    /// test fires the un-gated side-channels. Everything else passes straight through.
    /// </summary>
    private sealed class DelayingQueryCore(
        IMeshQueryCore inner,
        IObservable<Unit> gate,
        Func<MeshQueryRequest, bool> shouldDelay) : IMeshQueryCore
    {
        public IObservable<QueryResultChange<T>> Query<T>(
            MeshQueryRequest request,
            JsonSerializerOptions options)
            => shouldDelay(request)
                ? gate.Take(1).SelectMany(_ => inner.Query<T>(request, options))
                : inner.Query<T>(request, options);
    }

    private static MeshNode MakeSubject(string id)
        => new(id, SubjectsNamespace)
        {
            Name = id,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };

    /// <summary>
    /// Seeds one matching node and PROVES it is visible on the query surface via an
    /// un-gated probe query (exact-string delay predicate ⇒ the probe passes through),
    /// so the gated query's Initial — computed when the gate releases — deterministically
    /// contains the seed. Guards the assertions below against the create→index lag noted
    /// in <see cref="SyncedQueryTest"/>.
    /// </summary>
    private async Task<MeshNode> SeedAndProveVisible(string id, object probeId)
    {
        var seed = MakeSubject(id);
        await NodeFactory.CreateNode(seed).Should().Emit();

        var probe = Mesh.GetWorkspace().GetQuery(probeId, $"{GatedQuery} state:Active");
        await probe
            .Where(nodes => nodes.Any(n => n.Path == seed.Path))
            .Should().Within(15.Seconds()).Emit();
        return seed;
    }

    private static (IDisposable Recorder, Func<MeshNode[][]> Snapshot) Record(
        IObservable<IEnumerable<MeshNode>> stream)
    {
        var emissions = new List<MeshNode[]>();
        var recorder = stream.Subscribe(nodes =>
        {
            lock (emissions)
                emissions.Add(nodes.ToArray());
        });
        return (recorder, () =>
        {
            lock (emissions)
                return emissions.ToArray();
        });
    }

    private static void AssertNoEmptyFirstEmission(MeshNode[][] recorded, string seedPath)
    {
        recorded.Should().NotBeEmpty("the released Initial must reach the subscriber");
        recorded.Should().OnlyContain(e => e.Length > 0,
            "no emission may precede the upstream Initial — an empty pre-Initial snapshot "
            + "is exactly the issue-#201 bug (Replay(1) consumers cache and serve it)");
        recorded[0].Should().Contain(n => n.Path == seedPath,
            "the FIRST emission must be the complete Initial snapshot, never a "
            + "side-channel-fabricated empty dictionary");
    }

    /// <summary>
    /// The <see cref="SyncedQueryMeshNodes.NotifyDeleted"/> side-channel leg: a synthetic
    /// Removed for an UNRELATED path pushed while the upstream Initial is held must NOT
    /// produce an empty first emission. RED before the fix (the un-gated Scan emitted the
    /// empty dictionary synchronously); GREEN with the restored Initial gate.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task NotifyDeleted_BeforeInitial_DoesNotEmitEmptySnapshot()
    {
        var seed = await SeedAndProveVisible("seed-external", "$initial-gate-probe-external");

        var synced = new SyncedQueryMeshNodes(
            Mesh.GetWorkspace(),
            "$initial-gate-external",
            GatedQuery);

        var (recorder, snapshot) = Record(synced.StreamUpdates());
        using (recorder)
        {
            // While Initial is HELD by the decorator, fire the NotifyDeleted
            // side-channel for a path that was never in the result set —
            // delivered synchronously into the Scan on this thread.
            synced.NotifyDeleted("Unrelated/Path");

            // Release the gate → the REAL query subscribes and emits Initial.
            initialGate.OnNext(Unit.Default);

            await synced.StreamUpdates()
                .Where(nodes => nodes.Any(n => n.Path == seed.Path))
                .Should().Within(15.Seconds()).Emit();

            AssertNoEmptyFirstEmission(snapshot(), seed.Path);
        }
    }

    /// <summary>
    /// The <see cref="IMeshChangeFeed"/> deletion fast-path leg: the feed subscription in
    /// <c>BuildReadStreamCore</c> is process-wide and UNFILTERED by the query, so ANY
    /// hub's delete lands in EVERY synced query's pipeline. A Deleted event published
    /// while the upstream Initial is held must NOT produce an empty first emission.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ChangeFeedDeletion_BeforeInitial_DoesNotEmitEmptySnapshot()
    {
        var seed = await SeedAndProveVisible("seed-feed", "$initial-gate-probe-feed");

        var synced = new SyncedQueryMeshNodes(
            Mesh.GetWorkspace(),
            "$initial-gate-feed",
            GatedQuery);

        var (recorder, snapshot) = Record(synced.StreamUpdates());
        using (recorder)
        {
            // While Initial is HELD, publish a Deleted change-feed event for an
            // unrelated path — the in-process feed delivers synchronously.
            var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
            changeFeed.Publish(MeshChangeEvent.Deleted("Unrelated/FeedPath"));

            initialGate.OnNext(Unit.Default);

            await synced.StreamUpdates()
                .Where(nodes => nodes.Any(n => n.Path == seed.Path))
                .Should().Within(15.Seconds()).Emit();

            AssertNoEmptyFirstEmission(snapshot(), seed.Path);
        }
    }
}
