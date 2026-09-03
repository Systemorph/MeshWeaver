using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
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
/// 🚨 A bounded READ must not be issued on the hub that EXECUTES the mesh's node CRUD —
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/2901">#2901</see>, *"a freshly
/// uploaded content file 503s for minutes"*.
///
/// <para><b>The defect.</b> <c>/api/content/{node}/{collection}/{file}</c> reads the owning node's
/// collection config with a <c>GetDataRequest</c> bounded by <c>ReadBudget.Default</c> (10 s), and
/// <c>ContentFileResolver</c> issued it through <c>MeshExtensions.NodeOperationIssuingHub()</c> —
/// which for the <c>/api/content</c> endpoint (it holds the DI-injected root hub, i.e. the router)
/// resolves to <c>portal/nodeops-{meshId}</c>, <b>the mesh's ONE node-CRUD execution hub</b>. Every
/// <c>CreateNodeRequest</c>/<c>CreateOrUpdateNodeRequest</c> in the mesh runs on that hub's single
/// action block, one turn at a time, and <c>MessageService.DrainOne</c> does not advance until the
/// current turn's observable completes. <c>HandleCallbacks</c> is an ordinary delivery rule
/// (<c>MessageHub.cs:451</c>), so the reply to this read — once it has been DELIVERED — sits in
/// that block's buffer until the block drains. The read then burns its whole budget on an answer
/// that already arrived, <c>HubUnreachableException</c> is a <c>TimeoutException</c>, and the route
/// maps it to <b>503</b>.</para>
///
/// <para><b>Why an upload produces exactly that.</b> Uploading N files starts N indexing
/// activities, each costing node-CRUD round trips on that same block, so the block is busy for as
/// long as the burst lasts — minutes when the slowest leg is a vision-model call. That is #2901's
/// "503 for minutes, then it heals untouched", and why it is per-file rather than per-route: it is
/// whichever read lands inside the burst. Full elimination in
/// <c>Doc/Architecture/ContentRoute503</c>.</para>
///
/// <para><b>The repro.</b> No upload burst is needed to pin the mechanism — only a node-CRUD turn
/// that is genuinely in flight. An <see cref="INodeValidator"/> parks the create of ONE node
/// (nothing else is affected: it matches on path), which holds the node-CRUD execution hub's turn
/// exactly as a real write does. With the block held, the content read must still answer. Before
/// the fix it does not: it errors with <c>HubUnreachableException</c> after the full 10 s budget,
/// which is the 503. After it, the read answers in milliseconds because it is issued on
/// <c>portal/reads-{meshId}</c>, a hub that registers no handlers and therefore only ever
/// dispatches the replies to reads issued on it.</para>
///
/// <para>🚨 <b>What this does NOT claim.</b> It does not make the node-CRUD hub drain any faster —
/// why a single <c>CreateNodeRequest</c> turn can occupy that block for tens of seconds is
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/2543">#2543</see> and is untouched
/// here. This pins the read side: an interactive read has no business sharing an action block with
/// background node CRUD, whatever that CRUD costs.</para>
/// </summary>
public class ContentReadIsNotQueuedBehindNodeCrudTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string ProbeNodeType = "ContentRoute503Probe";
    private const string ProbeNodeId = "ContentProbe";
    private const string ProbeNodePath = $"{TestPartition}/{ProbeNodeId}";
    private const string ParkedNodeId = "ParkedWrite";
    private const string ParkedNodePath = $"{TestPartition}/{ParkedNodeId}";
    private const string UploadedFileName = "uploaded.bin";

    /// <summary>
    /// The collection's backing directory — per test CLASS instance, so nothing is shared with
    /// another suite running in the same xUnit process.
    /// </summary>
    private readonly string _contentRoot = Path.Combine(
        AppContext.BaseDirectory, "Files", "ContentRoute503", Guid.NewGuid().ToString("N"));

    /// <summary>Producer → test: the parking validator completes this once its turn is running.</summary>
    private readonly AsyncSubject<Unit> _parked = new();

    private ParkTheNodeCrudBlock? _park;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        _park = new ParkTheNodeCrudBlock(ParkedNodePath, _parked);
        Directory.CreateDirectory(_contentRoot);
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeValidator>(_park))
            .AddMeshNodes(
                new MeshNode(ProbeNodeType)
                {
                    Name = "Content Route 503 Probe",
                    // The one thing the node type needs for this test: a content collection, so the
                    // per-node hub answers the collection-config GetDataRequest the route issues.
                    HubConfiguration = config => config.AddFileSystemContentCollection(
                        ContentCollectionsExtensions.DefaultCollectionName, _ => _contentRoot)
                },
                new MeshNode(ProbeNodeId, TestPartition)
                {
                    Name = "Content Probe",
                    NodeType = ProbeNodeType,
                });
    }

    /// <summary>
    /// 🚨 THE REGRESSION. A content read must answer while the mesh's node-CRUD execution hub is
    /// occupied by a write. RED before the fix: the read is issued on that very hub, so its reply
    /// cannot be dispatched, and it errors with <c>HubUnreachableException</c> after
    /// <c>ReadBudget.Default</c> — the 503 the issue was filed on.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ContentFileResolution_Answers_WhileANodeWriteHoldsTheExecutionHub()
    {
        var uploaded = Path.Combine(_contentRoot, UploadedFileName);
        await File.WriteAllBytesAsync(uploaded, "uploaded bytes"u8.ToArray(),
            TestContext.Current.CancellationToken);

        // The write is a REAL node create, posted from a client hub and addressed at
        // NodeOperationTarget() exactly as production does — so what it occupies is the production
        // block, not a stand-in.
        var write = ObserveNodeOperation(new CreateNodeRequest(
            new MeshNode(ParkedNodeId, TestPartition)
            {
                Name = "Parked Write",
                NodeType = "Markdown",
            }));
        try
        {
            await _parked.Should().Within(TestTimeouts.Convergence)
                .Emit("the node-CRUD execution hub must actually be busy, or this test proves nothing");

            var reference =
                $"{ProbeNodePath}/{ContentCollectionsExtensions.DefaultCollectionName}/{UploadedFileName}";
            var resolution = await ContentFileResolver.Resolve(Mesh, reference)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the content route must answer while an unrelated node write is in flight — "
                    + "before the fix this read was issued on the write's own action block, so its "
                    + "reply could not be dispatched and it timed out into a 503 (#2901)");

            resolution.Reason.Should().BeNull(
                "the collection config resolved, so there is no reason-for-no-resolution");
            resolution.Resolution.Should().NotBeNull(
                "the probe node serves a 'content' collection and the file is in it");
            resolution.Resolution!.Collection.Name.Should().Be(
                ContentCollectionsExtensions.DefaultCollectionName);
            resolution.Resolution.FilePath.Should().Be(UploadedFileName);
            File.Exists(Path.Combine(_contentRoot, resolution.Resolution.FilePath))
                .Should().BeTrue("the resolution must point at the file that was uploaded");
        }
        finally
        {
            // In a finally so a failing assertion cannot strand the parked write — and therefore
            // cannot strand the mesh's teardown behind it.
            _park!.Release();
        }

        await write.Should().Within(TestTimeouts.Convergence)
            .Emit("the parked write must complete once released — a stranded node-CRUD turn would "
                + "leak into the next test");
    }

    /// <summary>
    /// The same seam, from the generic one-shot node read. <c>GetMeshNodeOutcome</c> issued its
    /// <c>GetDataRequest</c> on the node-CRUD execution hub for the same historical reason, and
    /// carries the same 10 s budget — the <c>"this read reached no verdict … within 10s"</c> the
    /// issue also records against ordinary node reads on a loaded portal.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OneShotNodeRead_Answers_WhileANodeWriteHoldsTheExecutionHub()
    {
        var write = ObserveNodeOperation(new CreateNodeRequest(
            new MeshNode(ParkedNodeId, TestPartition)
            {
                Name = "Parked Write",
                NodeType = "Markdown",
            }));
        try
        {
            await _parked.Should().Within(TestTimeouts.Convergence)
                .Emit("the node-CRUD execution hub must actually be busy");

            var node = await Mesh.GetMeshNode(ProbeNodePath)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("a one-shot node read must answer while an unrelated node write is in flight");

            node.Should().NotBeNull("the probe node exists");
            node!.Path.Should().Be(ProbeNodePath);
        }
        finally
        {
            _park!.Release();
        }

        await write.Should().Within(TestTimeouts.Convergence)
            .Emit("the parked write must complete once released");
    }

    /// <summary>
    /// The structural invariant behind both facts, asserted directly so a future refactor cannot
    /// quietly put the read back on the write block: the hub a router-held caller issues a READ on
    /// is not the hub node CRUD is EXECUTED on.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void TheReadIssuingHub_IsNotTheNodeOperationExecutionHub()
    {
        Mesh.ReadIssuingHub().Address.Should().NotBe(Mesh.NodeOperationTarget(),
            "a bounded read must not be dispatched by the action block that runs every "
            + "create/upsert in the mesh (#2901)");
        Mesh.ReadIssuingHub().Address.Should().NotBe(Mesh.Address,
            "and it must not be the router either — the router must be neither end of a delivery");
    }

    /// <summary>
    /// Holds ONE create's turn on whichever hub executes it — the mesh's node-CRUD execution hub.
    ///
    /// <para>No hand-woven gate: producer → test is the <see cref="AsyncSubject{T}"/> this
    /// completes on entry; test → parked worker is a volatile flag polled under a bounded
    /// <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>, which is what a real long-running
    /// turn does to that block. Every other path validates instantly, so nothing else in the mesh
    /// is slowed down.</para>
    /// </summary>
    private sealed class ParkTheNodeCrudBlock(string parkPath, AsyncSubject<Unit> parked) : INodeValidator
    {
        private int _released;

        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

        /// <summary>Lets the parked turn finish. Idempotent.</summary>
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
