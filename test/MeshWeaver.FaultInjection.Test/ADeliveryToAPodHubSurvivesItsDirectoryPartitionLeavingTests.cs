using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>#7842: a delivery to a pod hub, sent while the silo holding that hub's grain-directory
/// partition is leaving and before the hub's owner has re-asserted its claim, reaches it.</b>
///
/// <para><b>What production measured.</b> During a memex roll a cache hub's <c>SubscribeRequest</c>s
/// were handled by their owners and the replies were refused on the way back: "Directed delivery to pod
/// hub 'cache/…' was refused: no silo in this cluster is currently serving that hub", logged by two
/// routers on two pods, both naming the same third silo, 200–400 ms after the OLD silo began its stop.
/// The cache hub's pod was alive throughout; its requests timed out after 60 s. #7907 had the same
/// shape, 30 s after another silo's shutdown began.</para>
///
/// <para><b>The hypothesis this pins, and what it measured.</b> A pod-process hub is reachable because
/// the owner's pinned <c>IPodHubGrain</c> activation is registered in Orleans' DHT grain directory, in the
/// partition of the silo its grain id hashes to. The suspected mechanism was that when THAT silo enters
/// <c>ShuttingDown</c>, the remaining silos drop it from the ring (only <c>Active</c> silos own
/// partitions), the DHT hands nothing over, and a router looking the hub up before the owner has
/// re-asserted its claim finds nothing, places a throw-away activation on itself and refuses the
/// delivery. On THIS cluster's directory that does NOT happen: with the leaving silo held in
/// <c>ShuttingDown</c>, both remaining directories re-partitioned, and the owner's re-assertion HELD, the
/// post from the replier is still delivered to the live hub — because the cluster runs Orleans'
/// <c>DistributedGrainDirectory</c> (the test harness's default, asserted by the case), which hands the
/// leaving silo's partition over. Production runs <c>LocalGrainDirectory</c>, and on it the same case is
/// refused until the owner re-asserts: <see cref="ALocalDirectoryLosesAPodHubEntryUntilItsClaimIsReassertedTest"/>.
/// See <c>Doc/Architecture/PodHubClaimReassertion</c> for what is and is not established.</para>
///
/// <para><b>The shape.</b> Three silos. A pod hub (<c>cache/…</c>, the production address type) is
/// claimed on the OWNER silo under an id whose directory registration lives on a second silo, chosen by
/// the directory's own ring calculation, never by luck. The owner's re-assertion is held
/// (<see cref="ClaimHold"/>) — the owner that has not yet heard of the change. The second silo begins a
/// graceful stop and is held in <c>ShuttingDown</c> (<see cref="ShutdownHold"/>). Once both remaining
/// silos' directories have re-partitioned the hub away from it, the third silo — the REPLIER, which has
/// never addressed the hub, as a node hub answering a cold subscribe has not — posts to the hub: the
/// reply leg of every request the hub makes.</para>
///
/// <para><b>Negative control</b> (<see cref="ADeliveryToAPodHubWithNoSiloLeavingArrivesTest"/>): the
/// same roles and post with no silo leaving are delivered — the route itself is sound, so the leaving
/// case measures the leave.</para>
/// </summary>
public class ADeliveryToAPodHubSurvivesItsDirectoryPartitionLeavingTest(
    ADeliveryToAPodHubSurvivesItsDirectoryPartitionLeavingTest.Cluster mesh)
    : APodHubDeliveryAcrossItsDirectoryPartitionLeaving(mesh),
        IClassFixture<ADeliveryToAPodHubSurvivesItsDirectoryPartitionLeavingTest.Cluster>
{
    /// <inheritdoc />
    protected override bool DistributedDirectory => true;

    /// <summary>
    /// Its own cluster: the case stops a silo. 🚨 It runs Orleans' <c>DistributedGrainDirectory</c> —
    /// the test harness's default, which hands a leaving silo's partitions over — and the case asserts
    /// it. That is why the leave did not lose the entry in #7917's runs: production runs
    /// <c>LocalGrainDirectory</c> (#7842).
    /// </summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 3;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloShutdownHoldConfigurator);
    }
}

/// <summary>
/// The #7842 leave case, run on the directory a cluster declares (<see cref="DistributedDirectory"/>):
/// see <see cref="ADeliveryToAPodHubSurvivesItsDirectoryPartitionLeavingTest"/> for the shape.
/// </summary>
public abstract class APodHubDeliveryAcrossItsDirectoryPartitionLeaving(FaultInjectionCluster mesh)
{
    /// <summary>Far inside the 30 s / 60 s budgets production ran out; a healthy cross-silo post takes milliseconds.</summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    /// <summary>Whether the cluster runs <c>DistributedGrainDirectory</c> (true) or production's <c>LocalGrainDirectory</c>.</summary>
    protected abstract bool DistributedDirectory { get; }

    [Fact(Timeout = 240_000)]
    public async Task APostToAPodHubWhoseDirectoryPartitionIsLeaving_IsDelivered()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 3; i++)
            mesh.DefaultDirectoryIsDistributed(i).Should().Be(DistributedDirectory,
                $"silo {i} must run the directory this case claims to measure (#7842)");
        using var roles = await PodHubRoles.Claim(mesh, ct);
        roles.Directory.Should().Be(DistributedDirectory ? PodHubRoles.DistributedDirectory : PodHubRoles.LocalDirectory,
            "the roles must be placed by the directory the silos route by, or the leave is not the hub's partition");

        // The owner has not heard of the change yet: its re-assertion is held until the case ends.
        using var reassertion = mesh.Silo(roles.Owner).GetRequiredService<ClaimHold>().Hold(roles.Hub.ToString());
        var hold = mesh.Silo(roles.Leaving).GetRequiredService<ShutdownHold>();
        using var held = hold.Arm();
        var stopping = mesh.Drain(roles.Leaving);
        try
        {
            await held.Arrivals.Should().Within(TestTimeouts.CrossSilo).Emit(
                $"silo {roles.Leaving} has written ShuttingDown and is held there — the window a roll leaves open", ct);
            foreach (var silo in new[] { roles.Owner, roles.Replier })
                await PodHubRoles.PartitionMovedOff(mesh, silo, roles.Grain, roles.LeavingAddress)
                    .Should().Within(TestTimeouts.CrossSilo).Emit(
                        $"silo {silo}'s directory no longer places '{roles.Hub}' on the leaving silo {roles.Leaving}", ct);
            await reassertion.Arrivals.Should().Within(TestTimeouts.CrossSilo).Emit(
                $"the owner's re-assertion of '{roles.Hub}' for this membership change is in flight and held — "
                + "the moment before the owner's claim lands", ct);
            // The window is real at the moment of the post: the replier's OWN membership view has the
            // directory-partition silo leaving — a positive reading, waited for, not "the steps returned".
            await PodHubRoles.SeesStatus(mesh, roles.Replier, roles.LeavingAddress, SiloStatus.ShuttingDown)
                .Should().Within(TestTimeouts.CrossSilo).Emit(
                    $"silo {roles.Replier}'s own view must have silo {roles.Leaving} ShuttingDown (held) when the post is made", ct);

            roles.Post(mesh);
            await roles.Received.Should().Within(Prompt).Emit(
                $"a delivery to '{roles.Hub}' posted while silo {roles.Leaving} (its directory partition) is leaving, "
                + "before the owner's claim has been re-asserted, must still reach the live hub — #7842's production "
                + "replies were refused at their replier in a window of this shape",
                ct);
        }
        finally
        {
            reassertion.Release();
            held.Release();
            await stopping.WaitAsync(TestTimeouts.CrossSilo, ct);
        }
    }
}

/// <summary>
/// <b>#7842 — the same leave on PRODUCTION's directory (<c>LocalGrainDirectory</c>) loses the pod hub's
/// entry until the owner re-asserts its claim.</b>
///
/// <para>This is the counterpart that makes <see cref="ADeliveryToAPodHubSurvivesItsDirectoryPartitionLeavingTest"/>
/// mean something: the same three roles, the same held leave, the same held re-assertion — and only the
/// directory differs. <c>TestClusterBuilder</c> configures <c>DistributedGrainDirectory</c> for every test
/// cluster, which hands a leaving silo's partition over, so #7917's runs could not lose the entry. This
/// cluster removes that configurator, so its silos run the directory a production silo runs.</para>
///
/// <para><b>Measured.</b> (1) While the leave and the re-assertion are both held, a request from the
/// third silo to the live hub is REFUSED: the router's lookup finds no registration, prefer-local places a
/// throw-away activation on the asking silo, and it answers <c>PodHubNotHere</c> — the asker hears a
/// <see cref="DeliveryFailure"/>, the hub hears nothing. (2) Once the owner's membership-change
/// re-assertion is let go, posts reach the hub again. So on production's directory the window is
/// exactly "the partition owner has left, the owner has not re-asserted yet" — the 200–400 ms in which
/// #7842's replies were refused.</para>
///
/// <para><c>cache</c> is declared client-hosted in this test rig, so the refusal text here is the
/// client-hosted one (no memory stream on a multi-silo host); a production router answers the same
/// lookup miss with "no silo in this cluster is currently serving that hub". Both are reached only after
/// the pod-hub grain answered <c>PodHubNotHere</c>.</para>
/// </summary>
public class ALocalDirectoryLosesAPodHubEntryUntilItsClaimIsReassertedTest(
    ALocalDirectoryLosesAPodHubEntryUntilItsClaimIsReassertedTest.Cluster mesh)
    : IClassFixture<ALocalDirectoryLosesAPodHubEntryUntilItsClaimIsReassertedTest.Cluster>
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 240_000)]
    public async Task APostDuringTheLeaveIsRefused_AndOneAfterTheReassertionArrives()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 3; i++)
            mesh.DefaultDirectoryIsDistributed(i).Should().BeFalse(
                $"silo {i} must run production's LocalGrainDirectory, or this case measures the test default");
        using var roles = await PodHubRoles.Claim(mesh, ct);
        roles.Directory.Should().Be(PodHubRoles.LocalDirectory,
            "the roles must be placed by the directory the silos route by");
        using var reassertion = mesh.Silo(roles.Owner).GetRequiredService<ClaimHold>().Hold(roles.Hub.ToString());
        var hold = mesh.Silo(roles.Leaving).GetRequiredService<ShutdownHold>();
        using var held = hold.Arm();
        var stopping = mesh.Drain(roles.Leaving);
        try
        {
            await held.Arrivals.Should().Within(TestTimeouts.CrossSilo).Emit(
                $"silo {roles.Leaving} has written ShuttingDown and is held there", ct);
            foreach (var silo in new[] { roles.Owner, roles.Replier })
                await PodHubRoles.PartitionMovedOff(mesh, silo, roles.Grain, roles.LeavingAddress)
                    .Should().Within(TestTimeouts.CrossSilo).Emit(
                        $"silo {silo}'s directory no longer places '{roles.Hub}' on the leaving silo", ct);
            await reassertion.Arrivals.Should().Within(TestTimeouts.CrossSilo).Emit(
                $"the owner's re-assertion of '{roles.Hub}' is in flight and held", ct);

            var verdict = await mesh.Hub(roles.Replier)
                .Observe<PingResponse>(new PingRequest(), o => o.WithTarget(roles.Hub))
                .Materialize().Take(1)
                .Should().Within(Prompt).Emit(
                    "a request to the hub during the leave is answered — by the router's refusal", ct);
            verdict.Kind.Should().Be(System.Reactive.NotificationKind.OnError,
                "on LocalGrainDirectory the leave dropped the hub's registration, so the request is refused");
            verdict.Exception.Should().BeOfType<DeliveryFailureException>(
                "the refusal reaches the asker as a named DeliveryFailure");
            await roles.Received.Should().NotEmit(TimeSpan.FromMilliseconds(500),
                "the refused request never reached the live hub", ct);

            reassertion.Release();
            using var reposting = Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
                .Subscribe(_ => roles.Post(mesh));
            await roles.Received.Should().Within(TestTimeouts.CrossSilo).Emit(
                "once the owner's membership-change re-assertion lands, the hub is reachable again", ct);
        }
        finally
        {
            reassertion.Release();
            held.Release();
            await stopping.WaitAsync(TestTimeouts.CrossSilo, ct);
        }
    }

    /// <summary>Three silos on production's directory.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 3;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloShutdownHoldConfigurator);

        /// <inheritdoc />
        protected override bool UsesDistributedGrainDirectory => false;
    }
}

/// <summary>
/// The negative control of <see cref="ADeliveryToAPodHubSurvivesItsDirectoryPartitionLeavingTest"/>
/// (#7842): the same roles and the same post with no silo leaving are delivered.
/// </summary>
public class ADeliveryToAPodHubWithNoSiloLeavingArrivesTest(ADeliveryToAPodHubWithNoSiloLeavingArrivesTest.Cluster mesh)
    : IClassFixture<ADeliveryToAPodHubWithNoSiloLeavingArrivesTest.Cluster>
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 240_000)]
    public async Task APostToAPodHubWithNoSiloLeaving_IsDelivered()
    {
        var ct = TestContext.Current.CancellationToken;
        using var roles = await PodHubRoles.Claim(mesh, ct);
        roles.Post(mesh);
        await roles.Received.Should().Within(Prompt).Emit(
            $"with no silo leaving, silo {roles.Replier} reaches '{roles.Hub}' on silo {roles.Owner} through the directory", ct);
    }

    /// <summary>Same shape as the leaving case's cluster, so the two measure the same route.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 3;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloShutdownHoldConfigurator);
    }
}

/// <summary>
/// The three roles of the #7842 cases, placed by Orleans' own directory ring calculation: <see cref="Hub"/>
/// is registered and claimed on silo <see cref="Owner"/>; its grain-directory registration lives on silo
/// <see cref="Leaving"/>; silo <see cref="Replier"/> posts to it.
/// </summary>
internal sealed class PodHubRoles : IDisposable
{
    private const int Silos = 3;
    private static readonly Type LocalGrainDirectoryType = Type.GetType(
        "Orleans.Runtime.GrainDirectory.LocalGrainDirectory, Orleans.Runtime", throwOnError: true)!;
    private static readonly Type DirectoryMembershipServiceType = Type.GetType(
        "Orleans.Runtime.GrainDirectory.DirectoryMembershipService, Orleans.Runtime", throwOnError: true)!;

    private readonly ReplaySubject<IMessageDelivery> received = new();
    private IDisposable? registration;

    private PodHubRoles(int owner, int leaving, Address hub, GrainId grain, SiloAddress leavingAddress)
    {
        Owner = owner;
        Leaving = leaving;
        Replier = Enumerable.Range(0, Silos).Single(i => i != owner && i != leaving);
        Hub = hub;
        Grain = grain;
        LeavingAddress = leavingAddress;
    }

    /// <summary>The silo hosting the pod hub.</summary>
    public int Owner { get; }

    /// <summary>The silo holding the pod hub's grain-directory partition.</summary>
    public int Leaving { get; }

    /// <summary>The third silo, which posts to the pod hub.</summary>
    public int Replier { get; }

    /// <summary>The pod hub's address.</summary>
    public Address Hub { get; }

    /// <summary>The pod hub's <c>IPodHubGrain</c> id.</summary>
    public GrainId Grain { get; }

    /// <summary>The leaving silo's address.</summary>
    public SiloAddress LeavingAddress { get; }

    /// <summary>Which directory placed the roles (<see cref="LocalDirectory"/> or <see cref="DistributedDirectory"/>).</summary>
    public string Directory { get; private init; } = string.Empty;

    /// <summary>Emits the first delivery that reaches the pod hub's local route.</summary>
    public IObservable<IMessageDelivery> Received => received.Take(1);

    /// <summary>The name <see cref="DirectoryPartition"/> reports for Orleans' legacy DHT directory.</summary>
    internal const string LocalDirectory = "LocalGrainDirectory";

    /// <summary>The name <see cref="DirectoryPartition"/> reports for Orleans' distributed directory.</summary>
    internal const string DistributedDirectory = "DistributedGrainDirectory";

    /// <summary>
    /// The silo whose directory partition holds <paramref name="grain"/>, as silo <paramref name="silo"/>'s
    /// EFFECTIVE default directory computes it, and which directory answered (#7842 review).
    ///
    /// <para>The two directories keep separate ownership views: <c>LocalGrainDirectory</c> its own ring
    /// (<c>CalculateGrainDirectoryPartition</c>), <c>DistributedGrainDirectory</c> the
    /// <c>DirectoryMembershipService</c>'s current view (<c>TryGetOwner</c>). Asking the wrong one would let a
    /// case place its fault, and wait for the hand-off, against a directory the silo does not route by.
    /// Orleans keeps all of these types internal, hence the reflection.</para>
    /// </summary>
    internal static (SiloAddress? Owner, string Directory) DirectoryPartition(
        FaultInjectionCluster mesh, int silo, GrainId grain)
    {
        var services = mesh.Silo(silo);
        if (!mesh.DefaultDirectoryIsDistributed(silo))
        {
            var directory = services.GetRequiredService(LocalGrainDirectoryType);
            var calculate = LocalGrainDirectoryType.GetMethod("CalculateGrainDirectoryPartition", [typeof(GrainId)])
                            ?? throw new InvalidOperationException(
                                "Orleans' LocalGrainDirectory no longer exposes CalculateGrainDirectoryPartition(GrainId) — "
                                + "the #7842 cases cannot place their fault without it.");
            return ((SiloAddress?)calculate.Invoke(directory, [grain]), LocalDirectory);
        }

        var membership = services.GetRequiredService(DirectoryMembershipServiceType);
        var view = DirectoryMembershipServiceType.GetProperty("CurrentView")?.GetValue(membership)
                   ?? throw new InvalidOperationException(
                       "Orleans' DirectoryMembershipService no longer exposes CurrentView — "
                       + "the #7842 cases cannot place their fault without it.");
        var tryGetOwner = view.GetType().GetMethods()
                              .SingleOrDefault(m => m.Name == "TryGetOwner"
                                                    && m.GetParameters() is { Length: 3 } p
                                                    && p[0].ParameterType == typeof(GrainId))
                          ?? throw new InvalidOperationException(
                              "Orleans' DirectoryMembershipSnapshot no longer exposes TryGetOwner(GrainId, out, out) — "
                              + "the #7842 cases cannot place their fault without it.");
        object?[] args = [grain, null, null];
        return ((bool)tryGetOwner.Invoke(view, args)! ? (SiloAddress?)args[1] : null, DistributedDirectory);
    }

    /// <summary>Emits once <paramref name="silo"/>'s effective directory places <paramref name="grain"/> anywhere but <paramref name="leaving"/>.</summary>
    internal static IObservable<long> PartitionMovedOff(FaultInjectionCluster mesh, int silo, GrainId grain, SiloAddress leaving) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => DirectoryPartition(mesh, silo, grain).Owner is { } owner && !owner.Equals(leaving))
            .Take(1);

    /// <summary>
    /// Emits once silo <paramref name="observer"/>'s OWN membership view reports <paramref name="silo"/> as
    /// <paramref name="status"/> — gossip reaches each silo in its own time, so a hold on the leaving silo
    /// proves nothing about another silo's view until this has emitted.
    /// </summary>
    internal static IObservable<long> SeesStatus(FaultInjectionCluster mesh, int observer, SiloAddress silo, SiloStatus status) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => mesh.Silo(observer).GetRequiredService<IClusterMembershipService>().CurrentSnapshot
                .GetSiloStatus(silo) == status)
            .Take(1);

    /// <summary>
    /// Registers a <c>cache/…</c> pod hub on silo 0 under an id whose directory registration lives on
    /// another silo, and waits for its claim to land.
    /// </summary>
    internal static async Task<PodHubRoles> Claim(FaultInjectionCluster mesh, CancellationToken ct)
    {
        const int owner = 0;
        var addresses = Enumerable.Range(0, Silos)
            .Select(i => mesh.Silo(i).GetRequiredService<ILocalSiloDetails>().SiloAddress).ToArray();
        var factory = mesh.Silo(owner).GetRequiredService<IGrainFactory>();
        Address hub;
        GrainId grain;
        int leaving;
        string directory;
        do
        {
            hub = new Address("cache", $"leave-{Guid.NewGuid():N}");
            grain = factory.GetGrain<IPodHubGrain>(hub.ToString()).GetGrainId();
            (var partition, directory) = DirectoryPartition(mesh, owner, grain);
            leaving = Array.FindIndex(addresses, a => a.Equals(partition));
        } while (leaving < 0 || leaving == owner);

        var roles = new PodHubRoles(owner, leaving, hub, grain, addresses[leaving]) { Directory = directory };
        var routing = (OrleansRoutingService)mesh.Hub(owner).ServiceProvider.GetRequiredService<IRoutingService>();
        roles.registration = routing.RegisterStream(hub, (delivery, _) =>
        {
            roles.received.OnNext(delivery);
            return Observable.Return(delivery.Processed());
        });
        await (routing.PodHubClaimSettled(hub) ?? Observable.Return(Unit.Default)).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit($"the claim of '{hub}' on silo {owner} lands", ct);
        return roles;
    }

    /// <summary>Posts to the pod hub from the replier's mesh hub — the routed, directory-resolved leg.</summary>
    public void Post(FaultInjectionCluster mesh) =>
        mesh.Hub(Replier).Post(new PingRequest(), o => o.WithTarget(Hub));

    /// <inheritdoc />
    public void Dispose() => registration?.Dispose();
}

/// <summary>
/// Every silo a MULTI-SILO host (no memory stream), plus the pod-hub claim hold
/// (<see cref="MultiSiloClaimHoldConfigurator"/>) and the <see cref="ShutdownHold"/>.
/// </summary>
public class MultiSiloShutdownHoldConfigurator : MultiSiloClaimHoldConfigurator
{
    /// <inheritdoc />
    public override void Configure(ISiloBuilder siloBuilder)
    {
        base.Configure(siloBuilder);
        siloBuilder.Services.AddSingleton<ShutdownHold>();
        siloBuilder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(
            sp => sp.GetRequiredService<ShutdownHold>());
    }
}

/// <summary>
/// Holds a silo's graceful stop right after it has written <c>ShuttingDown</c> to the membership table
/// (Orleans' <c>MembershipAgent</c> does so when the <see cref="ServiceLifecycleStage.BecomeActive"/>
/// stage stops; this participant sits one stage below, so it stops next). The silo then STAYS
/// <c>ShuttingDown</c> — the graceful-shutdown window of a roll — until the switch is released.
/// One instance per silo (a DI singleton), never static.
/// </summary>
public sealed class ShutdownHold : ILifecycleParticipant<ISiloLifecycle>
{
    private FaultSwitch? _armed;

    /// <summary>Arms the hold for this silo's next stop; release the returned switch to let the stop finish.</summary>
    public FaultSwitch Arm()
    {
        var fault = new FaultSwitch("hold the silo in ShuttingDown");
        Volatile.Write(ref _armed, fault);
        return fault;
    }

    /// <inheritdoc />
    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(ShutdownHold), ServiceLifecycleStage.BecomeActive - 1,
            _ => Task.CompletedTask, OnStop);

    private Task OnStop(CancellationToken ct) =>
        Volatile.Read(ref _armed) is { IsClosed: true } fault
            ? fault.Gate(Observable.Return(Unit.Default), "silo stop, after ShuttingDown").Await(ct)
            : Task.CompletedTask;
}

