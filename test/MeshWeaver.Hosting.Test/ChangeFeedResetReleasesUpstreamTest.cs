using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>Evicting a faulted read entry must ALSO release the path's upstream sync streams —
/// on EVERY eviction path, including the change-feed one.</b> Issue #3110.
///
/// <para><b>The live defect.</b> A <c>HeartBeatEvent</c> from <c>cache/…</c> was still being
/// routed to <c>Crm/Stage/_Activity/compile-…</c> <b>49 minutes</b> after that compile had
/// finished <c>Succeeded</c> in about a second. Two reclaimers exist for such a mirror and both
/// are keyed on the cache's <c>_streams</c> entry: the ten-minute idle sweep ITERATES it, and the
/// event-driven <c>ReleaseIfUnwatched</c> (which <c>ActivityLogAppender</c> fires on the terminal
/// activity write, #1435/#1324) LOOKS THE PATH UP in it. Remove the entry without detaching its
/// upstream and the stream survives in the cache hub's workspace with its 45 s heartbeat running —
/// invisible to both, and reachable again only if that exact path is ever read again. A finished
/// compile activity is never read again, so "never" is the process lifetime.</para>
///
/// <para><b>The mechanism.</b> #1202 established the rule — an eviction must DETACH the upstream,
/// not just drop the entry — and centralised it in <c>MeshNodeStreamCache.EvictFaultedEntry</c>,
/// whose own remarks call it "the single teardown behind all three re-probe triggers … so they
/// cannot drift". There was a FOURTH faulted-entry eviction, and it drifted: the change-feed
/// invalidation reset (<c>ResetFailureState</c>) kept a hand-rolled copy that disposed only the Rx
/// hydration and reported its eviction with <c>UpstreamReleased: false</c> hard-coded. That path
/// runs on EVERY mesh change event for the path — including the activity's own terminal write —
/// so a compile activity whose owner had one transient miss (an idle-collected grain, a 60 s
/// request timeout: the node EXISTS, it was momentarily unreachable, and the stream therefore
/// stays live) had its entry dropped and its heartbeat orphaned in the same breath.</para>
///
/// <para><b>The contract under test.</b> A faulted entry evicted by the change feed releases its
/// upstream, exactly as the idle sweep's and the breakers' evictions do. The control arm proves
/// the assertion is not vacuous: the SAME fault shape, released through the already-correct
/// <c>ReleaseIfUnwatched</c> seam, reports an upstream too — so "no upstream was released" can
/// only mean the change-feed path failed to release one.</para>
/// </summary>
public class ChangeFeedResetReleasesUpstreamTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    /// <summary>
    /// Stands in as the OWNER of one throwaway node path through the framework's own
    /// <see cref="IRoutingService.RegisterStream(Address, SyncDelivery)"/> seam — the same call
    /// every hub makes for itself, so the node's real per-node hub is never activated and every
    /// <c>SubscribeRequest</c> the cache opens for the path lands here.
    ///
    /// <para>Each attempt is answered with the verbatim production timeout banner
    /// (<c>MessageHub.BuildTimeoutMessage</c>). That is the TRANSIENT owner-unreachable class:
    /// the node exists, so no negative window opens and nothing suppresses the path — the entry
    /// is left faulted with its upstream still live, which is precisely the state #3110 leaks.
    /// Borrowed verbatim from <c>MeshNodeStreamCacheFaultedEntryReprobeTest</c> (#1202).</para>
    /// </summary>
    private sealed class UnreachableOwner : IDisposable
    {
        private readonly IDisposable registration;
        private ImmutableList<string> requestIds = ImmutableList<string>.Empty;

        public UnreachableOwner(IMessageHub mesh, IRoutingService routing, string path)
        {
            var access = mesh.ServiceProvider.GetService<AccessService>();
            SyncDelivery answer = delivery =>
            {
                // Mirror PostNotFound's guard: never NACK a NACK, and never answer a
                // [CanBeIgnored] lifecycle message (that is the disposal ping-pong storm).
                if (delivery.Message is DeliveryFailure
                    || delivery.Message.GetType().HasAttribute<CanBeIgnoredAttribute>())
                    return delivery.Processed();
                var requestId = Guid.NewGuid().ToString("N")[..12];
                ImmutableInterlocked.Update(ref requestIds, ids => ids.Add(requestId));
                var banner = Banner(path, requestId);
                using (delivery.AccessContext is null ? access?.ImpersonateAsSystem() : null)
                    mesh.Post(
                        new DeliveryFailure(delivery) { ErrorType = ErrorType.Exception, Message = banner },
                        o => o.ResponseFor(delivery));
                return delivery.FailedAndNacked(banner);
            };
            registration = routing.RegisterStream(new Address(path), answer);
        }

        public ImmutableList<string> RequestIds => Volatile.Read(ref requestIds);

        public void Dispose() => registration.Dispose();

        private static string Banner(string path, string requestId) =>
            $"No response received in hub cache/mesh-node-cache within 00:01:00 for request " +
            $"SubscribeRequest (id={requestId}) → target {path}. The request may have been " +
            "undeliverable or the target hub was not found.";
    }

    private async Task<string> CreateNodeAsync(string prefix)
    {
        var path = $"{TestPartition}/{prefix}-{Guid.NewGuid():N}";
        var node = MeshNode.FromPath(path) with
        {
            Name = "Original",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        await NodeFactory.CreateNode(node).Should().Within(TestTimeouts.Convergence).Emit();
        return path;
    }

    private UnreachableOwner Hijack(string path) =>
        new(Mesh, Mesh.ServiceProvider.GetRequiredService<IRoutingService>(), path);

    /// <summary>Reads through the cache and waits for the terminal fault the hijacked owner
    /// produces, leaving the entry cached and FAULTED with its upstream still attached.</summary>
    private async Task<Notification<MeshNode>> FaultTheEntry(string path)
    {
        var failure = await Cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(TestTimeouts.Convergence).Match(
                n => n.Kind == NotificationKind.OnError,
                "an owner that cannot answer the SubscribeRequest must surface as OnError");
        MeshNodeStreamCache.IsTransientOwnerFailure(failure.Exception!).Should().BeTrue(
            "precondition: the 'no response received' banner is the TRANSIENT class — the node "
            + "exists and its stream stays live, which is what makes the orphan heartbeat possible");
        MeshNodeStreamCache.IsMissingNodeFailure(failure.Exception!).Should().BeFalse(
            "precondition: the node EXISTS — its owner is unreachable, which is not an absence, so "
            + "no NotFound tears the stream's keep-alive down for us");
        Cache.IsReadStreamLive(path).Should().BeTrue(
            "precondition: the faulted read left its entry cached — that entry is the ONLY handle "
            + "either reclaimer has on the upstream");
        return failure;
    }

    /// <summary>The exact broadcast every post-commit write publishes for a path (and the one
    /// <c>MeshOperations.RecycleCore</c> publishes for a recycle).</summary>
    private void PublishChangeBroadcast(string path)
    {
        var segments = path.Split('/');
        Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>().Publish(new MeshChangeEvent(
            Namespace: segments.Length > 1 ? string.Join("/", segments[..^1]) : "",
            Id: segments.Length > 0 ? segments[^1] : path,
            Path: path,
            Kind: MeshChangeKind.Updated,
            NodeType: MeshNode.NodeTypePath,
            Version: 0,
            Timestamp: DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// THE control arm, and it runs FIRST because it is what makes the subject arm mean anything:
    /// the same faulted-with-live-upstream state, released through the seam that already applies
    /// the #1202 rule, MUST report that it disposed an upstream. If this ever goes red the fault
    /// shape stopped leaving a stream behind and the subject arm below is measuring nothing.
    /// </summary>
    // 240_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant,
    // so the property cannot be written here. The value must still DOMINATE it — 216 s at the
    // CI factor (Convergence 108 s x OuterMargin 2) — or the xunit kill pre-empts the inner
    // wait and the failure cannot say what it was waiting for.
    [Fact(Timeout = 240_000)]
    public async Task ControlArm_ReleaseIfUnwatched_OfTheSameFaultedEntry_DisposesAnUpstream()
    {
        var path = await CreateNodeAsync("control");
        using var owner = Hijack(path);
        await FaultTheEntry(path);

        // Attach BEFORE the release — an eviction is a point event on a hot subject, so observing
        // it must not be able to race the action that produces it.
        var evictions = Cache.ReadStreamEvictions.Where(e => e.Path == path).Replay();
        using var connection = evictions.Connect();

        Cache.ReleaseIfUnwatched(path).Should().BeTrue(
            "nothing is subscribed to the faulted entry, so the release must claim and tear it down");

        var eviction = await evictions.FirstAsync().Should().Within(TestTimeouts.Convergence).Emit();
        eviction.UpstreamReleased.Should().BeTrue(
            "a transiently-faulted read leaves a LIVE upstream sync stream — with its 45 s "
            + "HeartBeatEvent — in the cache hub's workspace. If this is false the scenario no "
            + "longer holds an upstream and the change-feed arm below would pass vacuously");
    }

    /// <summary>
    /// THE pin for #3110. A change-feed event evicts the faulted entry — and must release its
    /// upstream in the same breath. Before the fix this eviction disposed only the Rx hydration
    /// and reported <c>UpstreamReleased: false</c>, orphaning a live sync stream that neither the
    /// idle sweep (which iterates <c>_streams</c>) nor <c>ReleaseIfUnwatched</c> (which looks the
    /// path up in it) could ever reach again — a 45 s heartbeat to a finished activity for the
    /// process lifetime.
    /// </summary>
    // 240_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant,
    // so the property cannot be written here. The value must still DOMINATE it — 216 s at the
    // CI factor (Convergence 108 s x OuterMargin 2) — or the xunit kill pre-empts the inner
    // wait and the failure cannot say what it was waiting for.
    [Fact(Timeout = 240_000)]
    public async Task ChangeFeedReset_OfAFaultedEntry_ReleasesItsUpstreamToo()
    {
        var path = await CreateNodeAsync("changefeed");
        using var owner = Hijack(path);
        await FaultTheEntry(path);

        var evictions = Cache.ReadStreamEvictions.Where(e => e.Path == path).Replay();
        using var connection = evictions.Connect();

        PublishChangeBroadcast(path);

        var eviction = await evictions.FirstAsync().Should().Within(TestTimeouts.Convergence).Emit(
            "a change-feed event must evict the faulted entry so the next read re-probes");
        Cache.IsReadStreamLive(path).Should().BeFalse(
            "the faulted entry is gone — from here on NOTHING in the cache references the path, so "
            + "this eviction was the last chance to release its upstream");
        eviction.UpstreamReleased.Should().BeTrue(
            "the eviction must DETACH and dispose the path's upstream sync streams, not merely "
            + "dispose the Rx hydration. Leaving them attached orphans a live stream in the cache "
            + "hub's workspace whose 45 s HeartBeatEvent keeps the finished node's hub — and its "
            + "sync/ sub-hubs — alive for the process lifetime, unreachable by BOTH reclaimers "
            + "(#3110). This is the same rule #1202 established and EvictFaultedEntry centralises");
    }
}
