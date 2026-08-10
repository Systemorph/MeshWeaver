using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Pins the NO-SILENT-HANG contract of <c>HandleCreateNodeRequest</c> (#981): the handler owes its
/// reply from a DETACHED reactive chain, and <b>every</b> way that chain can terminate must produce a
/// terminal answer — because the alternative is not a failed create, it is a requester whose
/// <c>hub.Observe</c> callback stays pending FOREVER.
///
/// <para><b>The defect these pin.</b> <see cref="IStorageAdapter.Write"/> emits <c>null</c> as the
/// documented try-then-claim sentinel — "this adapter does not own this path", NOT "the write
/// succeeded". The composite <c>PersistenceService.Write</c> folds that across its providers and
/// THROWS when every one declines, which the handler's <c>onError</c> arm answers correctly. But the
/// resolved <see cref="IStorageAdapter"/> is not always that composite (the non-partitioned wirings
/// resolve a single decorated adapter, and <c>PathFilteringStorageAdapter</c>,
/// <c>PostgreSqlPathRoutingAdapter</c>, <c>SnowflakePathRoutingAdapter</c>, <c>RoutingProxyAdapter</c>
/// and <c>StaticNodeStorageAdapter</c> all decline by contract). The handler used to filter that null
/// away with <c>.Where(n =&gt; n is not null)</c>, so the chain completed empty, no response was ever
/// posted, and ONE condition either failed cleanly or hung forever depending purely on which adapter
/// the hub happened to resolve.</para>
///
/// <para><b>Why the first two assertions read as timeouts before the fix.</b> There is no failure
/// response to assert on when the bug is present — the request is simply never answered, so
/// <c>Within(...).Emit(...)</c> times out. That IS the negative control: the current behaviour is a
/// hang, and a hang is what the reactive assertion reports.</para>
///
/// <para>The last two tests guard the other direction, which is the easier thing to get wrong: the
/// terminal backstop must NOT fire when a branch already answered (already-exists keeps its own
/// specific rejection reason) and must NOT race a real success into a spurious failure
/// (<see cref="LatePostCreationHandler"/> forces the Ok to be posted strictly AFTER the chain's
/// <c>onCompleted</c> has run, which is exactly when a mis-guarded backstop would overtake it).</para>
/// </summary>
public class CreateNodeAlwaysAnswersTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Partition = "Test";

    /// <summary>
    /// Storage adapter whose WRITE behaviour the test dictates, over a real in-memory read side.
    /// Everything else is the minimum the create handler touches: <c>Read</c> answers the existence
    /// probe, <c>Exists</c> answers the NodeType-registered probe.
    /// </summary>
    private sealed class ScriptedWriteAdapter(Func<MeshNode, IObservable<MeshNode?>> write) : IStorageAdapter
    {
        private readonly ConcurrentDictionary<string, MeshNode> stored = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Seeds a node so the create handler's existence probe finds it.</summary>
        public void Seed(MeshNode node) => stored[node.Path] = node;

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => Observable.Return(stored.TryGetValue(path, out var node) ? node : null);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => write(node);

        public IObservable<string> Delete(string path) => Observable.Return(path);

        // True so the handler's NodeType-existence probe passes — this stub is not the subject
        // under test, the terminal-answer contract is.
        public IObservable<bool> Exists(string path) => Observable.Return(true);

        public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
            string fullPath, JsonSerializerOptions options)
            => Observable.Return<(MeshNode?, int)>((null, 0));

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath)
            => Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []));

        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => Observable.Empty<object>();

        public IObservable<System.Reactive.Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => Observable.Return(System.Reactive.Unit.Default);

        public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => Observable.Return(System.Reactive.Unit.Default);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => Observable.Return<DateTimeOffset?>(null);

        public IObservable<DataChangeNotification> Changes => Observable.Never<DataChangeNotification>();
    }

    /// <summary>
    /// Post-creation handler that completes on ANOTHER scheduler, so the success response is posted
    /// strictly after the create chain's <c>onCompleted</c> arm has already run. Without that hop the
    /// Ok is posted synchronously inside <c>onNext</c> and a backstop that forgot to check "did the
    /// chain emit?" would still lose the race by accident — i.e. the success test would pass for the
    /// wrong reason. This is a scheduler hop, not a sleep: nothing waits on wall-clock time.
    ///
    /// <para>Registered for every test in the class — it is reached only on the success path, and
    /// registering it unconditionally keeps the host wiring independent of which test is running.</para>
    /// </summary>
    private sealed class LatePostCreationHandler : INodePostCreationHandler
    {
        public string NodeType => "Markdown";

        public IObservable<System.Reactive.Unit> Handle(MeshNode createdNode, string? createdBy)
            => Observable.Return(System.Reactive.Unit.Default).ObserveOn(TaskPoolScheduler.Default);
    }

    /// <summary>
    /// Write behaviour for the test currently running. Read at Write time (not at registration
    /// time), so a test body can set it before issuing its request.
    /// </summary>
    private Func<MeshNode, IObservable<MeshNode?>> writeBehaviour =
        node => Observable.Return<MeshNode?>(node);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services => services
                .AddSingleton(new MeshConfiguration(new List<MeshNode>()))
                .AddSingleton<IStorageAdapter>(Adapter)
                .AddSingleton<INodePostCreationHandler>(new LatePostCreationHandler()))
            .WithNodeOperationHandlers();

    private ScriptedWriteAdapter? resolvedAdapter;

    /// <summary>
    /// The one adapter instance the host resolves — created lazily so the field is available to
    /// both <c>ConfigureHost</c> (registration) and the test body (seeding).
    /// </summary>
    private ScriptedWriteAdapter Adapter
        => resolvedAdapter ??= new ScriptedWriteAdapter(node => writeBehaviour(node));

    private static MeshNode NewNode(string id) => new(id, Partition)
    {
        Name = "Probe",
        NodeType = "Markdown",
        State = MeshNodeState.Active,
    };

    private Task<IMessageDelivery<CreateNodeResponse>> Create(MeshNode node, string because)
        => GetHost()
            .Observe<CreateNodeResponse>(new CreateNodeRequest(node), o => o.WithTarget(CreateHostAddress()))
            .Should().Within(15.Seconds()).Emit(because);

    /// <summary>
    /// The named cause: the adapter DECLINES the path (the try-then-claim <c>null</c> sentinel). Before
    /// the fix the null was filtered out, the chain completed empty and nothing was ever posted — this
    /// assertion timed out rather than failing on a wrong response, because a hang is the bug.
    /// </summary>
    [Fact]
    public async Task Create_WhenAdapterDeclinesTheWrite_AnswersWithFailure()
    {
        writeBehaviour = _ => Observable.Return<MeshNode?>(null);

        var response = await Create(NewNode("Declined"),
            "a declined write must be ANSWERED — the same condition already throws (and is answered) "
            + "when the composite PersistenceService is the resolved adapter, so whether the caller "
            + "gets a reply must not depend on the storage wiring underneath it");

        response.Message.Success.Should().BeFalse();
        response.Message.Error.Should().Contain("declined",
            "the failure must name the decline, so a reader is not left guessing why nothing was written");
        response.Message.Error.Should().Contain(nameof(ScriptedWriteAdapter),
            "and it must name the adapter that declined — that is the piece every #981 capture lacked");
    }

    /// <summary>
    /// The second empty-completion class, which the decline fix alone does NOT cover: a storage leaf
    /// that completes without emitting at all. Every upstream in the chain (the existence read, the
    /// partition bootstrap, the validators, the NodeType probe, the save) is an adapter-supplied
    /// observable free to do this, so the terminal arm — not the save site — is what has to close it.
    /// </summary>
    [Fact]
    public async Task Create_WhenSaveCompletesWithoutEmitting_AnswersWithFailure()
    {
        writeBehaviour = _ => Observable.Empty<MeshNode?>();

        var response = await Create(NewNode("EmptySave"),
            "a chain that terminates without emitting anything must still answer — nothing was created, "
            + "so no Ok can ever be right, and a silent completion leaves the caller pending forever");

        response.Message.Success.Should().BeFalse();
        response.Message.Error.Should().Contain("terminated without producing a node",
            "the failure must say the pipeline terminated, not invent a rejection reason it cannot know");
    }

    /// <summary>
    /// The backstop must not REPLACE a branch's own answer. Already-exists posts a Fail and then
    /// returns <c>Observable.Empty</c> — an empty completion that is correctly answered — so the
    /// specific rejection reason must survive rather than being flattened into the backstop's Unknown.
    /// </summary>
    [Fact]
    public async Task Create_WhenNodeAlreadyExists_KeepsItsOwnRejectionReason()
    {
        var node = NewNode("Existing");
        Adapter.Seed(node);

        var response = await Create(node, "an existing node must be reported as such");

        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.NodeAlreadyExists,
            "this branch answers for itself, so the terminal backstop must stand down — a backstop that "
            + "fires on every empty completion would turn a precise rejection into a generic one");
    }

    /// <summary>
    /// The direction that matters most: a real success must not be converted into a failure. The late
    /// post-creation handler makes the Ok arrive AFTER the chain's <c>onCompleted</c>, so a backstop
    /// that only checked "was a response posted?" (and not "did the chain emit?") would answer Fail
    /// first and win the race.
    /// </summary>
    [Fact]
    public async Task Create_WhenWriteIsAccepted_AnswersOkEvenThoughTheOkIsPostedAfterCompletion()
    {
        writeBehaviour = node => Observable.Return<MeshNode?>(node);

        var response = await Create(NewNode("Accepted"),
            "a successful create must answer Ok even when the terminal response is posted after the "
            + "chain has already completed — the backstop must never overtake a create that emitted");

        response.Message.Success.Should().BeTrue();
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.Path.Should().Be($"{Partition}/Accepted");
    }
}
