using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the one rule that keeps a query the fan-in could not fully answer from becoming a
/// VERDICT about a node: a <see cref="QueryResultChange{T}.SilentProviders"/> frame is a FLOOR,
/// and <c>PathResolutionService</c> must refuse to resolve from one unless the full requested
/// path already matched.
///
/// <para><b>Where the frame comes from.</b> <c>MeshQuery.MergeProviderObservables</c> gates its
/// merged Initial on EVERY provider emitting one. A provider that COMPLETES without an Initial is
/// counted as empty so the gate cannot starve — that rule exists because the alternative hung every
/// real-user search for 300 s — and the provider is NAMED on the frame precisely so a consumer can
/// tell <i>"nobody answered"</i> from <i>"there is nothing there"</i> (MeshWeaver#4557).</para>
///
/// <para><b>What went wrong with it here (issue #1186).</b> Path resolution read that frame as an
/// answer. Two consequences, both silent:</para>
/// <list type="number">
///   <item>Nothing matched ⇒ the resolver emitted <c>null</c> ⇒ <c>MessageHubGrain</c> reported
///     <i>"Either the node does not exist or no query provider claims its partition"</i> — the
///     second clause manufactured out of a frame whose entire content is "nobody said". Every
///     sample on #1186 carries that sentence, and its first clause was falsified 3 of 3 against
///     nodes that demonstrably existed at the failing instant.</item>
///   <item>A SHALLOWER prefix matched ⇒ that partial resolution is a positive result, so
///     <c>ResolveSegments</c> CACHES it, and the path answers with its ancestor plus a remainder
///     for the life of the process — the #4557 durable negative wearing a wrong route instead of a
///     missing node.</item>
/// </list>
///
/// <para>Deterministic by construction: the snapshots are handed to the service directly, so no
/// timing decides any case here.</para>
/// </summary>
public class PathResolutionFloorIsNotAnAnswerTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Minimal <see cref="IMeshQueryCore"/> whose Nth <c>Query</c> subscription is driven by an
    /// injected behaviour. Hand-rolled, not NSubstitute: <see cref="IMeshQueryCore"/> is internal
    /// and DynamicProxy cannot proxy it (and AGENTS.md forbids mocking core interfaces anyway).
    /// </summary>
    private sealed class SequencedQueryCore(Func<int, IObservable<QueryResultChange<MeshNode>>> behaviour)
        : IMeshQueryCore
    {
        private int _calls;
        public int Calls => _calls;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        {
            // The fixture only knows how to answer MeshNode, and PathResolutionService's single
            // call site (ResolveSegmentsCore) asks for exactly that. SAY so: the
            // `(IObservable<QueryResultChange<T>>)(object)` hop below is sound only under that
            // assumption, and if the subject ever queried another type it would surface as an
            // InvalidCastException naming neither the type nor this double — the shape of
            // diagnostic this whole change exists to remove.
            if (typeof(T) != typeof(MeshNode))
                throw new NotSupportedException(
                    $"{nameof(SequencedQueryCore)} answers Query<MeshNode> only, and was asked for "
                    + $"Query<{typeof(T).Name}>. PathResolutionService is expected to query MeshNode; "
                    + "if that changed, teach this fixture the new type rather than casting through it.");
            return (IObservable<QueryResultChange<T>>)(object)behaviour(Interlocked.Increment(ref _calls));
        }
    }

    private (PathResolutionService Svc, SequencedQueryCore Query) BuildService(
        Func<int, IObservable<QueryResultChange<MeshNode>>> behaviour)
    {
        // A registered change feed is what ENABLES caching — without it the service resolves
        // uncached and the "not cached" half of this fixture could not fail.
        var feed = new InProcessMeshChangeFeed();
        var hub = Mesh.GetHostedHub(
            new Address($"pathres-floor-{Guid.NewGuid():N}"),
            c => c.WithServices(s => s.AddSingleton<IMeshChangeFeed>(feed)));
        var query = new SequencedQueryCore(behaviour);
        return (new PathResolutionService(hub, query, Array.Empty<IPartitionStorageProvider>()), query);
    }

    private static MeshNode Node(string path) => new(
        path.Split('/').Last(),
        path.Contains('/') ? path[..path.LastIndexOf('/')] : null)
    {
        NodeType = "Markdown",
        State = MeshNodeState.Active,
    };

    /// <summary>
    /// A snapshot a provider did not answer. <paramref name="silent"/> is what
    /// <c>MeshQuery.MergeProviderObservables</c> stamps; the trailing <c>Never</c> mirrors a live
    /// query staying open past its Initial.
    /// </summary>
    private static IObservable<QueryResultChange<MeshNode>> Snapshot(
        IReadOnlyList<string>? silent, params MeshNode[] items) =>
        Observable.Return(new QueryResultChange<MeshNode>
            {
                ChangeType = QueryChangeType.Initial,
                Items = items,
                SilentProviders = silent,
                Timestamp = DateTimeOffset.UtcNow,
            })
            .Concat(Observable.Never<QueryResultChange<MeshNode>>());

    /// <summary>
    /// 🚨 THE #1186 REPRO, first half. Nobody answered and nothing matched. The resolver must NOT
    /// emit the absent verdict — that verdict is what the grain turns into <i>"the node does not
    /// exist or no query provider claims its partition"</i>, and neither clause follows from this
    /// frame. It must fault, and the fault must NAME the provider that went silent, because that
    /// name is the whole difference between "look for the node" and "look for the provider".
    /// </summary>
    [Fact]
    public async Task FloorWithNothingMatched_IsRefused_NeverAnsweredAsAbsent()
    {
        var (svc, _) = BuildService(_ => Snapshot(["pg-partitioned"]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ResolvePath("Admin/_Notification/p8i5C-9eWUSrfKQlEcEYMg")
                .Take(1)
                .Timeout(TestTimeouts.Quick)
                .Await(TestContext.Current.CancellationToken));

        // The three facts a triage needs, on the exception the fingerprint is built from.
        Assert.Contains("INDETERMINATE", error.Message);
        Assert.Contains("pg-partitioned", error.Message);
        Assert.Contains("Admin/_Notification/p8i5C-9eWUSrfKQlEcEYMg", error.Message);
    }

    /// <summary>
    /// 🚨 THE #1186 REPRO, second half — the one that outlives the request. Only a shallower
    /// ANCESTOR matched while a provider was silent, so the deepest node at this path is unknown.
    /// Answering would hand the caller <c>Prefix=Admin, Remainder=_Notification/…</c>, and because
    /// that is a POSITIVE resolution <c>ResolveSegments</c> would CACHE it: the path keeps
    /// answering with its ancestor until the process restarts or a Created/Deleted event for that
    /// exact path arrives — and a reconcile re-writing an unchanged node publishes neither.
    /// </summary>
    [Fact]
    public async Task FloorWithOnlyAShallowerAncestor_IsRefused_AndNothingIsCached()
    {
        var (svc, query) = BuildService(call => call == 1
            // Call 1: the satellite provider went silent; only the partition root came back.
            ? Snapshot(["pg-partitioned"], Node("Admin"))
            // Call 2: everybody answered, and the deep node was there all along.
            : Snapshot(null, Node("Admin"), Node("Admin/_Notification/p8i5C")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ResolvePath("Admin/_Notification/p8i5C")
                .Take(1)
                .Timeout(TestTimeouts.Quick)
                .Await(TestContext.Current.CancellationToken));
        Assert.Contains("deeper than 'Admin'", error.Message);

        // Nothing was pinned: a second resolution runs a NEW query and gets the real answer.
        // Against the unguarded code the first call caches Prefix=Admin and this reads it back.
        var resolved = await svc.ResolvePath("Admin/_Notification/p8i5C")
            .Take(1)
            .Timeout(TestTimeouts.Quick)
            .Await(TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal("Admin/_Notification/p8i5C", resolved!.Prefix);
        Assert.Null(resolved.Remainder);
        Assert.True(query.Calls >= 2, "the refused resolution must not have been cached");
    }

    /// <summary>
    /// The OTHER side of the rule, and the reason it is not simply "refuse every floor": when the
    /// hit IS the full requested path, nothing a silent provider holds can be deeper, so the frame
    /// supports a resolution after all. Refusing here would turn every query that raced a silent
    /// provider into a 404 for a node that was found.
    /// </summary>
    [Fact]
    public async Task FloorWhoseHitIsTheFullRequestedPath_IsAnswered()
    {
        var (svc, _) = BuildService(_ => Snapshot(["pg-partitioned"], Node("Admin/_Notification/p8i5C")));

        var resolved = await svc.ResolvePath("Admin/_Notification/p8i5C")
            .Take(1)
            .Timeout(TestTimeouts.Quick)
            .Await(TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal("Admin/_Notification/p8i5C", resolved!.Prefix);
        Assert.Null(resolved.Remainder);
    }

    /// <summary>
    /// The control on the other side of the change: a COMPLETE snapshot that matched nothing is a
    /// real answer and still resolves to <c>null</c> (the absent verdict, which the grain reports
    /// promptly and correctly). Without this case the fixture could not tell "we stopped trusting
    /// floors" from "we stopped answering absent".
    /// </summary>
    [Fact]
    public async Task CompleteSnapshotWithNothingMatched_StillAnswersAbsent()
    {
        var (svc, _) = BuildService(_ => Snapshot(null));

        var resolved = await svc.ResolvePath("Admin/_Notification/p8i5C")
            .Take(1)
            .Timeout(TestTimeouts.Quick)
            .Await(TestContext.Current.CancellationToken);

        Assert.Null(resolved);
    }
}
