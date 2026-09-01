using System;
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
/// 🚨 A READ WHOSE OWNER IS MID-RECYCLE MUST CONVERGE, NOT GIVE UP SILENTLY (issue #2986).
///
/// <para><b>The failure.</b> <c>ImportTypeBeforeInstanceTest</c> timed out on CI run 33523142249
/// reading a node whose per-node hub was being recycled by the overlay self-heal — a recycle that
/// is BY DESIGN, so the read had to ride it out. Instead the transcript shows three
/// <c>resubscribe failed</c> lines, all carrying the SAME owner activation <c>#017DA86C</c> at
/// <c>RunLevel=Dead</c>, within ONE millisecond — and then nothing at all for the remaining ~55 s,
/// ending in a bare <c>TimeoutException</c> that named none of it. The import itself had already
/// reported <c>Imported / failed=0</c>: the defect is entirely in the read path.</para>
///
/// <para><b>Why the latch stopped trying.</b> <c>JsonSynchronizationStream</c>'s recycle re-arm
/// gates each re-ask on the rejecting instance's <c>DisposalCompleted</c> so the attempt lands on a
/// state that can answer (#1360). But <c>DisposalCompleted</c> is signalled AFTER
/// <c>RunLevel = Dead</c>, so an activation that has already reached Dead satisfies that join
/// instantly — and it can still be the instance routing hands the delivery to. The join is then a
/// no-op, the re-ask returns to the same corpse at memory speed, and the whole
/// <c>MaxRecycleReArms</c> budget — a counter only a SUCCESSFUL resubscribe resets — is spent
/// inside one teardown window. A pure recycle writes nothing, so the change-feed latch (the only
/// other trigger) never fires, and the stream is orphaned for good.</para>
///
/// <para><b>What is pinned here.</b> The owner is recycled for real and its teardown is allowed to
/// COMPLETE, so the test drives exactly the production state: an activation at <c>RunLevel=Dead</c>
/// whose <c>DisposalCompleted</c> has already fired. Routing is then held pointed at that corpse
/// (the residue the CI transcript shows) while the client opens its read, so every SubscribeRequest
/// is refused with the transient <see cref="ErrorType.ShuttingDown"/> NACK the real
/// <c>MessageService</c> mints, activation tag and all — no crafted failure, no mock. Once the
/// corpse has refused MORE attempts than the activation budget allows, the address is released and
/// the read MUST converge.</para>
///
/// <para><b>Before the fix this is a deterministic RED:</b> the four refusals exhaust
/// <c>MaxRecycleReArms</c> in microseconds, nothing re-asks after the address reactivates, and the
/// final wait runs out its full budget with an empty workspace. After the fix a refusal from the
/// activation that already refused us charges the TIME budget instead of the ACTIVATION budget and
/// rests before retrying, so the ride-out outlives the teardown and the very next paced re-ask
/// lands on the fresh activation.</para>
/// </summary>
public class RecycleReAskRidesOutTheDyingActivationTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// A dedicated address TYPE for the recycling owner, so the mesh can re-create it from the same
    /// configuration after its activation dies — exactly as a per-node hub is re-created from its
    /// node. Routing to a hosted hub is the framework's own path; nothing here is hand-rolled.
    /// </summary>
    private const string OwnerType = "recycling-owner";

    private static readonly Address OwnerAddress = new(OwnerType, "1");

    /// <summary>
    /// The budget the latch spends on distinct owner activations
    /// (<c>JsonSynchronizationStream.MaxRecycleReArms</c>) plus the initial SubscribeRequest. On
    /// the unfixed code this is EXACTLY how many deliveries the corpse ever sees; the test releases
    /// the address only once that many have been refused, so both the broken and the fixed
    /// behaviour reach the release on a positive signal rather than on a sleep.
    /// </summary>
    private const int RefusalsThatExhaustTheOldBudget = 4;

    /// <summary>
    /// Short so the paced ride-out is observable inside a test budget; production rests 500 ms.
    ///
    /// <para>🚨 Chosen for OBSERVATION HEADROOM, not to make anything pass. The release below fires
    /// once the corpse has refused <see cref="RefusalsThatExhaustTheOldBudget"/> attempts, which
    /// costs two pace intervals; the ride-out itself is capped at
    /// <c>MaxSameActivationReAsks</c> (16) intervals. 200 ms puts ~400 ms of work inside a ~3.2 s
    /// window — an 8× margin in ABSOLUTE wall-clock, so a 5×-oversubscribed runner cannot slide the
    /// observation past the cap and make the test read as "the latch gave up" when it did not.
    /// Neither bound moved; only the test's clock granularity did.</para>
    /// </summary>
    private static readonly TimeSpan TestReAskPace = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Long enough that the heartbeat NEVER fires during the test: every delivery the corpse
    /// refuses is then a SubscribeRequest from the read path, which is what the counts below mean.
    /// </summary>
    private static readonly TimeSpan NoHeartbeat = TimeSpan.FromMinutes(5);

    // Instance state — never static, so nothing bleeds between tests or between mesh instances.
    private IMessageHub? corpse;
    private int serveFromCorpse;
    private int corpseRefusals;
    private int released;
    private int attemptsAfterReactivation;

    private static MessageHubConfiguration ConfigureOwner(MessageHubConfiguration configuration)
        => configuration.AddData(data => data.AddSource(src => src
            .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))));

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf)
            // The router relays the read's SubscribeRequest to the corpse, so the type must be
            // registered here — otherwise the polymorphic resolver auto-registers it under a short
            // name and says so at Warning on every run.
            .WithTypes(typeof(SubscribeRequest), typeof(SubscribeAck))
            .WithRoutes(forward => forward
                // 🚨 THE RESIDUE THE CI TRANSCRIPT SHOWS: the address still resolves to the
                // activation that is already Dead. While the flag is set every delivery for the
                // owner goes to that instance, whose own MessageService answers the real transient
                // ShuttingDown NACK (activation tag included). Cleared, this handler returns the
                // delivery untouched so the ordinary hosted-hub route below re-creates the owner —
                // the reactivation the NACK promises.
                .RouteAddress(OwnerType, (address, delivery) =>
                {
                    if (Volatile.Read(ref serveFromCorpse) == 0
                        || corpse is null
                        || !OwnerAddress.Equals(address))
                    {
                        // 🚨 THE LOAD-INDEPENDENT DISCRIMINATOR. Once the address has been released,
                        // every delivery that arrives here is proof the latch made a FURTHER
                        // ATTEMPT. On the unfixed code this counter can never leave zero, no matter
                        // how long or how slowly the machine runs: the budget is spent and nothing
                        // re-asks. That is a structural fact about the latch, not a timing fact, and
                        // it is what this test really asserts.
                        if (Volatile.Read(ref released) == 1 && OwnerAddress.Equals(address))
                            Interlocked.Increment(ref attemptsAfterReactivation);
                        return delivery;
                    }
                    Interlocked.Increment(ref corpseRefusals);
                    corpse.DeliverMessage(delivery);
                    return delivery.Forwarded(corpse.Address);
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
    public async Task ReadOfAMidRecycleOwner_RidesTheNackOut_AndConvergesOnReactivation()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. Activate the owner, then recycle it for real and let the teardown COMPLETE — the exact
        //    production state: RunLevel=Dead with DisposalCompleted already signalled, which is what
        //    turns the re-arm's join into a no-op.
        var owner = Mesh.GetHostedHub(OwnerAddress, ConfigureOwner, HostedHubCreation.Always);
        owner.Should().NotBeNull("the owner hub must exist before it can be recycled");

        var teardownFinished = owner!.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.CrossSilo).Await(ct);
        // The self-DisposeRequest is the production recycle shape (NodeTypeRebindWatcher and the
        // overlay self-heal both post it to the instance's own address); posting it from the mesh
        // hub instead would make the ROUTER execute work, which the router-traffic guard reports.
        owner.Post(new DisposeRequest(), o => o.WithTarget(OwnerAddress));
        await teardownFinished;
        owner.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "the re-arm's join is only a no-op once DisposalCompleted has fired, which happens AFTER "
            + "RunLevel=Dead — that ordering is the whole of #2986");

        // 2. Keep the address pointed at the corpse. Every read attempt now gets the genuine
        //    transient NACK, from the genuine MessageService, carrying one stable activation tag.
        corpse = owner;
        Volatile.Write(ref serveFromCorpse, 1);

        // 3. Open the read. AddHubSource posts its SubscribeRequest as the client hub is built.
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        // 4. Wait — on the actual condition, no sleep — until the corpse has refused MORE attempts
        //    than the distinct-activation budget can pay for. On the unfixed code this is the last
        //    thing that ever happens on this stream.
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref corpseRefusals))
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n >= RefusalsThatExhaustTheOldBudget,
                $"the dying activation must refuse at least {RefusalsThatExhaustTheOldBudget} "
                + "attempts — one initial SubscribeRequest plus the whole MaxRecycleReArms budget — "
                + "so the release below happens strictly AFTER the point where the unfixed latch "
                + "stops trying");
        Output.WriteLine($"DIAG refusals by the dead activation: {Volatile.Read(ref corpseRefusals)}");

        // 5. The address reactivates, exactly as the NACK promised it might.
        Volatile.Write(ref released, 1);
        Volatile.Write(ref serveFromCorpse, 0);

        // 6. 🚨 THE STRUCTURAL ASSERTION, and the one that cannot be re-decided by machine load:
        //    did the latch make ANY further attempt at all? On the unfixed code this counter stays
        //    at zero for as long as you care to wait — the budget is spent, a pure recycle writes
        //    nothing so the change-feed latch never fires, and there is no other trigger. A loaded
        //    runner can only make a correct implementation SLOWER to satisfy this; it can never make
        //    a broken one satisfy it.
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Volatile.Read(ref attemptsAfterReactivation))
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n >= 1,
                "the latch must RE-ASK once the address has reactivated. Zero further attempts is "
                + "#2986 exactly: the re-arm budget was spent on re-asks the SAME dying activation "
                + "refused, so nothing is left to try — and because a pure recycle writes nothing, "
                + "the change-feed latch has no event to fire on either");

        // 7. …and the read must converge. This is the user-visible contract: a reader of a node
        //    whose hub is mid-recycle sees the node once the hub comes back, instead of waiting out
        //    its full timeout for a latch that stopped trying.
        await workspace.GetObservable<BusinessUnit>()
            .Should().Within(TestTimeouts.Convergence)
            .Match(x => x.Count > 0,
                "the owner has reactivated, so the ridden-out re-ask must land on the fresh "
                + "activation and hydrate the stream. Timing out here is #2986: the re-arm budget "
                + "was spent on re-asks the SAME dying activation refused, and nothing re-asks once "
                + "it is gone — a pure recycle writes nothing, so the change-feed latch never fires");

        Output.WriteLine(
            $"DIAG total refusals by the dead activation: {Volatile.Read(ref corpseRefusals)}; "
            + $"attempts after reactivation: {Volatile.Read(ref attemptsAfterReactivation)}");
    }
}
