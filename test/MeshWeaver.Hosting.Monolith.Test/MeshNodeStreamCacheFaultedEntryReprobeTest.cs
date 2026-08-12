using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the fix for #1202 (and its symptom reports #1194/#1195/#1196/#1197/#1203): ONE failed
/// owner subscribe must not make a path permanently unreadable through the shared cache.
///
/// <para><b>The live defect.</b> On memex-cloud a point read of <c>DataModeling/Formatting</c>
/// failed instantly, forever, by replaying a cached <c>TimeoutException</c> from an earlier failed
/// subscribe. The decisive evidence: three reads spanning ELEVEN MINUTES all returned the
/// byte-identical request id <c>3kjIJ9lXPUCqPU5Zkgmhjw</c>. A genuine re-probe allocates a fresh
/// request id every time, so after the first failure no further <c>SubscribeRequest</c> was ever
/// issued for that path — and that is exactly what these tests measure.</para>
///
/// <para><b>The mechanism.</b> The bookkeeping observer marks the entry <c>Faulted</c> and records
/// the failure, but a fault that is not a genuine missing node opens NO backoff window until the
/// streak passes the grace. <c>GetEntry</c> gates only on <c>TryTouch()</c> (evicted?), never on
/// <c>IsFaulted</c> — so the next read was handed the SAME entry, whose <c>Replay(1)</c>
/// re-delivered the SAME exception instance. Because no new upstream was ever opened, the
/// bookkeeping observer never fired again, the fail counter stayed frozen at the value the first
/// fault wrote, and the breaker's own eviction branch (which needs a count past the grace) was dead
/// by construction. The two escapes could not fire either: a change-feed invalidation needs a WRITE
/// on a path nobody writes, and the 10-minute idle sweep is reset by every read's <c>Touch()</c>.
/// </para>
///
/// <para><b>The contract under test.</b> A faulted entry is never served twice: with no breaker
/// window open, the next read EVICTS it and opens a genuinely NEW upstream (a new request id).
/// While a breaker window IS open the entry is left alone and the read fast-fails without opening
/// anything, so the storm / transient breakers keep bounding a persistently-failing owner exactly
/// as before.</para>
/// </summary>
public class MeshNodeStreamCacheFaultedEntryReprobeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    /// <summary>
    /// Stands in as the OWNER of one throwaway node path, through the framework's own
    /// <see cref="IRoutingService.RegisterStream(Address, SyncDelivery)"/> seam — the same call
    /// every hub makes for itself. <c>MonolithRoutingService.RouteImpl</c> consults the stream
    /// registry FIRST, so the node's real per-node hub is never activated and every
    /// <c>SubscribeRequest</c> the cache opens for the path lands here instead.
    ///
    /// <para>Each attempt is answered with the verbatim production banner
    /// (<c>MessageHub.BuildTimeoutMessage</c>) carrying a FRESH request id, and the ids are
    /// recorded. That makes the incident's signature directly measurable: a second read that
    /// records no new id — and returns the first id's error — is a replay, not a re-probe.</para>
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
                // Everything else — the cache's SubscribeRequest, typed or already serialised to
                // RawJson by the monolith transport — is an ATTEMPT to reach this owner.
                if (delivery.Message is DeliveryFailure
                    || delivery.Message.GetType().HasAttribute<CanBeIgnoredAttribute>())
                    return delivery.Processed();
                var requestId = Guid.NewGuid().ToString("N")[..12];
                ImmutableInterlocked.Update(ref requestIds, ids => ids.Add(requestId));
                var banner = Banner(path, requestId);
                // Answer the sender exactly as MonolithRoutingService.PostNotFound does — post the
                // DeliveryFailure ourselves, then mark the delivery FailedAndNacked so nothing
                // NACKs it a second time. ErrorType.Exception, not ShuttingDown: a shutdown reject
                // is ridden out by JsonSynchronizationStream's resubscribe latch by design; this is
                // the owner-unreachable class that TERMINATES the subscribe — the production shape.
                using (delivery.AccessContext is null ? access?.ImpersonateAsSystem() : null)
                    mesh.Post(
                        new DeliveryFailure(delivery) { ErrorType = ErrorType.Exception, Message = banner },
                        o => o.ResponseFor(delivery));
                return delivery.FailedAndNacked(banner);
            };
            registration = routing.RegisterStream(new Address(path), answer);
        }

        /// <summary>One entry per delivery that actually reached this owner — i.e. per upstream
        /// subscribe ATTEMPT the cache opened for the path.</summary>
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
        await NodeFactory.CreateNode(node).Should().Within(60.Seconds()).Emit();
        return path;
    }

    private Task<Notification<MeshNode>> ReadFailure(string path, string because) =>
        Cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(30.Seconds()).Match(n => n.Kind == NotificationKind.OnError, because);

    private UnreachableOwner Hijack(string path) =>
        new(Mesh, Mesh.ServiceProvider.GetRequiredService<IRoutingService>(), path);

    /// <summary>
    /// THE pin, measured exactly as the live probes measured it. Two successive reads of a path
    /// whose owner cannot answer must issue TWO SubscribeRequests with DIFFERENT ids. Before the
    /// fix the second read issued none at all and replayed the first one's cached exception —
    /// forever, for the process lifetime.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FaultedEntry_IsNotServedTwice_NextReadOpensANewUpstream()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("poison");
        using var owner = Hijack(path);

        var first = await ReadFailure(path,
            "an owner that cannot answer the SubscribeRequest must surface as OnError");
        var afterFirst = owner.RequestIds;
        afterFirst.Should().NotBeEmpty("the read must have reached the owner at least once");
        first.Exception!.Message.Should().Contain(afterFirst[^1],
            "the surfaced error must be the one this SubscribeRequest produced");

        // Precondition: this is the poisoning class. A transient owner failure is deliberately
        // NOT negative-cached, and within the grace it opens no window either — so nothing
        // suppresses the path and the ONLY thing standing between the reader and a fresh probe
        // is the faulted entry itself.
        MeshNodeStreamCache.IsTransientOwnerFailure(first.Exception!).Should().BeTrue(
            "precondition: the 'no response received' banner is the transient class (#1202's shape)");
        MeshNodeStreamCache.IsMissingNodeFailure(first.Exception!).Should().BeFalse(
            "precondition: the node EXISTS — its owner is unreachable, which is not an absence");
        cache.IsReadStreamLive(path).Should().BeTrue(
            "precondition: the failed read left its entry cached, holding the terminal error");

        var second = await ReadFailure(path, "the re-probe must reach a terminal verdict too");

        owner.RequestIds.Count.Should().BeGreaterThan(afterFirst.Count,
            "the second read must issue a NEW SubscribeRequest. Serving the cached faulted entry "
            + "issues none — that is the byte-identical request id seen across eleven minutes on "
            + "memex-cloud (#1202), and it never healed.");
        second.Exception.Should().NotBeSameAs(first.Exception,
            "a replayed exception INSTANCE is the poison: the entry's Replay(1) terminal, not a "
            + "fresh failure from a fresh probe");
        second.Exception!.Message.Should().Contain(owner.RequestIds[^1],
            "the second read's error must carry the SECOND request's id");
    }

    /// <summary>
    /// The same invariant stated on the cache's own eviction seam: the read that finds a faulted
    /// entry with no open breaker window must DROP it (reason <c>faulted</c>) — that eviction is
    /// what lets a fresh upstream be opened at all.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FaultedEntry_IsEvicted_OnTheNextRead()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("evict");
        using var owner = Hijack(path);

        await ReadFailure(path, "the first read must fault");
        cache.IsReadStreamLive(path).Should().BeTrue("the faulted entry is still cached");

        // Attach BEFORE the read that must evict — an eviction is a point event on a hot subject,
        // so observing it must not be able to race the action that produces it. Replay().Connect()
        // establishes the subscription synchronously, on this thread.
        var evictions = cache.ReadStreamEvictions
            .Where(e => e.Path == path && e.Reason == "faulted")
            .Replay();
        using var connection = evictions.Connect();

        await ReadFailure(path, "the second read must reach a terminal verdict");

        await evictions.FirstAsync().Should().Within(30.Seconds()).Emit(
            "a faulted entry with no breaker window open must be evicted so the read re-probes");
    }

    /// <summary>
    /// The other half of the contract — do NOT regress the breaker. With a transient streak past
    /// the grace the window is open: the read must fast-fail by replaying the RECORDED error,
    /// leaving the entry alone and opening NO upstream. This is the bound that stops a
    /// persistently-faulting owner from being re-probed once per read (the 2026-07-21 poisoned
    /// activation that leaked 4→22&#160;GiB in twelve minutes).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FaultedEntry_InsideAnOpenBreakerWindow_StillShortCircuits()
    {
        var cache = Cache;
        var path = await CreateNodeAsync("window");
        using var owner = Hijack(path);

        await ReadFailure(path, "the first read must fault and leave a faulted entry");
        cache.IsReadStreamLive(path).Should().BeTrue("precondition: the faulted entry is cached");
        var beforeSuppression = owner.RequestIds.Count;

        // Ten recordings put the window at 1s·2^6 = 64s ⇒ capped at 60s — impossible to outwait
        // within the test budget, exactly like the production loop.
        var recorded = new TimeoutException("owner wedged — recorded by the transient breaker");
        for (var i = 0; i < 10; i++)
            cache.RecordTransient(path, recorded);

        var suppressed = await ReadFailure(path, "an open breaker window must fast-fail the read");
        suppressed.Exception.Should().BeSameAs(recorded,
            "an open window replays the RECORDED error — the faulted-entry guard must never "
            + "pre-empt a breaker that is actively suppressing");
        owner.RequestIds.Count.Should().Be(beforeSuppression,
            "a suppressed read must not open an upstream SubscribeRequest — that IS the bound");
        cache.IsReadStreamLive(path).Should().BeTrue(
            "a fast-failed read must leave the entry alone: eviction is for reads that re-probe");
    }
}
