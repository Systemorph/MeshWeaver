using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A WRITE REFUSED BY A RECYCLING OWNER LANDS ON THE NEXT ACTIVATION</b> — MeshWeaver#7326
/// (a recurrence of #4484).
///
/// <para><b>The failure.</b> <c>DanglingNodeTypeUpdateTest.Upsert_WithTheNamedImportEscapeHatch_StillLands</c>
/// retypes a node — which makes the owner's rebind watcher recycle the owner — and then writes the
/// same address again. When that second write's mirror was answered by the owner just BEFORE the
/// recycle began, its patch reached an owner that was already refusing ("Hub … is shutting down …
/// the address may reactivate") and the upsert failed.</para>
///
/// <para><b>Why the re-ask did not save it.</b> <c>UpdateRemote</c> treats a
/// <see cref="ErrorType.ShuttingDown"/> refusal as "never applied — re-ask the next activation"
/// (#5011), but it re-asked AT ONCE. The re-attempt diffs against the same leased mirror, which
/// already has state, so it posts immediately — and the dying owner is still registered under its
/// address until its own teardown removes it, so routing hands the re-ask straight back to it. Every
/// attempt of the <c>MaxOwnerDisposingReenqueues</c> budget was refused by the SAME activation inside
/// one teardown window: rule 2 of <c>Doc/Architecture/RidingOutAShuttingDownAddress</c> ("never
/// re-ask immediately without a join"), broken by the one rider that writes.</para>
///
/// <para><b>The deterministic shape.</b> The write's own update lambda — which runs after the
/// writer's mirror was answered by a healthy owner and before the patch is posted — starts the
/// owner's recycle and holds until the owner refuses new work. The owner hosts one child whose
/// Quiescing drain holds an un-answered callback, so once disposed it stays registered and refusing
/// for the child's whole quiesce budget — orders of magnitude longer than an immediate re-ask. So the
/// patch meets a refusing owner by construction, not by scheduling luck, and the write must then
/// land on the NEXT activation, after the refusing one is gone.</para>
/// </summary>
public class AWriteDuringItsOwnersRecycleLandsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ProbeNodeType = "RecycleWindowWriteProbe";

    /// <summary>Accepted and never answered — parks one pending response callback on its sender.</summary>
    private record HoldRequest : IRequest<HoldResponse>;

    private record HoldResponse;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(ProbeNodeType) { Name = "Recycle-window write probe" });

    [Fact(Timeout = 600_000)]
    public async Task AWriteRefusedByARecyclingOwner_LandsOnTheNextActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodeId = "recycle-write-" + Guid.NewGuid().ToString("N")[..8];
        var nodePath = $"{TestPartition}/{nodeId}";
        await NodeFactory.CreateNode(
                new MeshNode(nodeId, TestPartition) { Name = "initial", NodeType = ProbeNodeType })
            .Should().Within(TestTimeouts.Convergence).Emit("the node to write must exist first",
                cancellationToken: ct);
        var workspace = Mesh.GetWorkspace();
        await workspace.GetMeshNodeStream(nodePath).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit("the node is readable", cancellationToken: ct);

        var owner = Mesh.GetHostedHub(new Address(nodePath), HostedHubCreation.Never);
        owner.Should().NotBeNull("reading the node activated its owner");

        // Hold the owner registered-and-refusing through its teardown: it hosts one child whose
        // Quiescing drain holds an un-answered callback, so the owner's hosted-hub disposal cannot
        // complete for the child's whole quiesce budget. The callback is owed by a HEALTHY hub
        // outside the owner's tree — a reply owed by a hub that is itself going down re-arms the
        // quiesce budget (MaxQuiesceRearms) and would hold the window for ~40 s, not one budget.
        var holdEntered = new AsyncSubject<Unit>();
        var holder = Mesh.GetHostedHub(
            new Address("recycle-hold", Guid.NewGuid().ToString("N")[..8]),
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse))
                .WithHandler<HoldRequest>((_, d) =>
                {
                    holdEntered.OnNext(Unit.Default);
                    holdEntered.OnCompleted();
                    return d.Processed();
                }));
        var child = owner!.GetHostedHub(
            new Address($"{nodePath}/_hold"),
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse)));
        using var held = child
            .Observe<HoldResponse>(new HoldRequest(), o => o.WithTarget(holder.Address))
            .Subscribe(
                d => Output.WriteLine($"hold answered unexpectedly: {d.Message}"),
                ex => Output.WriteLine($"hold released: {ex.GetType().Name}"));
        await holdEntered.Should().Within(TestTimeouts.Convergence).Emit(
            "the child holds an un-answered callback before the recycle starts", cancellationToken: ct);

        // THE RECYCLE, started from inside the write — after its mirror was answered, before its
        // patch is posted. Only the FIRST run starts it (a re-attempt runs the lambda again), and
        // the run holds until the owner refuses new work, under a bounded spin: the owner's teardown
        // runs on its own turn loop, never on the thread this lambda runs on.
        var recycleStarted = 0;
        var refusingAtPost = (int)MessageHubRunLevel.Started;
        var marker = "landed-" + Guid.NewGuid().ToString("N")[..8];
        MeshNode? callerTerminal = null;
        Exception? callerError = null;
        using var writeSub = workspace.GetMeshNodeStream(nodePath)
            .Update(n =>
            {
                if (Interlocked.Exchange(ref recycleStarted, 1) == 0)
                {
                    owner.Dispose();
                    SpinWait.SpinUntil(() => owner.RunLevel >= MessageHubRunLevel.DisposeHostedHubs,
                        TestTimeouts.Convergence);
                    Volatile.Write(ref refusingAtPost, (int)owner.RunLevel);
                }
                return n with { Name = marker };
            })
            .Subscribe(
                n => Volatile.Write(ref callerTerminal, n),
                ex => Volatile.Write(ref callerError, ex));

        var settled = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => Volatile.Read(ref callerTerminal) is not null
                        || Volatile.Read(ref callerError) is not null)
            .Select(_ => true)
            .Take(1)
            .Timeout(TestTimeouts.WriteConvergence, Observable.Return(false))
            .FirstAsync().Await(ct);
        settled.Should().BeTrue("the write must reach a terminal; UpdateRemote bounds every attempt");
        var levelAtPost = (MessageHubRunLevel)Volatile.Read(ref refusingAtPost);
        Output.WriteLine($"owner RunLevel when the first patch left: {levelAtPost}");
        levelAtPost.Should().Be(MessageHubRunLevel.DisposeHostedHubs,
            "the premise: the first patch was posted to an owner already refusing new work and still "
            + "registered under its address");
        Volatile.Read(ref callerError).Should().BeNull(
            "a ShuttingDown refusal is a promise that the address reactivates — the write must wait "
            + "for the refusing activation to be gone and land on the next one, never burn its "
            + "re-ask budget against the same dying owner");
        Volatile.Read(ref callerTerminal)!.Name.Should().Be(marker);
        owner.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "the write landed after the refusing activation was gone — not on it");

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => storage.Read(nodePath, Mesh.JsonSerializerOptions))
            .Where(n => n?.Name == marker)
            .Should().Within(TestTimeouts.Convergence).Emit("the write is durable", cancellationToken: ct);
    }
}
