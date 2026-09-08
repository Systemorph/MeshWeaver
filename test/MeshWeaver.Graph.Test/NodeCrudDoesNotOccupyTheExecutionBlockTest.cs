using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A node create in flight must NOT occupy the node-CRUD execution block</b> — and measuring
/// that it does not is what makes
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/2543">#2543</see>'s central capture an
/// ANOMALY rather than the normal cost of a create.
///
/// <para><b>The reasoning this pins down.</b> #2543 is argued from one hub dump reading
/// <c>Queue(buffer=45,…) Executing(CreateNodeRequest, 24888ms)</c> on <c>portal/nodeops</c> — the
/// mesh's ONE node-CRUD execution hub, whose single block runs every create and upsert in the mesh.
/// The natural reading is that a create simply costs that much: <c>HandleCreateNodeRequest</c>
/// <c>.Subscribe(...)</c>s its whole pipeline — partition bootstrap, validators, NodeType existence,
/// enrich, save, change feed — <b>inline</b>, and only then returns <c>request.Processed()</c>, so
/// everything that runs synchronously inside that <c>Subscribe</c> is charged to the block. If that
/// were the steady state, the fix would be a throughput question and the whole issue would be
/// mis-framed.</para>
///
/// <para><b>It is not the steady state, and this test is the measurement.</b> The pipeline's first
/// leaf is a storage read, and storage reads go through <c>IIoPool</c> — so the chain leaves the
/// block at that hop and the handler returns in milliseconds. Parking a create INSIDE its own
/// validator (which runs downstream of that read) therefore parks a pool thread, not the pump: the
/// execution hub reads completely idle while a create is provably in flight. Measured, not assumed —
/// the first draft of this test asserted the opposite and failed with
/// <c>Queue(buffer=0,deferred=0,drainsInFlight=0,openGates=0,draining=False)</c>.</para>
///
/// <para><b>What that buys the investigation.</b> A create that holds the block for 24.9 s is doing
/// something this path does not normally do, so "which rule holds the block" is not answered by
/// "the create pipeline is expensive". It also gives the invariant a guard: should a future change
/// move a synchronous leaf ahead of the pool hop — a blocking service resolution, an eager address
/// resolution, a hosted-hub construction — every node CRUD in the mesh would start serialising
/// behind it, and this test goes red at that commit instead of in a bake three weeks later.</para>
///
/// <para>Companion: <c>TurnLoopSnapshotIsMeasuredTest</c> (MeshWeaver.Messaging.Hub.Test) is the
/// positive control for the two fields read here — it builds a genuinely blocked block and asserts
/// they say so. Together: the fields move, and node CRUD does not move them.</para>
/// </summary>
public class NodeCrudDoesNotOccupyTheExecutionBlockTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string ParkedNodeId = "TurnStateParkedWrite";
    private const string ParkedNodePath = $"{TestPartition}/{ParkedNodeId}";

    /// <summary>The INDEPENDENT second operation: never parked, so only a held pump delays it.</summary>
    private const string ProbeNodeId = "TurnStateProbeWrite";

    /// <summary>The control's node — created with nothing parked, so it measures the probe itself.</summary>
    private const string ControlNodeId = "TurnStateControlWrite";

    /// <summary>Bound for the probe. Deliberately WELL under the parked validator's own
    /// <c>TestTimeouts.Convergence</c> spin, so the park cannot expire first and hand the probe a
    /// pass that says nothing about whether the pump was free.</summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(10);

    /// <summary>The two nodes parked SIMULTANEOUSLY by the parallelism test below.</summary>
    private const string ParallelANodeId = "TurnStateParallelA";

    private const string ParallelBNodeId = "TurnStateParallelB";

    private const string ParallelAPath = $"{TestPartition}/{ParallelANodeId}";

    private const string ParallelBPath = $"{TestPartition}/{ParallelBNodeId}";

    /// <summary>Producer → test: the parking validator completes this once its turn is running.</summary>
    private readonly AsyncSubject<Unit> _parked = new();

    private readonly AsyncSubject<Unit> _parkedA = new();

    private readonly AsyncSubject<Unit> _parkedB = new();

    private ParkTheNodeCrudBlock? _park;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        _park = new ParkTheNodeCrudBlock(new Dictionary<string, AsyncSubject<Unit>>
        {
            [ParkedNodePath] = _parked,
            [ParallelAPath] = _parkedA,
            [ParallelBPath] = _parkedB,
        });
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeValidator>(_park));
    }

    /// <summary>The mesh's ONE node-CRUD execution hub — the hub #2543 is about.</summary>
    private MessageHub NodeOperationHub =>
        Mesh.GetHostedHub(Mesh.NodeOperationTarget(), HostedHubCreation.Never) as MessageHub
        ?? throw new InvalidOperationException(
            "the node-CRUD execution hub is not materialised — the test cannot describe a hub that "
            + "does not exist; a node operation must have been issued first");

    /// <summary>
    /// 🚨 THE MEASUREMENT. A create is blocked mid-pipeline, in its own validator, and the hub that
    /// EXECUTES node CRUD is idle: no drain body, no executing turn, nothing queued.
    ///
    /// <para>So the create pipeline is not what a 25 s <c>Executing(CreateNodeRequest, …)</c>
    /// reports. Anything that DOES hold that block for tens of seconds is running ahead of the
    /// pipeline's first pooled hop, and that is a much smaller place to look.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheExecutionBlock_IsFree_WhileACreateIsInFlightInsideItsValidator()
    {
        // A REAL node create, posted exactly as production does, so what it exercises is the
        // production path rather than a stand-in.
        var write = ObserveNodeOperation(new CreateNodeRequest(
            new MeshNode(ParkedNodeId, TestPartition)
            {
                Name = "Turn State Parked Write",
                NodeType = "Markdown",
            }));
        // 🚨 THE BLOCK IS MEASURED BY WHAT IT CAN STILL DO, NOT BY GetTurnLoopSnapshot().
        //
        // The snapshot's `Executing(...)` clause CANNOT answer this question, and the first version
        // of this test failed because it asked. `currentlyExecutingMessageType` is cleared in the
        // `.Finally(...)` of the handler's returned OBSERVABLE (MessageService), so it stays set for
        // as long as the handler's chain is in flight — including every pooled hop the chain has
        // already left the block for. Its elapsed is therefore the handler's lifetime, not the time
        // the pump was occupied: with the create parked here, one run read
        // `Executing(CreateNodeResponse, 36001ms)` while the pump was idle throughout, and the
        // number tracked the park window exactly because the park IS the handler's lifetime.
        // `drainsInFlight`/`draining` read 1/True for the same reason.
        //
        // That is also why #2543's `Executing(CreateNodeRequest, 24888ms)` does not by itself say
        // the block was held for 24.9 s — the same field, the same ambiguity.
        //
        // So the claim is measured behaviourally: with a create provably parked, post an INDEPENDENT
        // node operation to the same hub and require it to complete. A pump held by the parked
        // create cannot answer it; a pump that answers is free by demonstration, whatever the
        // snapshot says.
        try
        {
            await _parked.Should().Within(TestTimeouts.Convergence)
                .Emit("the create must actually be in flight and blocked, or this test describes an "
                    + "idle mesh and proves nothing");

            var probe = ObserveNodeOperation(new CreateNodeRequest(
                new MeshNode(ProbeNodeId, TestPartition)
                {
                    Name = "Turn State Probe Write",
                    NodeType = "Markdown",
                }));

            await probe.Should().Within(ProbeBudget)
                .Emit("the node-CRUD execution hub must still process a SECOND node operation while "
                    + "the first is parked inside its validator: the create pipeline leaves the "
                    + "action block at its first pooled storage hop, so it is a pool thread that is "
                    + "parked, not the pump. If this times out the block IS held for the duration of "
                    + "a create, every node CRUD in the mesh serialises behind it, and #2543's "
                    + "24.9 s capture is routine rather than anomalous. Turn-loop snapshot at this "
                    + "moment (diagnostic only, see above — it cannot decide this): "
                    + NodeOperationHub.GetTurnLoopSnapshot());
        }
        finally
        {
            // In a finally so a failing assertion cannot strand the parked write — and therefore
            // cannot strand the mesh's teardown behind it.
            _park!.Release();
        }

        await write.Should().Within(TestTimeouts.Convergence)
            .Emit("the parked write must complete once released — a stranded create would leak into "
                + "the next test");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL for the measurement above, and it is not optional. The probe there
    /// is read as "the pump is held" when it does not complete — a reading that is only valid if
    /// the very same operation DOES complete when nothing is parked. Without this, a probe that
    /// never completes for its own reasons looks exactly like a held pump, and the conclusion
    /// would be drawn from an instrument that cannot answer.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheProbeOperation_CompletesPromptly_WhenNothingIsParked()
    {
        var probe = ObserveNodeOperation(new CreateNodeRequest(
            new MeshNode(ControlNodeId, TestPartition)
            {
                Name = "Turn State Control Write",
                NodeType = "Markdown",
            }));

        await probe.Should().Within(ProbeBudget)
            .Emit("an unparked node create must complete well inside the budget the probe above "
                + "uses — that budget is only meaningful if this passes");
    }

    /// <summary>
    /// 🚨 <b>Node CRUD runs in PARALLEL</b> — the property the decoupling in
    /// <c>MessageHub.ContinueOffBlockRestoringUserContext</c> exists to give, asserted without a
    /// stopwatch.
    ///
    /// <para>Two creates are parked in their validators AT THE SAME TIME. Both validators must be
    /// entered before either is released, which can only happen if two creates are in flight
    /// concurrently. When every response continuation ran on <c>portal/nodeops</c>'s action block,
    /// the second create could not even be DEQUEUED while the first was parked — it sat at
    /// <c>Queue(buffer=1,…)</c> and its validator never ran, so this test could not pass by
    /// timing luck. It is a structural assertion, not a benchmark.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TwoCreates_AreInFlightSimultaneously()
    {
        var a = ObserveNodeOperation(new CreateNodeRequest(
            new MeshNode(ParallelANodeId, TestPartition) { Name = "Parallel A", NodeType = "Markdown" }));
        var b = ObserveNodeOperation(new CreateNodeRequest(
            new MeshNode(ParallelBNodeId, TestPartition) { Name = "Parallel B", NodeType = "Markdown" }));

        try
        {
            await _parkedA.Should().Within(TestTimeouts.Convergence)
                .Emit("the first create must reach its validator");
            await _parkedB.Should().Within(ProbeBudget)
                .Emit("the SECOND create must reach its validator while the first is still parked "
                    + "there — two node writes genuinely in flight at once. A hub that runs "
                    + "continuations on its own action block cannot get here: the second create "
                    + "would still be queued behind the first, unstarted");
        }
        finally
        {
            _park!.Release();
        }

        await a.Should().Within(TestTimeouts.Convergence).Emit("the first create must complete");
        await b.Should().Within(TestTimeouts.Convergence).Emit("the second create must complete");
    }

    /// <summary>
    /// Blocks ONE create inside its validator. The validator runs downstream of the create
    /// pipeline's first storage read, which is pooled — so this parks a pool thread, which is
    /// exactly the fact under test.
    ///
    /// <para>No hand-woven gate: producer → test is the <see cref="AsyncSubject{T}"/> this completes
    /// on entry; test → parked worker is a volatile flag polled under a bounded
    /// <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>. Every other path validates instantly,
    /// so nothing else in the mesh is slowed down. (Same harness as
    /// <c>ContentReadIsNotQueuedBehindNodeCrudTest</c>.)</para>
    /// </summary>
    private sealed class ParkTheNodeCrudBlock(
        IReadOnlyDictionary<string, AsyncSubject<Unit>> parkPoints) : INodeValidator
    {
        private int _released;

        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

        /// <summary>Lets the parked create finish. Idempotent.</summary>
        public void Release() => Interlocked.Exchange(ref _released, 1);

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => parkPoints.TryGetValue(context.Node.Path, out var parked)
                ? Observable.Defer(() =>
                {
                    parked.OnNext(Unit.Default);
                    parked.OnCompleted();
                    SpinWait.SpinUntil(() => Volatile.Read(ref _released) == 1,
                        TestTimeouts.Convergence);
                    return Observable.Return(NodeValidationResult.Valid());
                })
                : Observable.Return(NodeValidationResult.Valid());
    }
}
