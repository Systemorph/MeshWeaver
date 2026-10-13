using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// <b>A recycle tells each subscriber ONCE per owner, not once per stream</b> -- Systemorph/MeshWeaver#5532.
///
/// <para><b>What was measured.</b> memex-cloud, pod memex-portal-deployment-b79b66b68-w488f,
/// 2026-10-11T19:21:35.0Z to 19:21:35.2Z: a RecycleCascade requested by
/// portal/nodeops-ZjzXQ4RCv0K2l430tkdI5A (a live module swap of MeshWeaver.OgCard) recycled the
/// hubs LinkedIn, RemoteControl, Edu, Reinsurance, Hosting and more. 0.66 s later that same hub
/// logged ACTION-BLOCK OVERLOAD at inbound queue depth 30907, shedding StreamEndedEvent, and
/// mesh/ZjzXQ4RCv0K2l430tkdI5A followed at depth 44118.</para>
///
/// <para><b>Why one hub received all of it.</b> Every recycled owner runs
/// <c>Workspace.AnnounceRecycleToClientSubscriptions</c>, which posts one <c>StreamEndedEvent</c>
/// per live client subscription -- one per (subscriber, stream) pair -- and posts every one of them
/// through the same carrier: for a hub under the mesh router, the router's <c>RouterCarrier</c>
/// spokesman, which on a mesh is the nodeops hub. A subscriber address that holds many streams of
/// one owner (a circuit, a cache hub) is told once per stream, and a cascade over many owners puts
/// the sum of all their streams on that one single-threaded queue within a second.</para>
///
/// <para><b>What is pinned.</b> One subscriber holds <c>StreamsPerOwner</c> streams on each of
/// <c>Owners</c> owners, and all owners are recycled. Counted as the messages LEAVE the carrier (its
/// post pipeline), the subscriber must be told at most once per owner, whatever the number of
/// streams. On the unfixed code the carrier posts Owners x StreamsPerOwner messages.</para>
///
/// <para><b>The control, and it is not optional.</b> Every one of the mirrors must still re-ask its
/// owner after the recycle. Without it the count above would also pass on a build that deleted the
/// announcement outright -- the stranding #2533 and #3986 fixed. The number of re-asks that cross
/// the router is written to the output: there is one per stream, and this test does not claim to
/// change that.</para>
/// </summary>
public class RecycleCascadeAnnouncementFanOutTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>A dedicated address type for the recycled owners, hosted by the mesh router as a per-node hub is.</summary>
    private const string OwnerType = "fanout-owner";

    /// <summary>How many owners the cascade recycles.</summary>
    private const int Owners = 3;

    /// <summary>How many streams the ONE subscriber holds on each owner.</summary>
    private const int StreamsPerOwner = 20;

    /// <summary>
    /// The mesh's designated non-router spokesman. Production's MeshBuilder names the nodeops hub;
    /// this fixture names its own, or the announcement correctly declines to speak through the router
    /// and there would be nothing to count.
    /// </summary>
    private static readonly Address CarrierAddress = new("fanout-carrier", "1");

    /// <summary>Long enough that no heartbeat fires, so every re-ask counted below answers the recycle.</summary>
    private static readonly TimeSpan NoHeartbeat = TimeSpan.FromMinutes(5);

    // Instance state -- never static, so nothing bleeds between tests.
    private int recycling;
    private int reAsksAfterRecycle;
    // Each DISTINCT mirror that re-asked, as (owner address, stream id): an aggregate count alone
    // passes when a subset of mirrors loops while the rest stay stranded.
    private readonly ConcurrentDictionary<(string Owner, string StreamId), int> reAskedMirrors = new();
    private readonly ConcurrentDictionary<string, int> postedByCarrier = new();

    private static MessageHubConfiguration ConfigureOwner(MessageHubConfiguration configuration)
        => configuration.AddData(data => data.AddSource(src => src
            .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))));

    /// <summary>
    /// Passive counter on the carrier's outbound path: once the recycle has started it counts what the
    /// carrier posts, per target, and never alters it.
    /// </summary>
    private MessageHubConfiguration ConfigureCarrier(MessageHubConfiguration configuration)
        => configuration.AddPostPipeline(p => p.AddPipeline((d, next) =>
        {
            if (Volatile.Read(ref recycling) == 1
                && d.Target is { } target
                && !target.Equals(CarrierAddress))
                postedByCarrier.AddOrUpdate(target.ToString(), 1, (_, n) => n + 1);
            return next.Invoke(d);
        }));

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf)
            // The router relays the re-asks and the announcements, so their types are registered here.
            .WithTypes(typeof(SubscribeRequest), typeof(SubscribeAck), typeof(StreamEndedEvent))
            .Set(new RouterCarrier(router => router.GetHostedHub(CarrierAddress, ConfigureCarrier)))
            .WithRoutes(forward => forward
                // Counts, never handles: every SubscribeRequest that reaches an owner address after
                // the recycle started is a re-ask, and returning the delivery untouched hands it to
                // the hosted-hub route below, which re-creates the owner -- the reactivation.
                .RouteAddress(OwnerType, (_, delivery) =>
                {
                    if (Volatile.Read(ref recycling) == 1 && delivery.Message is SubscribeRequest reAsk)
                    {
                        Interlocked.Increment(ref reAsksAfterRecycle);
                        reAskedMirrors.AddOrUpdate(
                            (delivery.Target?.ToString() ?? string.Empty, reAsk.StreamId), 1, (_, n) => n + 1);
                    }
                    return delivery;
                })
                .RouteAddressToHostedHub(OwnerType, ConfigureOwner));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services => services.Configure<SyncStreamOptions>(o =>
            {
                o.HeartbeatInterval = NoHeartbeat;
                o.FirstHeartbeat = NoHeartbeat;
            }))
            .AddData(data => data.AddSource(ds => ds.WithType<BusinessUnit>(t => t)));

    /// <summary>
    /// THE REGRESSION. One subscriber, Owners x StreamsPerOwner live streams, every owner recycled
    /// at once. RED before the fix: the carrier posts one StreamEndedEvent per stream.
    /// </summary>
    [HubFact]
    public async Task ARecycleTellsEachSubscriberOncePerOwner_NotOncePerStream()
    {
        var ct = TestContext.Current.CancellationToken;

        // The carrier exists BEFORE the recycle, so the counter is installed when the first
        // announcement is posted (the RouterCarrier factory returns this same hub).
        var carrier = Mesh.GetHostedHub(CarrierAddress, ConfigureCarrier);
        carrier.Should().NotBeNull("the carrier has to exist for its outbound path to be counted");
        await carrier!.Started.WaitAsync(TestTimeouts.Convergence, ct);

        var owners = new List<IMessageHub>();
        for (var i = 1; i <= Owners; i++)
        {
            var owner = Mesh.GetHostedHub(
                new Address(OwnerType, i.ToString()), ConfigureOwner, HostedHubCreation.Always);
            owner.Should().NotBeNull("each owner is hosted by the mesh router, as a per-node hub is");
            owners.Add(owner!);
        }
        var collectionName = owners[0].GetWorkspace().DataContext
            .GetTypeSource(typeof(BusinessUnit))!.CollectionName;

        // ONE subscriber address holding many streams of each owner. GetRemoteStreamAsHub does not
        // go through the remote-stream cache, so each call is its own subscription with its own
        // stream id, as distinct references opened by one circuit or one cache hub are.
        var client = GetClient();
        var workspace = (Workspace)client.ServiceProvider.GetRequiredService<IWorkspace>();
        var streams = new List<ISynchronizationStream<EntityStore>>();
        try
        {
            foreach (var owner in owners)
                for (var k = 0; k < StreamsPerOwner; k++)
                    streams.Add(workspace.GetRemoteStreamAsHub(
                        owner.Address, new CollectionsReference(collectionName)));

            foreach (var stream in streams)
                await stream.Should().Within(TestTimeouts.Convergence).Emit(
                    "every mirror must be live, and so registered on its owner, before the recycle "
                    + "whose announcement is counted");

            // THE CASCADE: every owner recycled with the routed DisposeRequest a recycle posts.
            Volatile.Write(ref recycling, 1);
            foreach (var owner in owners)
                owner.Post(new DisposeRequest(), o => o.WithTarget(owner.Address));
            foreach (var owner in owners)
                await owner.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
                    "each recycled owner must finish its teardown -- its announcement is delivered off it");

            // THE CONTROL: every DISTINCT mirror was told and re-asked. Counted per (owner, stream
            // id), never in aggregate: a retry loop on a few mirrors would satisfy a total while the
            // others stay stranded. Reaching this also means every announcement has been posted, so
            // the carrier count below is complete.
            await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Select(_ => reAskedMirrors.Count)
                .Should().Within(TestTimeouts.Convergence)
                .Match(n => n >= Owners * StreamsPerOwner,
                    "every one of the Owners x StreamsPerOwner mirrors must re-ask its owner -- an "
                    + "announcement that reaches fewer mirrors strands the rest on a dead activation "
                    + "(#2533, #3986)");
            reAskedMirrors.Keys.Select(k => k.Owner).Distinct().Should().HaveCount(Owners,
                "the re-asks must come back to every recycled owner, not pile onto one");

            var perTarget = postedByCarrier.ToArray();
            Output.WriteLine(
                "DIAG carrier posts per target after the recycle: "
                + string.Join(", ", perTarget.Select(kv => kv.Key + "=" + kv.Value)));
            Output.WriteLine(
                $"DIAG re-asks that crossed the router after the recycle: {Volatile.Read(ref reAsksAfterRecycle)} "
                + $"from {reAskedMirrors.Count} distinct mirrors");

            perTarget.Length.Should().BeGreaterThan(0,
                "the recycle must be announced through the carrier -- the mirrors above were told by it");
            perTarget.Max(kv => kv.Value).Should().BeLessThanOrEqualTo(Owners,
                "a subscriber must be told at most once per recycled owner, whatever the number of "
                + "streams it held there. One message per stream puts the sum of every recycled "
                + "owner's streams on the carrier's single-threaded queue: on memex-cloud on "
                + "2026-10-11 that was depth 30907 on portal/nodeops-ZjzXQ4RCv0K2l430tkdI5A, shedding "
                + "StreamEndedEvent (#5532)");
        }
        finally
        {
            foreach (var stream in streams)
                stream.Dispose();
        }
    }

    /// <summary>
    /// THE ROLLING-DEPLOY CONTRACT of the coalescing. Only a subscription whose subscribe DECLARED
    /// <c>AcceptsCoalescedStreamEnd</c> is folded into one end; every other one (a JS or Python
    /// client, a pod on the previous image) keeps one plain <see cref="StreamEndedEvent"/> per
    /// stream with no <c>AlsoEndedStreamIds</c>, because a subscriber that does not know the
    /// property handles <c>StreamId</c> alone and would strand the rest. The end-to-end test above
    /// only mints declaring subscribers, so it cannot see this branch; this pins it on the one
    /// seam both paths go through.
    /// </summary>
    [Fact]
    public void AnUndeclaredSubscriberKeepsOneEndPerStream_ADeclaredOneGetsOne()
    {
        var declaring = new Address("circuit", "declaring");
        var legacy = new Address("circuit", "legacy");
        var subscriptions = new (Address, string, bool)[]
        {
            (declaring, "d1", true), (declaring, "d2", true), (declaring, "d3", true),
            (legacy, "l1", false), (legacy, "l2", false), (legacy, "l3", false),
        };

        var ends = Workspace.CoalesceRecycleEnds(subscriptions);

        var toLegacy = ends.Where(e => e.Subscriber.Equals(legacy)).Select(e => e.Ended).ToArray();
        // A subscriber that did not declare the capability is told once per stream, exactly as
        // before the coalescing existed.
        toLegacy.Select(e => e.StreamId).OrderBy(id => id, StringComparer.Ordinal).ToArray()
            .Should().Equal("l1", "l2", "l3");
        toLegacy.Should().OnlyContain(e => e.AlsoEndedStreamIds == null,
            "an undeclared subscriber skips AlsoEndedStreamIds as unmapped, so it must never carry one");

        var toDeclaring = ends.Where(e => e.Subscriber.Equals(declaring)).Select(e => e.Ended).ToArray();
        toDeclaring.Should().ContainSingle("a declaring subscriber is told once for all its streams");
        // The one coalesced end names every stream the subscriber held, none twice.
        new[] { toDeclaring[0].StreamId }.Concat(toDeclaring[0].AlsoEndedStreamIds ?? [])
            .OrderBy(id => id, StringComparer.Ordinal).ToArray()
            .Should().Equal("d1", "d2", "d3");
    }
}
