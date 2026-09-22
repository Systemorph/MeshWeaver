using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Regression guard for issue #3432 — <b>the owner-side <c>sync/{id}</c> population must drain when
/// the SUBSCRIBING hub is torn down</b>, not only when the stream is disposed.
///
/// <para>A subscription builds TWO streams and therefore two <c>sync/{id}</c> hubs: the client's,
/// hosted by the subscribing hub, and the owner's twin, hosted by the owner. The ONLY thing that
/// ends the owner's twin is the <c>UnsubscribeRequest</c> that
/// <c>JsonSynchronizationStream.CreateExternalClient</c> registers for disposal (nothing else
/// disposes a server-side stream). On the <b>stream-dispose</b> route that request leaves through a
/// live subscribing hub — <c>UserActionOutlivesStreamReleaseTest.AnOrdinaryReleaseIsPrompt</c> pins
/// it. On the <b>hub-teardown</b> route — a Blazor circuit ending, a <c>DisposeRequest</c>, a
/// recycle, i.e. the route production actually takes — the subscribing hub is itself inside
/// <c>DisposeHostedHubs</c> when its hosted <c>sync/{id}</c> reaches <c>ShutDown</c> and runs the
/// release. Its own <c>Post</c> is then refused by the teardown guard in
/// <c>MessageService.PostImplGeneric</c> (everything but <c>ShutdownRequest</c>/<c>DisposeRequest</c>
/// and a correlated reply), so the farewell never leaves, the owner is never told, and its twin
/// stays at <c>RunLevel=Started</c> for the life of the process holding its Autofac scope and its
/// TypeRegistry (~390 KB each).</para>
///
/// <para><b>Both directions are asserted.</b> <see cref="DisposingTheSubscribingHubDrainsTheOwnerSidePopulation"/>
/// goes red on the defect — the owner-side hubs never reach <c>DisposalCompleted</c>.
/// <see cref="TheSubscribingHubsOwnTeardownStillCompletes"/> goes red on the lazy "fix" of holding
/// the subscribing hub's teardown open until the owner has answered, which would satisfy the first
/// test on its own while wedging every circuit close.</para>
/// </summary>
public class SubscriberTeardownReleasesTheOwnerSyncHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "TeardownRelease";
    private const string ButtonArea = Area + "/Button";

    /// <summary>
    /// How many remote streams one subscribing hub opens. More than one on purpose: the defect is a
    /// POPULATION that never drains, and a single hub cannot tell "the release reached the owner"
    /// apart from "one hub happened to go away".
    /// </summary>
    private const int StreamsPerSubscriber = 3;

    private static UiControl View()
        => Controls.Stack.WithView(Controls.Html("Run"), "Button");

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(Area, View()));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [HubFact]
    public async Task DisposingTheSubscribingHubDrainsTheOwnerSidePopulation()
        => await AssertSubtreeReleasesOwnerStreams(0);

    [HubFact]
    public async Task DisposingANestedSubtreeDrainsTheSurvivingOwnersPopulation()
        => await AssertSubtreeReleasesOwnerStreams(1);

    private async Task AssertSubtreeReleasesOwnerStreams(int nestedParents)
    {
        var subtree = GetClient();
        var client = subtree;
        foreach (var index in Enumerable.Range(0, nestedParents))
            client = client.GetHostedHub(
                new Address("nested-subscriber", index.ToString()),
                c => c.WithPostingIdentity(PostingIdentity.System).AddLayoutClient(d => d));
        var ownerSyncHubs = await SubscribedOwnerSyncHubs(client);

        // The hub-teardown route: nobody disposes a stream. This is what a circuit close, a
        // DisposeRequest and a recycle all do.
        subtree.Dispose();

        await subtree.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the subscribing hub must finish its own teardown — until it has, nothing can be said "
            + "about what that teardown did or did not tell the owner");

        await Observable.Zip(ownerSyncHubs.Select(hub => hub.DisposalCompleted))
            .Should().Within(TestTimeouts.Convergence).Emit(
                "every owner-side sync/{id} must be released when the hub that subscribed to it is "
                + "torn down — it is the ONLY thing an UnsubscribeRequest ends, and a farewell the "
                + "subscribing hub refuses to carry leaves one Started hub per subscription alive "
                + "for the life of the process (#3432)");
    }

    /// <summary>
    /// The control that a fix which simply HOLDS the subscribing hub's teardown open — waiting for
    /// the owner to acknowledge — must fail. Teardown lets accepted work finish; it never waits on
    /// a peer, and a circuit close that blocks on a remote answer is how a portal wedges.
    /// </summary>
    [HubFact]
    public async Task TheSubscribingHubsOwnTeardownStillCompletes()
    {
        var client = GetClient();
        await SubscribedOwnerSyncHubs(client);

        client.Dispose();

        await client.DisposalCompleted.Should().Within(TestTimeouts.Quick).Emit(
            "a subscribing hub's teardown must complete on its own schedule — it announces its "
            + "departure, it does not wait to be answered");
    }

    /// <summary>
    /// <see cref="StreamsPerSubscriber"/> remote streams on <paramref name="client"/>, each with its
    /// owner-side <c>sync/{id}</c> sub-hub live and holding the area's stream-scoped handlers — the
    /// population whose draining is the subject.
    /// </summary>
    private async Task<ImmutableList<IMessageHub>> SubscribedOwnerSyncHubs(IMessageHub client)
    {
        var host = GetHost();
        var ownerSyncHubs = ImmutableList<IMessageHub>.Empty;
        foreach (var index in Enumerable.Range(0, StreamsPerSubscriber))
        {
            var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
                CreateHostAddress(), new LayoutAreaReference(Area) { Id = $"view-{index}" });

            await stream.GetControlStream(ButtonArea).Should().Within(TestTimeouts.Convergence)
                .Match(control => control is not null,
                    "the owner-side LayoutAreaHost and its per-subscriber stream must be live "
                    + "before anything about releasing them can be measured",
                    cancellationToken: TestContext.Current.CancellationToken);

            var ownerSyncHub = host.GetHostedHub(
                SynchronizationAddress.Create(stream.StreamId), HostedHubCreation.Never);
            ownerSyncHub.Should().NotBeNull(
                "the owner hosts one sync/{id} sub-hub per subscriber, and it is what the "
                + "UnsubscribeRequest disposes — without it this test measures nothing");
            ownerSyncHubs = ownerSyncHubs.Add(ownerSyncHub!);
        }

        ownerSyncHubs.Should().HaveCount(StreamsPerSubscriber,
            "the denominator is stated, not assumed: a population that was never built cannot "
            + "fail to drain");
        return ownerSyncHubs;
    }
}
