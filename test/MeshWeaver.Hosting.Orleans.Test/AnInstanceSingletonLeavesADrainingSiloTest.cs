#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Maintainer, 2026-10-04: "babysitter should be 1 process per cloud". The control instance's always-on
/// singleton hub (the PR babysitter and the PR steward) sat on a DRAINING pod for up to 30 minutes after each
/// roll, because nothing told its activation the pod was leaving until SIGTERM.
///
/// <para><b>The contract.</b> Two silos; the singleton and a control node both live on silo B. Silo B BEGINS
/// TO DRAIN (<see cref="HostDrainSignal.Begin"/>, the first <c>/drain</c> probe) — it does NOT stop: it
/// lingers Active, as a terminating pod does through preStop. The singleton (<see cref="HostDrainSignal.Relocate"/>,
/// what <c>RelocateOnDrain()</c> registers) is handed off to silo A promptly and answers the next message
/// from there; the control node — an ordinary hub, still serving the sessions that hold the pod open — stays.</para>
/// </summary>
public class AnInstanceSingletonLeavesADrainingSiloTest(TwoSiloCacheUpdateFixture fixture)
    : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(20);

    private static IServiceProvider SiloServices(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    private static async Task<string> NodeOnB(IServiceProvider siloB, IMessageHub hubB, string name, CancellationToken ct)
    {
        var ns = $"drain-singleton-{Guid.NewGuid():N}";
        var path = $"{ns}/{name}";
        var access = siloB.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => siloB.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode(name, ns) { Name = name, NodeType = "Markdown", State = MeshNodeState.Active }))
            .FirstAsync().Await(ct);
        await access.RunAsSystem(() => hubB.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TimeSpan.FromSeconds(30))
            .Emit($"{name} answers on silo B — the precondition", ct);
        hubB.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().NotBeNull($"{name} must be activated on silo B, the silo that drains");
        return path;
    }

    [Fact(Timeout = 180_000)]
    public async Task ASingleton_MovesToALiveSilo_WhenItsSiloBeginsToDrain_AndOrdinaryHubsStay()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(170));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the singleton must have a live silo to move to");

        var siloA = SiloServices(cluster, 0);
        var siloB = SiloServices(cluster, 1);
        var hubA = siloA.GetRequiredService<IMessageHub>();
        var hubB = siloB.GetRequiredService<IMessageHub>();

        var singleton = await NodeOnB(siloB, hubB, "Singleton", ct);
        var ordinary = await NodeOnB(siloB, hubB, "Ordinary", ct);

        var signalB = siloB.GetRequiredService<HostDrainSignal>();
        signalB.Relocate(new Address(singleton));
        siloA.GetRequiredService<HostDrainSignal>().Begun.Should().BeFalse("only silo B drains");

        try
        {
            // Silo B begins to DRAIN — the first /drain probe. It does not stop.
            signalB.Begin().Should().BeTrue("the first probe begins termination");
            signalB.Begin().Should().BeFalse("and only the first");

            // 🚨 PROACTIVELY — no message needed: an idle singleton leaves the draining silo by itself, at once.
            var moved = await Observable.Interval(TimeSpan.FromMilliseconds(250))
                .Select(_ => hubB.GetHostedHub(new Address(singleton), HostedHubCreation.Never) is null)
                .FirstAsync(gone => gone)
                .Timeout(Prompt)
                .Catch((TimeoutException _) => Observable.Return(false))
                .Await(ct);
            moved.Should().BeTrue("🚨 the singleton's activation LEFT silo B within seconds of the drain beginning — not at SIGTERM");

            // The next message — from silo A — is answered, from the live silo.
            var accessA = siloA.GetRequiredService<AccessService>();
            await accessA.RunAsSystem(() => hubA.NodeOperationIssuingHub()
                    .Observe(new PingRequest(), o => o.WithTarget(new Address(singleton))))
                .Should().Within(Prompt)
                .Emit("the next message to the singleton is answered — by its new activation on the live silo", ct);
            hubB.GetHostedHub(new Address(singleton), HostedHubCreation.Never).Should().BeNull("and it did not come back to the draining silo");
            hubA.GetHostedHub(new Address(singleton), HostedHubCreation.Never).Should().NotBeNull("it lives on silo A, the live silo");

            // The ordinary hub keeps serving from the draining silo — the drain waits for exactly such work.
            await accessA.RunAsSystem(() => hubA.NodeOperationIssuingHub()
                    .Observe(new PingRequest(), o => o.WithTarget(new Address(ordinary))))
                .Should().Within(TimeSpan.FromSeconds(30))
                .Emit("an ordinary hub still answers", ct);
            hubB.GetHostedHub(new Address(ordinary), HostedHubCreation.Never).Should().NotBeNull(
                "an ordinary hub is NOT moved by drain — only hubs that RelocateOnDrain");
        }
        finally
        {
            await cluster.StopSiloAsync(cluster.Silos[1]);
        }
    }

    [Fact]
    public void TheSignal_BeginsOnce_AndNamesOnlyRegisteredSingletons()
    {
        using var signal = new HostDrainSignal();
        var singleton = new Address("Hosting/PlatformBuilds");
        var other = new Address("Some/Node");
        signal.Relocate(singleton);
        signal.MustLeave(singleton).Should().BeFalse("nothing leaves before drain begins");
        var seen = 0;
        using var _ = signal.WhenBegun.Subscribe(_ => seen++);
        signal.Begin().Should().BeTrue();
        signal.Begin().Should().BeFalse();
        seen.Should().Be(1, "it begins once");
        signal.MustLeave(singleton).Should().BeTrue("a registered singleton leaves once drain began");
        signal.MustLeave(other).Should().BeFalse("an ordinary hub stays");
        var late = 0;
        using var __ = signal.WhenBegun.Subscribe(_ => late++);
        late.Should().Be(1, "a late subscriber still learns that drain began");
    }
}
