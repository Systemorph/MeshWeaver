using System;
using System.Reactive;
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
/// 🚨 A RE-ASK'S NON-TRANSIENT FAILURE IS A VERDICT, NOT A LINE IN THE LOG (issue #3498).
///
/// <para><b>The failure.</b> CD run 7946 died in <c>MeshPluginTest.FullCrudWorkflow_CreateGetUpdateDelete</c>
/// with <c>"never reported the node gone within 20s"</c>. The transcript holds the answer 0.1 s after
/// the delete — <c>RouteMessage: NotFound for RawJson → ACME/CrudTest_…</c> — followed by
/// <c>Stream …: resubscribe failed.</c>, then the same NotFound five seconds later (the keep-alive
/// heartbeat of a stream that was still alive), then nothing until the reader's budget expired. It is
/// #1029's silence on the door #1029 did not close.</para>
///
/// <para><b>The mechanism.</b> <c>JsonSynchronizationStream</c> answers a <c>DeliveryFailure</c> on a
/// <c>SubscribeRequest</c> in two arms that disagreed. The INITIAL subscribe treats every
/// classification but <see cref="ErrorType.ShuttingDown"/> as terminal: it faults the subscribers and
/// disposes the keep-alive. The RE-ASK arm — the one the change-feed latch and the recycle ride-out
/// use — pushed a <c>ShuttingDown</c> back through the re-arm carrier and logged everything else as
/// <c>resubscribe failed</c>, cleared its in-flight flag, and did nothing more: no <c>OnError</c>, no
/// teardown. The stream parked with no value and no error; the heartbeat kept posting to the deleted
/// owner; the reader waited out its budget for an answer that had already arrived.</para>
///
/// <para><b>What is pinned here.</b> The production sequence, with the framework's own parts and no
/// crafted failure: the owner is recycled for real and its address held at the corpse, so the initial
/// SubscribeRequest is refused with the genuine transient <c>ShuttingDown</c> NACK and the ride-out
/// carrier arms a re-ask. That re-ask is answered the way routing answers a deleted node — an
/// authoritative <c>NotFound</c> <c>DeliveryFailure</c> — and the stream MUST fault with it. Nothing
/// else can rescue the unfixed stream: the heartbeat is off, a hub-only mesh has no change feed, and
/// the carrier only re-arms on <c>ShuttingDown</c>. So before the fix the final wait runs out its whole
/// budget on a parked stream — a deterministic red, not a race — and after it the verdict lands
/// milliseconds after the NotFound.</para>
/// </summary>
public class AReAsksNotFoundIsAVerdictTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string OwnerType = "deleted-owner";
    private static readonly Address OwnerAddress = new(OwnerType, "1");

    /// <summary>Short so the ride-out's single re-ask is observable inside a test budget.</summary>
    private static readonly TimeSpan TestReAskPace = TimeSpan.FromMilliseconds(200);

    /// <summary>Long enough that the heartbeat NEVER fires: the only deliveries the owner address sees
    /// are the initial SubscribeRequest and the carrier's re-ask, which is what the counters mean.</summary>
    private static readonly TimeSpan NoHeartbeat = TimeSpan.FromMinutes(5);

    // Instance state — never static, so nothing bleeds between tests or between mesh instances.
    private IMessageHub? corpse;
    private int serveFromCorpse;
    private int corpseRefusals;
    private int answerNotFound;
    private int notFoundAnswers;

    private static MessageHubConfiguration ConfigureOwner(MessageHubConfiguration configuration)
        => configuration.AddData(data => data.AddSource(src => src
            .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))));

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf)
            .WithTypes(typeof(SubscribeRequest), typeof(SubscribeAck))
            .WithRoutes(forward => forward
                .RouteAddress(OwnerType, (address, delivery) =>
                {
                    if (!OwnerAddress.Equals(address))
                        return delivery;
                    if (Volatile.Read(ref serveFromCorpse) == 1 && corpse is not null)
                    {
                        // The residue a recycle leaves: the address still resolves to the activation
                        // that is already Dead, whose own MessageService mints the genuine transient
                        // ShuttingDown NACK. This is what makes the stream arm a re-ask at all.
                        Interlocked.Increment(ref corpseRefusals);
                        corpse.DeliverMessage(delivery);
                        return delivery.Forwarded(corpse.Address);
                    }
                    if (Volatile.Read(ref answerNotFound) == 1)
                    {
                        // The node is gone. Answer exactly as RoutingServiceBase.PostNotFound does for
                        // a deleted path: the routing infrastructure's own NotFound NACK, ResponseFor
                        // the request, and the delivery classified NotFound.
                        Interlocked.Increment(ref notFoundAnswers);
                        Mesh.Post(
                            new DeliveryFailure(delivery)
                            {
                                ErrorType = ErrorType.NotFound,
                                Message = $"No node found at '{address}'. Closest ancestor is '{OwnerType}'.",
                            },
                            o => o.ResponseFor(delivery));
                        return delivery.NotFound();
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
                o.RecycleReAskPace = TestReAskPace;
            }))
            .AddData(data => data.AddHubSource(OwnerAddress, ds => ds.WithType<BusinessUnit>()));

    [HubFact]
    public async Task AReAskAnsweredNotFound_FaultsTheStreamWithTheVerdict_InsteadOfParkingIt()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. Activate the owner, then recycle it for real and let the teardown COMPLETE, so the
        //    re-arm's join is a no-op and the carrier's re-ask fires on the pace alone.
        var owner = Mesh.GetHostedHub(OwnerAddress, ConfigureOwner, HostedHubCreation.Always);
        owner.Should().NotBeNull("the owner hub must exist before it can be recycled");
        var teardownFinished = owner!.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.CrossSilo).Await(ct);
        owner.Post(new DisposeRequest(), o => o.WithTarget(OwnerAddress));
        await teardownFinished;
        owner.RunLevel.Should().Be(MessageHubRunLevel.Dead);

        // 2. Hold the address at the corpse: the initial SubscribeRequest gets the genuine transient
        //    NACK, and the stream's ride-out carrier arms ONE re-ask after the pace.
        corpse = owner;
        Volatile.Write(ref serveFromCorpse, 1);

        // 3. Open the read. AddHubSource posts its SubscribeRequest as the client hub is built.
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        // Subscribe BEFORE the verdict can arrive so the fault is observed, never missed: the stream
        // faults its subscribers; a late subscriber of a faulted stream is a different contract.
        var verdict = workspace.GetStream(typeof(BusinessUnit))
            .Materialize()
            .Where(n => n.Kind == NotificationKind.OnError)
            .Replay(1);
        using var observing = verdict.Connect();

        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref corpseRefusals))
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n >= 1, "the dying activation must refuse the initial SubscribeRequest, which is "
                                + "what arms the ride-out carrier's re-ask");

        // 4. The node is deleted before the re-ask lands: from here the address answers NotFound —
        //    the authoritative verdict routing gives a deleted path.
        Volatile.Write(ref serveFromCorpse, 0);
        Volatile.Write(ref answerNotFound, 1);

        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref notFoundAnswers))
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n >= 1, "the carrier must re-ask after the pace, and that re-ask is the delivery "
                                + "answered NotFound — the exact delivery the unfixed arm dropped");

        // 5. 🚨 THE ASSERTION: the stream faults with the classified verdict. Unfixed, this waits out
        //    its whole budget: the NotFound was logged as "resubscribe failed" and nothing else in
        //    this mesh can ever emit on the stream again.
        await verdict
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Exception is DeliveryFailureException { Failure.ErrorType: ErrorType.NotFound },
                "a re-ask answered NotFound is a verdict about the owner — the node is gone — and must "
                + "fault the stream's subscribers exactly as the initial SubscribeRequest's NotFound "
                + "does; parking the stream silently is #3498 (CD 7946: 'never reported the node gone "
                + "within 20s' with the NotFound in the log 0.1 s after the delete)");

        Output.WriteLine(
            $"DIAG refusals by the corpse: {Volatile.Read(ref corpseRefusals)}; "
            + $"NotFound answers: {Volatile.Read(ref notFoundAnswers)}");
    }
}
