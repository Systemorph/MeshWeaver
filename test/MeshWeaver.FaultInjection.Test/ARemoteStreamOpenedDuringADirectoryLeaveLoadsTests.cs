using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>#7842 end to end: a node read opened while its READER's grain-directory entry is lost loads once
/// the entry is back, instead of timing out at 60 s.</b>
///
/// <para><b>What production measured.</b> <c>CollaborativeMarkdownView</c> read a document through the
/// process's cache hub (<c>cache/…</c>). The cache hub's <c>SubscribeRequest</c> reached the document's
/// owner, which answered; the ANSWER was refused on the way back ("no silo in this cluster is currently
/// serving that hub") because another silo's graceful leave had dropped the cache hub's entry from
/// Orleans' <c>LocalGrainDirectory</c>. Nothing re-asked, so the view's read ended in
/// <c>TimeoutException: No response received … within 00:01:00 for request SubscribeRequest</c>.</para>
///
/// <para><b>Why this case exists beside the router-level ones.</b>
/// <see cref="ARefusedReplyTellsItsAskerByNameTest"/> pins that the router tells the asker of a refused
/// reply by name. It drives the routing grain with raw route callbacks, so it does not show that the
/// READ recovers: that needs the real subscriber (<c>JsonSynchronizationStream</c>) to take the named
/// <see cref="ErrorType.ShuttingDown"/> failure as "ask again", re-ask while the entry is still missing,
/// and load once the owner's membership-change re-assertion lands. This case runs that whole chain on
/// production's directory with a real hub and a real per-node owner.</para>
///
/// <para><b>The shape.</b> Three multi-silo silos on <c>LocalGrainDirectory</c>. The reader is a real
/// hub (<c>silohub/…</c>, the rig's pod-hub type, the leg <c>cache/…</c> takes in production) on silo 0,
/// under an id whose directory registration lives on a second silo, chosen by the directory's own ring
/// calculation. The node's owner is activated on the third silo, under a path whose own directory
/// registration is NOT on the leaving silo (a leave deactivates every activation registered there, and a
/// re-activated owner would answer from the reader's silo, past the directory). The reader's re-assertion
/// is held (<see cref="ClaimHold"/>), the second silo begins a graceful stop and is held in
/// <c>ShuttingDown</c> (<see cref="ShutdownHold"/>), and once every remaining directory has moved the
/// entry off it the reader opens the node's stream.</para>
///
/// <para><b>The premise is asserted, not assumed.</b> The first thing the reader's tapped local route
/// hears from the node must be the router's refused-reply report (<see cref="ErrorType.ShuttingDown"/>,
/// the catalog sentence), and the owner must still be the one placed on the third silo. Only then does
/// the case hold the window: the stream neither loads nor fails. Then the re-assertion is let go, and the
/// stream must load within <see cref="Prompt"/>, far inside the 60 s budget production ran out, still
/// served by the placed owner.</para>
///
/// <para><b>Negative control</b> (measured locally, recorded in <c>Doc/Architecture/PodHubClaimReassertion</c>):
/// with the router's report to the asker removed (<c>RoutingGrain.AnswerPodHubNotHere</c> never calls
/// <c>reportRefusedReplyToAsker</c>), the same case is red — the stream delivers nothing within
/// <see cref="Prompt"/> after the re-assertion lands, because the only thing left that would end the
/// subscriber's wait is its own 60 s budget.</para>
/// </summary>
public class ARemoteStreamOpenedDuringADirectoryLeaveLoadsTest(ARemoteStreamOpenedDuringADirectoryLeaveLoadsTest.Cluster mesh)
    : IClassFixture<ARemoteStreamOpenedDuringADirectoryLeaveLoadsTest.Cluster>
{
    private const int Silos = 3;
    private const int Reader = 0;

    /// <summary>Far inside the 60 s budget production ran out; the re-ask is paced at 500 ms.</summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the window is held open after the read is opened. Well inside the subscriber's
    /// route-refusal ride-out (16 re-asks paced at 500 ms): this case measures the recovery, not the give-up.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    [Fact(Timeout = 240_000)]
    public async Task ANodeReadOpenedWhileTheReadersEntryIsLost_LoadsOnceItIsReasserted()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < Silos; i++)
            mesh.DefaultDirectoryIsDistributed(i).Should().BeFalse(
                $"silo {i} must run production's LocalGrainDirectory, or this case measures the test default");

        // ── Roles, placed by the directory's own ring calculation ───────────────────────────────────
        var addresses = Enumerable.Range(0, Silos)
            .Select(i => mesh.Silo(i).GetRequiredService<ILocalSiloDetails>().SiloAddress).ToArray();
        var factory = mesh.Silo(Reader).GetRequiredService<IGrainFactory>();
        Address readerAddress;
        GrainId readerGrain;
        int leaving;
        do
        {
            readerAddress = new Address(OrleansTestMeshExtensions.SiloHostedStreamRoutedType, $"reader-{Guid.NewGuid():N}");
            readerGrain = factory.GetGrain<IPodHubGrain>(readerAddress.ToString()).GetGrainId();
            var (partition, _) = PodHubRoles.DirectoryPartition(mesh, Reader, readerGrain);
            leaving = Array.FindIndex(addresses, a => a.Equals(partition));
        } while (leaving < 0 || leaving == Reader);
        var owner = Enumerable.Range(0, Silos).Single(i => i != Reader && i != leaving);

        // ── The node, activated on the owner silo ──────────────────────────────────────────────────
        // 🚨 The node's OWN grain must not be registered on the leaving silo either. LocalGrainDirectory
        // deactivates, on every silo, each activation whose directory partition was on a silo that has
        // entered a terminating status (Orleans' LocalGrainDirectory.OnSiloStatusChange). An owner
        // registered there is deactivated by the leave; the reader's SubscribeRequest re-activates it,
        // prefer-local, on the READER's silo, and its answer then reaches the reader locally without
        // crossing the directory at all. That happened in about one run in three (main ac8a8fddb): the read
        // loaded inside the window from an owner the case had not placed. So the path is chosen the way
        // the reader's id is, by the directory's own ring calculation.
        string ns, path;
        GrainId nodeGrain;
        do
        {
            ns = $"leave-read-{Guid.NewGuid():N}";
            path = $"{ns}/Doc";
            nodeGrain = factory.GetGrain<IMessageHubGrain>(path).GetGrainId();
        } while (Equals(PodHubRoles.DirectoryPartition(mesh, Reader, nodeGrain).Owner, addresses[leaving]));
        var node = new Address(path);
        var ownerServices = mesh.Silo(owner);
        var ownerAccess = ownerServices.GetRequiredService<AccessService>();
        await ownerAccess.RunAsSystem(() => ownerServices.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode("Doc", ns) { Name = "v1", NodeType = "Markdown", State = MeshNodeState.Active }))
            .Should().Within(TestTimeouts.Convergence).Emit("the node is created on its owner's silo", ct);
        await ownerAccess.RunAsSystem(() => mesh.Hub(owner).NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(node)))
            .Should().Within(TestTimeouts.Convergence).Emit($"the node's owner answers on silo {owner}", ct);
        mesh.Hub(owner).GetHostedHub(node, HostedHubCreation.Never).Should().NotBeNull(
            $"the node's owner must be activated on silo {owner}, the silo that is neither leaving nor reading");

        // ── The reader: a real hub on silo 0 whose pod-hub claim has landed ───────────────────────
        var readerRouting = mesh.Hub(Reader).ServiceProvider.GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        readerRouting.HasMemoryStream.Should().BeFalse(
            "with a memory stream a refused pod-hub delivery falls back to the stream and this case would prove nothing");
        // The reader's own local route, tapped. What reaches the hub through it (the owner's answer, or
        // the router's report that the answer was refused) is the positive reading of the window.
        var heard = new ReplaySubject<IMessageDelivery>();
        var reader = mesh.Hub(Reader).GetHostedHub(readerAddress, config => config
                .WithPostingIdentity(PostingIdentity.System)
                .AddData()
                .WithInitialization(h => h.RegisterForDisposal(((IRoutingService)readerRouting).RegisterStream(h.Address,
                    (SyncDelivery)(delivery =>
                    {
                        heard.OnNext(delivery);
                        return h.DeliverMessage(delivery);
                    })))),
            HostedHubCreation.Always);
        Assert.NotNull(reader);
        // No fallback: a null settle signal means the reader took no pod-hub claim at all, and the
        // window below would then inject nothing. Fail by name instead of waiting on nothing.
        var claimSettled = readerRouting.PodHubClaimSettled(readerAddress);
        if (claimSettled is null)
            Assert.Fail($"'{readerAddress}' must take the pod-hub leg (a claim to settle) — otherwise this case measures nothing");
        await claimSettled.Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit($"the reader's claim of '{readerAddress}' lands", ct);

        // ── The window: the reader's directory partition leaves, its re-assertion is held ─────────
        using var reassertion = mesh.Silo(Reader).GetRequiredService<ClaimHold>().Hold(readerAddress.ToString());
        using var held = mesh.Silo(leaving).GetRequiredService<ShutdownHold>().Arm();
        var stopping = mesh.Drain(leaving);
        try
        {
            await held.Arrivals.Should().Within(TestTimeouts.CrossSilo).Emit(
                $"silo {leaving} has written ShuttingDown and is held there — the window a roll leaves open", ct);
            foreach (var silo in new[] { Reader, owner })
                await PodHubRoles.PartitionMovedOff(mesh, silo, readerGrain, addresses[leaving])
                    .Should().Within(TestTimeouts.CrossSilo).Emit(
                        $"silo {silo}'s directory no longer places '{readerAddress}' on the leaving silo {leaving}", ct);
            await reassertion.Arrivals.Should().Within(TestTimeouts.CrossSilo).Emit(
                $"the reader's re-assertion of '{readerAddress}' for this membership change is in flight and held", ct);
            await PodHubRoles.SeesStatus(mesh, owner, addresses[leaving], SiloStatus.ShuttingDown)
                .Should().Within(TestTimeouts.CrossSilo).Emit(
                    $"the owner silo {owner}'s own view must have silo {leaving} ShuttingDown when it answers", ct);

            var access = mesh.Silo(Reader).GetRequiredService<AccessService>();
            var read = access.RunAsSystem(() => (IObservable<ChangeItem<MeshNode>>)((Workspace)reader.GetWorkspace())
                    .GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(node, new MeshNodeReference()))
                .Select(change => change.Value)
                .Where(node => node is not null)
                .Materialize()
                .Replay();
            using var open = read.Connect();

            // POSITIVE: the premise happened. The FIRST thing the reader hears from the node is the router's
            // report that the owner's answer was refused because no silo serves the reader: the named,
            // ShuttingDown-classified failure of RoutingGrain.RefusedReplyFailure. An answer that reached the
            // reader first would mean the directory did not lose the entry, and the case would measure nothing.
            var first = await heard.Where(d => d.Sender == node).Take(1).Should().Within(Prompt).Emit(
                $"the reader's subscription to '{path}' must hear from the node while the window is open", ct);
            var failure = first.Message.Should().BeOfType<DeliveryFailure>(
                $"the owner's answer to '{readerAddress}' must be REFUSED on the way back and reported to the reader, "
                + $"not delivered (it was {first.Message?.GetType().Name}) — otherwise the window did not happen").Subject;
            failure.ErrorType.Should().Be(ErrorType.ShuttingDown, "the refused-reply report is classified ShuttingDown");
            failure.Message.Should().Be(LocalizationCatalog.Get(ARefusedDeliveryAndItsAsker.RefusedReplyMessageKey, null),
                "it is the router's refused-reply report, not some other failure");
            mesh.Hub(owner).GetHostedHub(node, HostedHubCreation.Never).Should().NotBeNull(
                $"the refused answer must come from the owner placed on silo {owner}, not from a re-activation elsewhere");

            // And the read rides the refusal out: it neither loads nor fails while the entry is gone.
            await read.Select(n => n.Exception?.ToString() ?? n.Kind.ToString()).Should().NotEmit(Window,
                $"while '{readerAddress}' has no directory entry the owner's answer is refused on the way back, so "
                + "the read neither loads nor fails — it rides the refusal out", ct);

            reassertion.Release();

            var loaded = await read.Take(1).Should().Within(Prompt).Emit(
                $"once the reader's re-assertion lands the read of '{path}' must load — the refused answer was "
                + "reported to the reader as ShuttingDown and it asked again — instead of waiting out the 60 s "
                + "SubscribeRequest budget that ended production's CollaborativeMarkdownView read (#7842)", ct);
            loaded.Kind.Should().Be(System.Reactive.NotificationKind.OnNext,
                $"the read must deliver the node, not end with {loaded.Exception?.GetType().Name}: {loaded.Exception?.Message}");
            Assert.NotNull(loaded.Value);
            loaded.Value.Name.Should().Be("v1");
            mesh.Hub(owner).GetHostedHub(node, HostedHubCreation.Never).Should().NotBeNull(
                $"the read must have been served by the owner placed on silo {owner}");
        }
        finally
        {
            reassertion.Release();
            held.Release();
            await stopping.WaitAsync(TestTimeouts.CrossSilo, ct);
        }
    }

    /// <summary>Three silos on production's directory; its own: the case stops a silo.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => Silos;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloShutdownHoldConfigurator);

        /// <inheritdoc />
        protected override bool UsesDistributedGrainDirectory => false;
    }
}
