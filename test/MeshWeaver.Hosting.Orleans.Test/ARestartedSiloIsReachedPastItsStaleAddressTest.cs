#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #7707: after a silo dies and its pod comes back, other silos' grain directories still
/// name the dead incarnation for a few seconds. Placement's directory lookup is addressed there,
/// the connection is refused, and Orleans logs <c>Orleans.Messaging[100071] Failed to address
/// message … IMessageHubGrain.DeliverMessage</c> carrying
/// <c>OrleansMessageRejectionException: Exception while sending message:
/// …ConnectionFailedException: Unable to connect to S…, will retry after …ms</c>. The question is
/// whether the SENDER sees that, or only Orleans' log does.
///
/// <para><b>The production shape.</b> On memex on 2026-10-30, the target addresses carry
/// generations that are hours older than the pod that could not reach them. One target shares its
/// IP with the reporting silo's own NEW generation, which means the pod's container restarted in
/// place. No shutdown line came before either burst, so the log watcher's roll-churn rule (which
/// needs one) rightly did not classify them. That is a silo that died without leaving, so
/// membership learns of it only from missed probes or the new incarnation's join.</para>
///
/// <para><b>How it is reproduced.</b> The scenario is the same as
/// <see cref="APlacementRefusedByADepartedSiloIsReplacedTest"/>, with #7707's two differences. Silo B
/// is KILLED and then a new silo is STARTED, which is the restart. The refusal is the
/// connection-refused rejection exactly as Orleans logs it. In-process, a kill writes <c>Dead</c> to
/// the membership table, so the stale window cannot occur by itself here. The counted switch
/// (<see cref="DepartedOwnerRefusal"/>) supplies it: it refuses a counted number of attempts and then
/// forwards to the survivor's real grain factory.</para>
///
/// <para><b>Controls.</b> With no re-send budget, the delivery is lost and NACK'd. That proves the
/// refusal reaches the leg and Orleans does not rescue it on its own. With a refusal that never
/// lifts, the verdict is <see cref="ErrorType.ShuttingDown"/>, which subscriptions ride out, never
/// <see cref="ErrorType.Failed"/>. Last, an unswitched ping from the RESTARTED silo through the
/// production router is answered. So the restarted incarnation does not inherit its predecessor's
/// stale address.</para>
/// </summary>
public class ARestartedSiloIsReachedPastItsStaleAddressTest(ARestartedSiloIsReachedPastItsStaleAddressTest.Fixture fixture)
    : IClassFixture<ARestartedSiloIsReachedPastItsStaleAddressTest.Fixture>
{
    private static IServiceProvider Services(SiloHandle silo) => ((InProcessSiloHandle)silo).SiloHost.Services;

    [Fact(Timeout = 180_000)]
    public async Task ADeliveryRefusedByARestartedSilosOldIncarnation_IsReplacedWithoutAUserVisibleRejection()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(170));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the restarting silo and the survivor must be different silos");

        var siloA = Services(cluster.Silos[0]);
        var siloB = Services(cluster.Silos[1]);
        var hubA = siloA.GetRequiredService<IMessageHub>();
        var hubB = siloB.GetRequiredService<IMessageHub>();
        var oldIncarnation = siloB.GetRequiredService<ILocalSiloDetails>().SiloAddress;

        // 1. The target, created and ACTIVATED on silo B (MessageHubGrain is [PreferLocalPlacement]).
        var ns = $"restart-{Guid.NewGuid():N}";
        var path = $"{ns}/Target";
        var accessB = siloB.GetRequiredService<AccessService>();
        await accessB.RunAsSystem(() => siloB.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode("Target", ns)
                {
                    Name = "Owned by the silo that is about to restart",
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                }))
            .FirstAsync().Await(ct);
        await accessB.RunAsSystem(() => hubB.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the target answers a ping on silo B — the precondition for the rest", ct);
        hubB.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().NotBeNull(
            "the target must be activated on silo B, the silo that restarts — otherwise nothing is re-placed");

        // 2. Silo B dies without leaving, and its pod comes back as a NEW incarnation.
        await cluster.KillSiloAsync(cluster.Silos[1]);
        var membershipA = siloA.GetRequiredService<IClusterMembershipService>();
        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .Select(_ => membershipA.CurrentSnapshot.GetSiloStatus(oldIncarnation))
            .Where(status => status == SiloStatus.Dead)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the survivor must declare the old incarnation dead", ct);
        var restarted = await cluster.StartAdditionalSiloAsync();
        var siloC = Services(restarted);
        var newIncarnation = siloC.GetRequiredService<ILocalSiloDetails>().SiloAddress;
        newIncarnation.Should().NotBe(oldIncarnation, "a restart is a new silo incarnation, with a new generation");
        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .Select(_ => membershipA.CurrentSnapshot.GetSiloStatus(newIncarnation))
            .Where(status => status == SiloStatus.Active)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the survivor must see the restarted silo join", ct);

        var grainsA = siloA.GetRequiredService<IGrainFactory>();
        var refusal = ConnectionRefused(oldIncarnation);

        // 3a. CONTROL — the refusal reaches the leg, and Orleans does not rescue it on its own.
        var unsent = new DepartedOwnerRefusal(refusal, refusals: 1, () => Deliver(grainsA, hubA, path));
        var unsentNacks = await APlacementRefusedByADepartedSiloIsReplacedTest.RunLeg(unsent, path, maxRetries: 0, ct);
        unsent.Attempts.Should().Be(1, "with no re-send budget the leg makes exactly the one refused attempt");
        unsentNacks.Should().ContainSingle(
            "a refused addressing attempt that is never re-sent is a lost delivery the sender must be told about");

        // 3b. CONTROL — a refusal that never lifts exhausts the ladder and is answered TRANSIENT.
        var permanent = new DepartedOwnerRefusal(refusal, refusals: int.MaxValue, () => Deliver(grainsA, hubA, path));
        var permanentNacks = await APlacementRefusedByADepartedSiloIsReplacedTest.RunLeg(permanent, path, maxRetries: 6, ct);
        permanent.Attempts.Should().Be(7, "the first attempt plus the six re-sends of the bounded ladder");
        permanentNacks.Should().ContainSingle().Which.Type.Should().Be(ErrorType.ShuttingDown,
            "a refused connection to a dead incarnation is a lifecycle transition: the sender's subscriptions "
            + "must ride it out, and a terminal Failed would tear them down (#2299 / #2307)");

        // 4. THE SUBJECT — refused twice (the stale window spans more than one attempt), then Orleans'
        //    own placement. Only the target's PingResponse to THIS ping proves the delivery was serviced.
        var issuer = hubA.NodeOperationIssuingHub();
        var ping = new MessageDelivery<PingRequest>(
            issuer.Address, new Address(path), new PingRequest(), issuer.JsonSerializerOptions);
        var answered = issuer.Observe(ping).Replay(1);
        using var awaiting = answered.Connect();
        var subject = new DepartedOwnerRefusal(refusal, refusals: 2,
            () => grainsA.GetGrain<IMessageHubGrain>(path).DeliverMessage(ping));
        var subjectNacks = await APlacementRefusedByADepartedSiloIsReplacedTest.RunLeg(subject, path, maxRetries: 6, ct);
        subject.Attempts.Should().Be(3, "two refused attempts, then one re-placed attempt");
        subjectNacks.Should().BeEmpty(
            "a delivery refused by a restarted silo's old incarnation must be re-placed, not rejected to "
            + "the sender (#7707)");
        subject.Landed.Should().NotBeNull("the re-placed attempt must reach the target grain");
        subject.Landed!.State.Should().NotBe(MessageDeliveryState.Failed,
            "the target, re-placed on a live silo, accepts the delivery");
        await answered.Where(d => d.Message is PingResponse)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the target must answer THIS ping — the re-placed delivery was serviced, not dropped", ct);

        // 5. And the RESTARTED silo reaches the target through the production router, with no switch.
        var accessC = siloC.GetRequiredService<AccessService>();
        await accessC.RunAsSystem(() => siloC.GetRequiredService<IMessageHub>().NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the restarted silo must reach the target, not its predecessor's stale address", ct);
    }

    private static Task<IMessageDelivery> Deliver(IGrainFactory grains, IMessageHub sender, string path) =>
        grains.GetGrain<IMessageHubGrain>(path)
            .DeliverMessage(new MessageDelivery<PingRequest>(
                sender.Address, new Address(path), new PingRequest(), sender.JsonSerializerOptions));

    /// <summary>
    /// The rejection #7707 logs, worded as Orleans words it. The constructor is not public, so it is
    /// built the way <see cref="DepartedSiloClassificationTest"/> builds it, and its message is checked.
    /// </summary>
    private static Exception ConnectionRefused(SiloAddress departed)
    {
        var message = "Exception while sending message: Orleans.Runtime.Messaging.ConnectionFailedException: "
                      + $"Unable to connect to {departed}, will retry after 55.7015ms";
        var rejection = (Exception)Activator.CreateInstance(
            typeof(OrleansMessageRejectionException),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: [message],
            culture: null)!;
        rejection.Message.Should().Be(message, "the leg's verdict reads this rejection's text");
        return rejection;
    }

    public class Fixture : TwoSiloCacheUpdateFixture
    {
        // Fast death detection, so the survivor converges on the kill inside a test budget.
        protected override Type SiloConfiguratorType => typeof(AKilledOwnerSiloIsReactivatedByItsHoldersTest.KillConfigurator);
    }
}
