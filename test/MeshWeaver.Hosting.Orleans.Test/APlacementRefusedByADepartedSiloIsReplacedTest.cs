#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 Issues #7002 / #7003 (and #5336, the same departure seen one step earlier): a message-hub
/// delivery whose PLACEMENT is refused because the silo owning the grain-directory partition has
/// left. Is the delivery dropped, or re-placed?
///
/// <para><b>The production shape.</b> <c>Orleans.Messaging[100071] Failed to address message …
/// IMessageHubGrain.DeliverMessage</c>, carrying <see cref="SiloUnavailableException"/>
/// <c>The target silo became unavailable for message: … sys.svc.dir.mem/… IDhtGrainDirectory.LookupAsync</c>,
/// raised in <c>PlacementService.PlacementWorker.ExecutePlacementAsync</c> after Orleans' own
/// placement retry (<c>PlacementMaxRetries</c> 3) was spent. That wording is a literal of
/// <b>Orleans.Core</b> — the callback Orleans breaks when MEMBERSHIP DECLARES the target silo dead —
/// so the exception is the moment of convergence, not a stale view that persists: the next
/// placement resolves the partition against a ring that no longer contains the departed silo.</para>
///
/// <para><b>What the hosting does with it</b> (<c>RoutingGrain.DeliverToGrainRoute</c>): the
/// exception is an <see cref="OrleansMessageRejectionException"/>, so
/// <c>IsResendableDeliveryFailure</c> admits it and the cold grain call is re-invoked — a FRESH
/// <c>GetGrain</c>, i.e. a fresh placement through Orleans' own placement service, which is the
/// re-route. If the refusal outlasted the bounded ladder the sender would be answered
/// <see cref="ErrorType.ShuttingDown"/> (<c>IsShutdownShaped</c>'s type test), which its
/// subscriptions ride out — never <see cref="ErrorType.Failed"/>.</para>
///
/// <para><b>How it is reproduced.</b> A real two-silo cluster. The target hub is created and
/// ACTIVATED on silo B; silo B is KILLED (no graceful stop, no deactivation); the survivor's
/// membership is waited on until it lists B as dead. The delivery is then issued from silo A
/// through the production leg, and its first attempt is answered with the exact exception Orleans
/// hands the caller (a controlled switch: <see cref="DepartedOwnerRefusal"/> refuses a counted
/// number of attempts, then forwards to silo A's real grain factory). Nothing sleeps; every wait
/// is on an observed condition.</para>
///
/// <para><b>Controls.</b> The refusal is proven to REACH the leg: forwarded with no re-send
/// budget, the delivery is lost and the sender is NACK'd — so the subject below cannot pass
/// because the injection was inert, nor because Orleans recovered on its own. With the refusal
/// never lifting, the verdict after the ladder is <see cref="ErrorType.ShuttingDown"/>, not
/// <see cref="ErrorType.Failed"/>.</para>
/// </summary>
public class APlacementRefusedByADepartedSiloIsReplacedTest(APlacementRefusedByADepartedSiloIsReplacedTest.Fixture fixture)
    : IClassFixture<APlacementRefusedByADepartedSiloIsReplacedTest.Fixture>
{
    private static IServiceProvider SiloServices(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    /// <summary>
    /// One fact, three phases, because the kill changes the cluster for everything after it: the
    /// two controls run on the post-kill cluster first, then the subject.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ADeliveryWhosePlacementIsRefusedByADepartedOwner_IsReplacedOnTheSurvivor()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(170));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the departed owner and the survivor must be different silos");

        var siloA = SiloServices(cluster, 0);
        var siloB = SiloServices(cluster, 1);
        var hubA = siloA.GetRequiredService<IMessageHub>();
        var hubB = siloB.GetRequiredService<IMessageHub>();
        var siloBAddress = siloB.GetRequiredService<ILocalSiloDetails>().SiloAddress;

        // 1. The target, created and ACTIVATED on silo B (MessageHubGrain is [PreferLocalPlacement]).
        var ns = $"departed-{Guid.NewGuid():N}";
        var path = $"{ns}/Target";
        var accessB = siloB.GetRequiredService<AccessService>();
        await accessB.RunAsSystem(() => siloB.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode("Target", ns)
                {
                    Name = "Owned by the silo that is about to leave",
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                }))
            .FirstAsync().Await(ct);
        await accessB.RunAsSystem(() => hubB.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the target answers a ping on silo B — the precondition for the rest", ct);
        hubB.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().NotBeNull(
            "the target must be activated on silo B, the silo that leaves — otherwise nothing is re-placed");

        // 2. Silo B leaves abruptly, and the survivor's membership converges on it.
        await cluster.KillSiloAsync(cluster.Silos[1]);
        var membershipA = siloA.GetRequiredService<IClusterMembershipService>();
        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .Select(_ => membershipA.CurrentSnapshot.GetSiloStatus(siloBAddress))
            .Where(status => status == SiloStatus.Dead)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the survivor must declare silo B dead — the instant Orleans breaks the lookups "
                  + "addressed to it with the SiloUnavailableException this test injects", ct);

        var grainsA = siloA.GetRequiredService<IGrainFactory>();
        var refusal = DepartedOwnerRefusal.For(siloBAddress, path);

        // 3a. CONTROL — the refusal reaches the leg, and Orleans does not rescue it on its own:
        //     with no re-send budget the delivery is lost and the sender is NACK'd.
        var unsent = new DepartedOwnerRefusal(refusal, refusals: 1, () => Deliver(grainsA, hubA, path));
        var unsentNacks = await RunLeg(unsent, path, maxRetries: 0, ct);
        unsent.Attempts.Should().Be(1, "with no re-send budget the leg makes exactly the one refused attempt");
        unsentNacks.Should().ContainSingle(
            "a refused placement that is never re-sent is a lost delivery the sender must be told about");
        hubA.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().BeNull(
            "the refused attempt never reached the grain, so nothing was activated on the survivor — "
            + "if it had been, the subject below could pass without the re-send");

        // 3b. CONTROL — a refusal that never lifts exhausts the ladder and is answered TRANSIENT.
        var permanent = new DepartedOwnerRefusal(refusal, refusals: int.MaxValue, () => Deliver(grainsA, hubA, path));
        var permanentNacks = await RunLeg(permanent, path, maxRetries: 6, ct);
        permanent.Attempts.Should().Be(7, "the first attempt plus the six re-sends of the bounded ladder");
        permanentNacks.Should().ContainSingle().Which.Type.Should().Be(ErrorType.ShuttingDown,
            "a departed silo is a lifecycle transition: the sender's subscriptions must ride it out, "
            + "and a terminal Failed would tear them down for an ordinary roll (#2299 / #2307)");

        // 4. THE SUBJECT — the departure's refusal on the first attempt, then Orleans' own placement.
        //    The ping is sent FROM an issuing hub that has registered the callback for THIS delivery's
        //    id before the leg runs, so the only thing that can resolve it is the target's own
        //    PingResponse to this very ping — proof the re-placed delivery was serviced, not merely
        //    accepted (MessageHubGrain.DeliverMessage answers before its hub has dispatched).
        //    Observe(delivery) registers the callback for an existing delivery and posts nothing.
        var issuer = hubA.NodeOperationIssuingHub();
        var ping = new MessageDelivery<PingRequest>(
            issuer.Address, new Address(path), new PingRequest(), issuer.JsonSerializerOptions);
        var answered = issuer.Observe(ping).Replay(1);
        using var awaiting = answered.Connect();
        var subject = new DepartedOwnerRefusal(refusal, refusals: 1,
            () => grainsA.GetGrain<IMessageHubGrain>(path).DeliverMessage(ping));
        var subjectNacks = await RunLeg(subject, path, maxRetries: 6, ct);
        subject.Attempts.Should().Be(2, "one refused attempt, then one re-placed attempt");
        subjectNacks.Should().BeEmpty(
            "a delivery whose placement was refused by a departed directory owner must be re-placed, "
            + "not dropped (#7002 / #7003)");
        subject.Landed.Should().NotBeNull("the re-placed attempt must reach the target grain");
        subject.Landed!.State.Should().NotBe(MessageDeliveryState.Failed,
            "the target, re-placed on the survivor, accepts the delivery");
        await answered.Where(d => d.Message is PingResponse)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the target must answer THIS ping — the re-placed delivery was serviced, not dropped", ct);
        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .Select(_ => hubA.GetHostedHub(new Address(path), HostedHubCreation.Never))
            .Where(h => h is not null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the re-placement lands the target on the SURVIVOR — the hub that lived on silo B is gone", ct);
    }

    /// <summary>
    /// 🚨 <b>THE ANTI-INERT PIN for the wording this page reads as "membership has declared the silo
    /// dead".</b> The classification above is by TYPE and needs no text, but two readers depend on
    /// the words: the reading in <c>Doc/Architecture/ADepartedSiloIsNotADeliveryDefect</c> (the line
    /// dates convergence) and the log watcher's roll-churn rule for this site (MeshWeaver.Plugins
    /// <c>RollChurnClassifier.DepartedSiloDeclaredDeadRejection</c>), which matches it verbatim. If an
    /// Orleans upgrade re-words it this goes red — repair both readers, never delete the pin.
    /// </summary>
    [Fact]
    public void TheDeclaredDeadWording_IsStillAShippedOrleansCoreLiteral()
    {
        DepartedSiloClassificationTest.ShippedLiteralPresent("Orleans.Core.dll", DepartedOwnerRefusal.DeclaredDeadMarker)
            .Should().BeTrue(
                $"'{DepartedOwnerRefusal.DeclaredDeadMarker}' must be a string literal of the shipped Orleans.Core — "
                + "the text Orleans breaks an outstanding request with when membership declares its target silo dead");
    }

    private static Task<IMessageDelivery> Deliver(IGrainFactory grains, IMessageHub sender, string path) =>
        grains.GetGrain<IMessageHubGrain>(path)
            .DeliverMessage(new MessageDelivery<PingRequest>(
                sender.Address, new Address(path), new PingRequest(), sender.JsonSerializerOptions));

    /// <summary>Runs the production delivery leg over the controlled switch and collects every NACK it posts.</summary>
    internal static async Task<ImmutableList<(string Message, ErrorType Type)>> RunLeg(
        DepartedOwnerRefusal grainCall, string path, int maxRetries, CancellationToken ct)
    {
        var nacks = ImmutableList<(string Message, ErrorType Type)>.Empty;
        await RoutingGrain.DeliverToGrainRoute(
                grainCall.Invoke,
                grainKey: path,
                addressPath: path,
                deliveryId: $"d-7003-{Guid.NewGuid():N}",
                postFailureToSender: (m, t) => ImmutableInterlocked.Update(ref nacks, l => l.Add((m, t))),
                logger: NullLogger.Instance,
                maxRetries: maxRetries,
                backoff: _ => TimeSpan.FromMilliseconds(10))
            .Await(ct)
            .WaitAsync(TestTimeouts.Convergence, ct);
        return nacks;
    }

    public class Fixture : TwoSiloCacheUpdateFixture
    {
        // Fast death detection, so the survivor converges on the kill inside a test budget — the
        // production mechanism at test cadence.
        protected override Type SiloConfiguratorType => typeof(AKilledOwnerSiloIsReactivatedByItsHoldersTest.KillConfigurator);
    }
}

/// <summary>
/// The controlled switch: the first <c>refusals</c> attempts of a grain call are answered with the
/// exception Orleans hands a caller whose placement lookup was addressed to a departed silo; every
/// later attempt is forwarded to the real call. Counts attempts so a case can assert the switch
/// actually fired.
/// </summary>
internal sealed class DepartedOwnerRefusal(
    Exception refusal, int refusals, Func<Task<IMessageDelivery>> forward)
{
    private int attempts;
    private IMessageDelivery? landed;

    /// <summary>How many times the leg invoked the grain call.</summary>
    public int Attempts => Volatile.Read(ref attempts);

    /// <summary>What the forwarded call answered, once one was forwarded.</summary>
    public IMessageDelivery? Landed => Volatile.Read(ref landed);

    /// <summary>The grain call the leg re-invokes on each attempt.</summary>
    public Task<IMessageDelivery> Invoke() =>
        Interlocked.Increment(ref attempts) <= refusals
            ? Task.FromException<IMessageDelivery>(refusal)
            : ForwardAsync();

    private async Task<IMessageDelivery> ForwardAsync()
    {
        var result = await forward();
        Volatile.Write(ref landed, result);
        return result;
    }

    /// <summary>Orleans.Core's wording for a request broken because its target silo was declared dead.</summary>
    public const string DeclaredDeadMarker = "The target silo became unavailable";

    /// <summary>
    /// The production rejection, worded as Orleans words it (#7003's samples): the departed silo is
    /// the target of the directory lookup the placement made.
    /// </summary>
    public static SiloUnavailableException For(SiloAddress departed, string path) =>
        new($"{DeclaredDeadMarker} for message: Request [sys.client/hosted]->[{departed} "
            + $"sys.svc.dir.mem/{departed}] Orleans.GrainDirectory.IDhtGrainDirectory.LookupAsync(messagehub/{path}, 0). "
            + "See https://aka.ms/orleans-troubleshooting for troubleshooting help.");
}
