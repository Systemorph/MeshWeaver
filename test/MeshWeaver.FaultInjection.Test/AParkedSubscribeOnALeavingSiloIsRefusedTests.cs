using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>A subscribe a leaving silo ACCEPTED before it began to stop</b> (#7470).
///
/// <para><b>What production measured.</b> During a roll a cache hub posted a <c>SubscribeRequest</c> for
/// <c>Admin/ContentIndex</c> about 2 s before the silo hosting that node's grain received SIGTERM. The
/// trail ended <c>Forwarded … no handler was ever entered</c>, nothing NACKed it, and the subscriber
/// waited out its full 60 s; about 330 sibling subscribes from the same cache hub stalled the same way.
/// The grain's intake gate (#5256) refuses what arrives AFTER the stop — this is the work that arrived
/// just BEFORE it: accepted, parked on a hub still being built, and answered after the stop through a
/// router that refuses every outbound delivery from <c>ApplicationStopping</c> on.</para>
///
/// <para><b>The shape reproduced.</b> Two silos. The node's grain is activated on silo 1 with its hub
/// BUILD held (<see cref="FaultInjectionCluster.HubBuilds"/>), so a subscribe from silo 0 is accepted
/// and parked there — the positive signal the case waits on. Then silo 1 is told to stop and LINGERS.
/// The subscriber must be answered well inside its own budget: a <c>ShuttingDown</c> refusal from the
/// leaving grain sends it to the next activation, on silo 0, which serves the node.</para>
///
/// <para><b>Negative control</b> (<see cref="ASubscribeParkedOnASiloThatStaysUpIsAnsweredTest"/>):
/// the same parked subscribe on a silo that does NOT stop is answered normally once its build is
/// released, and by that silo — so the hold itself neither answers nor refuses anything.</para>
/// </summary>
public class AParkedSubscribeOnALeavingSiloIsRefusedTest(AParkedSubscribeOnALeavingSiloIsRefusedTest.Cluster mesh)
    : IClassFixture<AParkedSubscribeOnALeavingSiloIsRefusedTest.Cluster>
{
    /// <summary>
    /// How long the subscriber may wait once silo 1 begins to stop: far inside the 30 s / 60 s budgets
    /// production ran out. The hand-off to the surviving silo takes well under a second here.
    /// </summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 180_000)]
    public async Task ASubscribeParkedOnASiloThatBeginsToStop_IsRefused_AndReAnsweredByTheNextActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var parked = await ParkASubscribe(mesh, owner: 1, subscriber: 0, "leaving-park");
        using var hold = parked.Hold;
        using var subscribed = parked.Subscribed;
        await using var lingering = mesh.Linger(1);
        try
        {
            var answer = await parked.Outcome.Should().Within(Prompt).Emit(
                "a subscribe the leaving grain accepted before its host began to stop must be answered "
                + "promptly — refused ShuttingDown, then served by the next activation — never left to "
                + "wait out its budget (#7470)", ct);
            answer.Kind.Should().Be(NotificationKind.OnNext,
                $"the refusal sends the subscriber to the next activation, which serves the node — got "
                + $"{answer.Kind}: {answer.Exception?.Message}");
            answer.Value!.Path.Should().Be(parked.Path);
            mesh.Hub(0).GetHostedHub(new Address(parked.Path), HostedHubCreation.Never).Should().NotBeNull(
                "the answer came from a new activation on silo 0 — the leaving silo's held build never "
                + "served it");
            hold.IsClosed.Should().BeTrue(
                "silo 1's build was still held when the subscriber was answered, so the answer cannot have "
                + "come from silo 1's hub");
        }
        finally
        {
            hold.Release();
        }
    }

    /// <summary>
    /// Creates a node, activates its grain on <paramref name="owner"/> with the hub build HELD, and
    /// subscribes to it from <paramref name="subscriber"/> — returning once the owner's grain has
    /// ACCEPTED and parked that subscribe.
    /// </summary>
    internal static async Task<ParkedSubscribe> ParkASubscribe(FaultInjectionCluster mesh, int owner, int subscriber, string prefix)
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"{prefix}-{Guid.NewGuid():N}";
        var path = $"{ns}/Doc";
        var hold = mesh.HubBuilds(owner).Hold(path);
        try
        {
            var ownerServices = mesh.Silo(owner);
            var ownerAccess = ownerServices.GetRequiredService<AccessService>();
            await ownerAccess.RunAsSystem(() => ownerServices.GetRequiredService<IMeshService>().CreateNode(
                    new MeshNode("Doc", ns) { Name = "v1", NodeType = "Markdown", State = MeshNodeState.Active }))
                .Should().Within(TestTimeouts.Convergence).Emit("the node is created", ct);

            // Activate the grain on the OWNER's silo ([PreferLocalPlacement]): a delivery routed by the
            // owner's router places it there, and the build reaching the hold proves where it lives.
            var ownerHub = mesh.Hub(owner);
            using var activation = ownerAccess.RunAsSystem(() => ownerHub.NodeOperationIssuingHub()
                    .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
                .Subscribe(_ => { }, _ => { });
            await hold.Arrivals.Should().Within(TestTimeouts.Convergence).Emit(
                $"the grain for '{path}' activates on silo {owner} and its build reaches the hold", ct);
            var parkedHere = mesh.HubBuilds(owner).Parked.Where(p => p.Path == path);

            // A COLD subscriber on the other silo — the shape of MeshNodeStreamHandle's initial-state
            // wait in every production sample. Materialized and replayed, so neither arm is missed.
            var subscriberServices = mesh.Silo(subscriber);
            var outcome = subscriberServices.GetRequiredService<AccessService>()
                .RunAsSystem(() => subscriberServices.GetRequiredService<IMeshNodeStreamCache>()
                    .GetStream(path, mesh.Hub(subscriber).JsonSerializerOptions))
                .Take(1)
                .Materialize()
                .Replay();
            var subscribed = outcome.Connect();
            // Identified by its SENDER, the subscriber silo's cache hub: the owner's own build also parks a
            // self-addressed read on the held build (MessageHubGrain.ComposeActivationSource), and that one
            // must not be mistaken for the subscribe under test.
            var subscriberCache = new Address("cache", mesh.Hub(subscriber).Address.Id).ToString();
            await parkedHere.Where(p => p.Delivery.Sender?.ToString() == subscriberCache)
                .Should().Within(TestTimeouts.Convergence).Emit(
                    $"silo {owner}'s grain accepts the subscribe from silo {subscriber} and parks it on its "
                    + "held build — the precondition for everything after", ct);
            hold.ArrivalCount.Should().Be(1, "exactly one build — the owner's — is held");
            return new ParkedSubscribe(path, hold, outcome, subscribed);
        }
        catch
        {
            hold.Release();
            throw;
        }
    }

    internal sealed record ParkedSubscribe(
        string Path,
        FaultSwitch Hold,
        IObservable<Notification<MeshNode>> Outcome,
        IDisposable Subscribed);

    /// <summary>Its own cluster: the case stops a silo.</summary>
    public class Cluster : FaultInjectionCluster;
}

/// <summary>
/// The negative control of <see cref="AParkedSubscribeOnALeavingSiloIsRefusedTest"/> (#7470), on its
/// own cluster because that case stops a silo: the same parked subscribe, on a silo that stays up, is
/// answered by that silo once its build is released — the hold neither answers nor refuses anything.
/// </summary>
public class ASubscribeParkedOnASiloThatStaysUpIsAnsweredTest(ASubscribeParkedOnASiloThatStaysUpIsAnsweredTest.Cluster mesh)
    : IClassFixture<ASubscribeParkedOnASiloThatStaysUpIsAnsweredTest.Cluster>
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 180_000)]
    public async Task ASubscribeParkedOnASiloThatStaysUp_IsAnsweredByThatSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        var parked = await AParkedSubscribeOnALeavingSiloIsRefusedTest.ParkASubscribe(mesh, owner: 0, subscriber: 1, "staying-park");
        using var hold = parked.Hold;
        using var subscribed = parked.Subscribed;
        try
        {
            hold.Release();
            var answer = await parked.Outcome.Should().Within(Prompt).Emit(
                "a subscribe parked on a silo that stays up is answered once its build completes", ct);
            answer.Kind.Should().Be(NotificationKind.OnNext,
                $"the owner serves the node — got {answer.Kind}: {answer.Exception?.Message}");
            answer.Value!.Path.Should().Be(parked.Path);
            mesh.Hub(0).GetHostedHub(new Address(parked.Path), HostedHubCreation.Never).Should().NotBeNull(
                "the owner stayed on silo 0, the silo that parked the subscribe");
            mesh.Hub(1).GetHostedHub(new Address(parked.Path), HostedHubCreation.Never).Should().BeNull(
                "and the subscriber's silo never activated a copy of its own");
        }
        finally
        {
            hold.Release();
        }
    }

    /// <summary>Its own cluster, so the stopping case cannot leave it changed.</summary>
    public class Cluster : FaultInjectionCluster;
}

/// <summary>
/// The case that isolates the grain's own reaction to the stop (#7470): a request parked on a held
/// build, on a silo that begins to stop with NOWHERE to hand off to
/// (<see cref="FaultInjectionCluster.LeaveNoHandOffTarget"/>). Nothing migrates or deactivates the
/// activation, and its build stays held for the whole case, so the only thing that can answer the parked
/// request is the park's race with the host's stop (<c>MessageHubGrain.HubReadyUnlessTheHostLeaves</c>).
/// The requester must get the transient <c>ShuttingDown</c> refusal promptly; without the race it waits
/// out its own budget, as production's subscribers did.
/// </summary>
public class AParkedRequestOnALeavingSiloWithNoHandOffIsRefusedTest(AParkedRequestOnALeavingSiloWithNoHandOffIsRefusedTest.Cluster mesh)
    : IClassFixture<AParkedRequestOnALeavingSiloWithNoHandOffIsRefusedTest.Cluster>
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 180_000)]
    public async Task ARequestParkedOnAStoppingSiloThatCannotHandOff_IsRefusedShuttingDown()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"nohandoff-{Guid.NewGuid():N}";
        var path = $"{ns}/Doc";
        using var hold = mesh.HubBuilds(1).Hold(path);
        try
        {
            var ownerServices = mesh.Silo(1);
            var ownerAccess = ownerServices.GetRequiredService<AccessService>();
            await ownerAccess.RunAsSystem(() => ownerServices.GetRequiredService<IMeshService>().CreateNode(
                    new MeshNode("Doc", ns) { Name = "v1", NodeType = "Markdown", State = MeshNodeState.Active }))
                .Should().Within(TestTimeouts.Convergence).Emit("the node is created", ct);
            using var activation = ownerAccess.RunAsSystem(() => mesh.Hub(1).NodeOperationIssuingHub()
                    .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
                .Subscribe(_ => { }, _ => { });
            await hold.Arrivals.Should().Within(TestTimeouts.Convergence).Emit(
                "the grain activates on silo 1 and its build reaches the hold", ct);

            mesh.LeaveNoHandOffTarget(1);

            // The request under test: from silo 0, parked on silo 1's held build.
            var requester = mesh.Hub(0).NodeOperationIssuingHub();
            var outcome = mesh.Silo(0).GetRequiredService<AccessService>()
                .RunAsSystem(() => requester.Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
                .Take(1)
                .Materialize()
                .Replay();
            using var connected = outcome.Connect();
            var requesterAddress = requester.Address.ToString();
            await mesh.HubBuilds(1).Parked
                .Where(p => p.Path == path && p.Delivery.Sender?.ToString() == requesterAddress)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("silo 1's grain accepts the request from silo 0 and parks it on the held build", ct);

            await using var lingering = mesh.Linger(1);
            var answer = await outcome.Should().Within(Prompt).Emit(
                "a request parked on a silo that begins to stop, with no hand-off to settle it, is refused "
                + "by the grain itself — never left to wait out its budget (#7470)", ct);
            answer.Kind.Should().Be(NotificationKind.OnError,
                $"the parked request is refused, not served — got {answer.Kind}");
            answer.Exception.Should().BeOfType<DeliveryFailureException>()
                .Which.Failure.ErrorType.Should().Be(ErrorType.ShuttingDown,
                    "the refusal is the transient one a requester rides out");
            hold.IsClosed.Should().BeTrue("the build was held throughout, so nothing built answered it");
        }
        finally
        {
            hold.Release();
        }
    }

    /// <summary>Its own cluster: the case stops a silo and rewrites its drain roster.</summary>
    public class Cluster : FaultInjectionCluster;
}
