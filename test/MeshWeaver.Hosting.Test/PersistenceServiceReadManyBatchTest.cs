using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>THE FACADE MUST FORWARD A BATCH, NOT DEGRADE IT INTO N POINT READS</b> — MeshWeaver#4200.
///
/// <para><b>The defect.</b> <see cref="PersistenceService"/> is the <see cref="IStorageAdapter"/>
/// every deployed host resolves from DI, and it did not override
/// <see cref="IStorageAdapter.ReadMany"/>. Neither guard decorator above it introduces one either —
/// <c>SubtreeDeletionGuardStorageAdapter</c> and <c>MonotonicWriteGuardStorageAdapter</c> both
/// delegate straight through — so every batched read in the platform fell back to the INTERFACE
/// DEFAULT, <c>Observable.Merge(paths.Select(Read))</c>: exactly the N unbounded-concurrency point
/// reads of possibly-absent paths that the callers say in so many words they are avoiding
/// (<c>InstallCompleteness.Observe</c>, <c>StaleMainNodeRepair</c>,
/// <c>StorageAdapterMeshQueryProvider</c>'s exact-path probe). A backend's batched override was
/// unreachable from the facade.</para>
///
/// <para><b>Why it is not merely round-trips.</b> The single-path and batched Postgres paths DO NOT
/// FAIL THE SAME WAY. <c>PostgreSqlStorageAdapter.Read</c> catches <c>PostgresException</c> with
/// <c>SqlState == UndefinedTable</c> (42P01 — a half-provisioned partition whose satellite table was
/// never created) and answers <c>null</c>, i.e. silently "no node"; the batched read has no such
/// catch and FAULTS. Under the degraded default, therefore, "this partition's table does not exist"
/// was spelled identically to "this node is absent" — and <c>InstallCompleteness</c> turns that into
/// <c>Incomplete</c> plus an ABSENT name for a node that is fine, where the batched path would have
/// reached its <c>.Catch</c> and reported the honest <c>NotObserved</c>: "the mesh could not be
/// read, so completeness was NOT checked — this is not a pass".
/// <see cref="AProviderWhoseBatchFaults_FaultsTheRead_RatherThanReportingItsNodesAbsent"/> is that
/// asymmetry expressed as an invariant.</para>
/// </summary>
public class PersistenceServiceReadManyBatchTest
{
    private static readonly JsonSerializerOptions Options = new();

    private const string Partition = "Pkg";
    private const string Shared = $"{Partition}/Shared";
    private const string OnlyInA = $"{Partition}/OnlyInA";
    private const string OnlyInB = $"{Partition}/OnlyInB";
    private const string Nowhere = $"{Partition}/Nowhere";

    private static MeshNode Node(string path, string name) =>
        MeshNode.FromPath(path) with
        {
            NodeType = "Markdown",
            Name = name,
            State = MeshNodeState.Active,
        };

    // ── The pins ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE REGRESSION. One <see cref="IStorageAdapter.ReadMany"/> over N paths must reach the
    /// provider's BATCHED entry point exactly once, and its single-path <c>Read</c> not at all.
    ///
    /// <para>Before the override this fails with the numbers inverted: the batched entry point is
    /// never called and <c>Read</c> is called once per path. The result set is asserted too, so a
    /// forwarding that batched correctly but lost nodes could not pass either.</para>
    /// </summary>
    [Fact]
    public async Task OneBatchedRead_ReachesTheProvidersBatchedEntryPoint_Once()
    {
        var store = new RecordingAdapter([Node(Shared, "s"), Node(OnlyInA, "a"), Node(OnlyInB, "b")]);
        IStorageAdapter persistence = new PersistenceService([new Provider("A", store)]);

        var read = await persistence
            .ReadMany([Shared, OnlyInA, OnlyInB], Options)
            .ToList()
            .Timeout(TestTimeouts.Quick)
            .Await(TestContext.Current.CancellationToken);

        read.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            [OnlyInA, OnlyInB, Shared],
            "the batch must return every node it asked for");
        store.BatchCalls.Should().Be(1,
            "the facade must FORWARD the batch to the provider's ReadMany — a backend that batches "
            + "(Postgres: one `WHERE path = ANY($1)` per resolved table) is unreachable otherwise");
        store.SingleReads.Should().Be(0,
            "the interface DEFAULT fans the batch out into one point read per path; that is the "
            + "shape every caller of ReadMany documents itself as avoiding (#4200)");
        store.Batches.Should().ContainSingle().Which.Should().Equal(
            [OnlyInA, OnlyInB, Shared],
            "the whole requested set goes to the first provider in one call");
    }

    /// <summary>
    /// First-provider-wins, applied to a SET: the walk is <see cref="PersistenceService.ReadCore"/>'s
    /// rule (providers in <c>_allOrdered</c> order, first non-null answer keeps the path), and the
    /// second provider is asked ONLY for what the first did not answer.
    ///
    /// <para>The shared path is held by BOTH providers with DIFFERENT content, so a walk that
    /// merged instead of concatenating — or that asked every provider for every path — would show
    /// up here as B's copy, or as a duplicate. The winning copy is asserted to be IDENTICAL to what
    /// the single-path <see cref="IStorageAdapter.Read"/> serves for the same path, so the two entry
    /// points cannot drift into disagreeing about who owns a node.</para>
    /// </summary>
    [Fact]
    public async Task TheFirstProviderThatHasAPath_WinsIt_AndTheNextIsAskedOnlyForTheRemainder()
    {
        var a = new RecordingAdapter([Node(Shared, "from A"), Node(OnlyInA, "a")]);
        var b = new RecordingAdapter([Node(Shared, "from B"), Node(OnlyInB, "b")]);
        IStorageAdapter persistence = new PersistenceService(
            [new Provider("A", a), new Provider("B", b)]);

        var read = await persistence
            .ReadMany([Shared, OnlyInA, OnlyInB, Nowhere], Options)
            .ToList()
            .Timeout(TestTimeouts.Quick)
            .Await(TestContext.Current.CancellationToken);

        read.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            [OnlyInA, OnlyInB, Shared],
            "every provider's paths are served, and a path NO provider holds is simply ABSENT from "
            + "the sequence — never an error, never a null (that tolerance is ReadMany's contract)");
        read.Select(n => n.Path).Should().NotContain(Nowhere,
            "a missing path is absent, not an error — the caller decides what absence means");

        var winner = read.Single(n => string.Equals(n.Path, Shared, StringComparison.OrdinalIgnoreCase));
        winner.Name.Should().Be("from A",
            "providers are walked in _allOrdered order and the FIRST that has the path wins it — "
            + "a Merge across providers would let B answer for a path A already owns");

        var pointRead = await persistence.Read(Shared, Options)
            .Timeout(TestTimeouts.Quick)
            .Await(TestContext.Current.CancellationToken);
        winner.Name.Should().Be(pointRead!.Name,
            "the batched and single-path entry points must agree about who owns a node; asserting "
            + "the equality directly is what stops them drifting");

        b.Batches.Should().ContainSingle().Which.Should().Equal(
            [Nowhere, OnlyInB],
            "provider B is asked only for the paths still unanswered after A — the remaining set is "
            + "carried forward, so a path A already served is never re-read downstream");
    }

    /// <summary>
    /// 🚨 <b>THE PRODUCTION STACK, not a bare facade.</b> The facade's override is worth nothing if
    /// a decorator above it does not forward the batch — and one did not: the chain
    /// <c>PersistenceExtensions.DecorateStorageAdapterWithVersionWriting</c> registers is
    /// <c>SubtreeDeletionGuard → MonotonicWriteGuard → VersionWriting → PersistenceService</c>, and
    /// <c>VersionWritingStorageAdapter</c> declared no <c>ReadMany</c>. Dispatch therefore landed on
    /// the interface default INSIDE the chain, fanned the batch back out through that decorator's
    /// own single-path <c>Read</c>, and the facade's override was never reached — so every arm
    /// above could pass while every deployed host still degraded.
    ///
    /// <para>This arm exercises the chain in the production ORDER and asserts the batch arrives at
    /// the provider's batched entry point. It is the one that catches a NEW decorator forgetting
    /// the forward, which is the defect this whole change is about;
    /// <c>StorageAdapterDecoratorsForwardBatchReadGuard</c> is the same rule enforced statically
    /// over every decorator in the assembly, so a decorator nobody wires into this test is still
    /// held to it.</para>
    /// </summary>
    [Fact]
    public async Task TheProductionDecoratorChain_StillReachesTheBatchedEntryPoint()
    {
        var store = new RecordingAdapter([Node(Shared, "s"), Node(OnlyInA, "a"), Node(OnlyInB, "b")]);

        // The exact composition DecorateStorageAdapterWithVersionWriting builds, in its order.
        IStorageAdapter production = new SubtreeDeletionGuardStorageAdapter(
            new MonotonicWriteGuardStorageAdapter(
                new VersionWritingStorageAdapter(
                    new PersistenceService([new Provider("A", store)]),
                    versionQuery: null)),
            registry: null);

        var read = await production
            .ReadMany([Shared, OnlyInA, OnlyInB], Options)
            .ToList()
            .Timeout(TestTimeouts.Quick)
            .Await(TestContext.Current.CancellationToken);

        read.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal).Should().Equal(
            [OnlyInA, OnlyInB, Shared],
            "the chain must still return every node the batch asked for");
        store.BatchCalls.Should().Be(1,
            "EVERY layer of the production chain must forward the batch — one that does not sends "
            + "dispatch to the interface default inside the chain, and the facade's override below "
            + "it is then unreachable however correct it is (#4200)");
        store.SingleReads.Should().Be(0,
            "a decorator that silently fans a batch back into point reads is the whole defect, and "
            + "it is invisible from outside: the results are identical, only the failure modes and "
            + "the round-trips differ");
    }

    /// <summary>
    /// 🚨 A provider whose BATCH faults must fault the read — never be read as "these nodes are
    /// absent".
    ///
    /// <para>The double here is the 42P01 shape verbatim: its single-path <c>Read</c> answers
    /// <c>null</c> (what <c>PostgreSqlStorageAdapter.Read</c> does when it catches
    /// <c>UndefinedTable</c>) while its <c>ReadMany</c> faults (what the batched path does with the
    /// same condition). So on the degraded default this test does not merely fail to batch — it
    /// completes EMPTY and reports three healthy nodes missing, which is the #4200 signature.
    /// Forwarding the batch is what turns that into a fault the caller can classify.</para>
    /// </summary>
    [Fact]
    public async Task AProviderWhoseBatchFaults_FaultsTheRead_RatherThanReportingItsNodesAbsent()
    {
        var store = new RecordingAdapter(
            [Node(Shared, "s"), Node(OnlyInA, "a"), Node(OnlyInB, "b")],
            batchFault: new InvalidOperationException(
                "42P01: relation \"pkg.mesh_nodes_code\" does not exist"));
        IStorageAdapter persistence = new PersistenceService([new Provider("A", store)]);

        Func<Task> act = () => persistence
            .ReadMany([Shared, OnlyInA, OnlyInB], Options)
            .ToList()
            .Timeout(TestTimeouts.Quick)
            .Await(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "a store that could not answer is NOT a store that answered 'absent' — swallowing "
                + "the fault is what turns a half-provisioned partition into an ABSENT name for a "
                + "healthy node instead of an honest NotObserved (#4200)"))
            .WithMessage("*42P01*");
    }

    // ── Doubles ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A hand-written storage adapter over a fixed path→node map that RECORDS which entry point the
    /// facade used. Not a mock of a core interface: a provider-side <see cref="IStorageAdapter"/> is
    /// how <see cref="PersistenceService"/> has always been tested here (see
    /// <c>StorageAdapterWriteManyTests</c>, <c>AcknowledgedWriteIsReadableTest</c>) — the subject is
    /// the facade's fan-out, and a real backend cannot say which of its own methods was called.
    /// </summary>
    private sealed class RecordingAdapter : IStorageAdapter
    {
        private readonly ImmutableDictionary<string, MeshNode> _nodes;
        private readonly Exception? _batchFault;
        private int _singleReads;
        private int _batchCalls;
        private ImmutableList<ImmutableList<string>> _batches = ImmutableList<ImmutableList<string>>.Empty;

        public RecordingAdapter(IEnumerable<MeshNode> nodes, Exception? batchFault = null)
        {
            _nodes = nodes.ToImmutableDictionary(n => n.Path, n => n, StringComparer.OrdinalIgnoreCase);
            _batchFault = batchFault;
        }

        /// <summary>How many times the SINGLE-path entry point was used.</summary>
        public int SingleReads => Volatile.Read(ref _singleReads);

        /// <summary>How many times the BATCHED entry point was used.</summary>
        public int BatchCalls => Volatile.Read(ref _batchCalls);

        /// <summary>The path set of each batch, in call order, each sorted so the assertion is
        /// about membership rather than about an order ReadMany never promised.</summary>
        public ImmutableList<ImmutableList<string>> Batches => Volatile.Read(ref _batches);

        /// <summary>
        /// The 42P01 shape when <c>batchFault</c> is set: a missing satellite table is CAUGHT here
        /// and answered as <c>null</c>, which is indistinguishable from "no such node".
        /// </summary>
        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => Observable.Defer(() =>
            {
                Interlocked.Increment(ref _singleReads);
                return Observable.Return<MeshNode?>(
                    _batchFault is null ? _nodes.GetValueOrDefault(path) : null);
            });

        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => Observable.Defer(() =>
            {
                Interlocked.Increment(ref _batchCalls);
                ImmutableInterlocked.Update(
                    ref _batches,
                    (recorded, batch) => recorded.Add(batch),
                    paths.OrderBy(p => p, StringComparer.Ordinal).ToImmutableList());
                return _batchFault is not null
                    ? Observable.Throw<MeshNode>(_batchFault)
                    : paths
                        .Select(p => _nodes.GetValueOrDefault(p))
                        .Where(n => n is not null)
                        .Select(n => n!)
                        .ToObservable();
            });

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => Observable.Return<MeshNode?>(null);

        public IObservable<string> Delete(string path) => Observable.Return(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(
            string? parentPath)
            => Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []));

        public IObservable<bool> Exists(string path) => Observable.Return(_nodes.ContainsKey(path));

        public IObservable<object> GetPartitionObjects(
            string nodePath, string? subPath, JsonSerializerOptions options)
            => Observable.Empty<object>();

        public IObservable<System.Reactive.Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => Observable.Return(System.Reactive.Unit.Default);

        public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => Observable.Return(System.Reactive.Unit.Default);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => Observable.Return<DateTimeOffset?>(null);
    }

    /// <summary>A writable wildcard provider — no fixed namespace and the default Priority, so the
    /// providers keep registration order in <see cref="PersistenceService"/>'s chain.</summary>
    private sealed class Provider(string name, IStorageAdapter adapter) : IPartitionStorageProvider
    {
        public string Name => name;
        public bool IsReadOnly => false;
        public IStorageAdapter Adapter => adapter;
    }
}
