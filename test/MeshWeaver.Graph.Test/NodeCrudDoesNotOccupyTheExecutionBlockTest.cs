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

    /// <summary>Producer → test: the parking validator completes this once its turn is running.</summary>
    private readonly AsyncSubject<Unit> _parked = new();

    private ParkTheNodeCrudBlock? _park;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        _park = new ParkTheNodeCrudBlock(ParkedNodePath, _parked);
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
        string snapshot;
        try
        {
            await _parked.Should().Within(TestTimeouts.Convergence)
                .Emit("the create must actually be in flight and blocked, or this test describes an "
                    + "idle mesh and proves nothing");

            snapshot = NodeOperationHub.GetTurnLoopSnapshot();
        }
        finally
        {
            // In a finally so a failing assertion cannot strand the parked write — and therefore
            // cannot strand the mesh's teardown behind it.
            _park!.Release();
        }

        snapshot.Should().NotContain("Executing(",
            "the create pipeline left the action block at its first pooled storage hop, so no "
            + "handler is on the block while the create is blocked downstream — a create that DID "
            + "hold the block would make #2543's 24.9 s capture routine instead of anomalous");
        snapshot.Should().Contain("drainsInFlight=0",
            "no drain body is executing: the parked work is on a pool thread, not on the pump");
        snapshot.Should().Contain("buffer=0",
            "and nothing is queued behind it — node CRUD in flight does not back up this hub");

        await write.Should().Within(TestTimeouts.Convergence)
            .Emit("the parked write must complete once released — a stranded create would leak into "
                + "the next test");
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
    private sealed class ParkTheNodeCrudBlock(string parkPath, AsyncSubject<Unit> parked) : INodeValidator
    {
        private int _released;

        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

        /// <summary>Lets the parked create finish. Idempotent.</summary>
        public void Release() => Interlocked.Exchange(ref _released, 1);

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => string.Equals(context.Node.Path, parkPath, StringComparison.Ordinal)
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
