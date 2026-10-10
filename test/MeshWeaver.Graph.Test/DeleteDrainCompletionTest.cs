using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A real <see cref="IStorageAdapter"/> over a real <see cref="InMemoryStorageAdapter"/> that can
/// (a) give DELETES a latency, and (b) write one node straight into the backing store the first
/// time a delete lands under a watched root.
///
/// <para>Neither is a mock of a core interface — the store of record IS an
/// <c>InMemoryStorageAdapter</c> and every call reaches it. It reproduces the two things a live
/// mesh does that a fast, quiet in-memory suite never does: a backend whose deletes cost real time,
/// and a writer that creates something under the subtree BETWEEN the delete's plan snapshot and its
/// drain. The second is exactly what makes the drain remove MORE paths than it planned — the
/// "162 of 161 planned path(s)" in issue #3392.</para>
/// </summary>
internal sealed class LatentDeleteStorageAdapter(InMemoryStorageAdapter inner) : IStorageAdapter
{
    /// <summary>Backing store of record — tests assert against this, never against a cache.</summary>
    public InMemoryStorageAdapter Inner => inner;

    /// <summary>Paths at or under this prefix pay <see cref="DeleteLatency"/> on every delete.</summary>
    public string? LatencyRoot { get; set; }

    /// <summary>Simulated per-delete backend latency. Applies only under <see cref="LatencyRoot"/>.</summary>
    public TimeSpan DeleteLatency { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Written straight into the backing store the first time a delete lands under
    /// <see cref="LatencyRoot"/> — a mid-flight creation from "another process", after the delete's
    /// plan was already snapshotted.
    /// </summary>
    public MeshNode? InjectOnFirstDelete { get; set; }

    /// <summary>
    /// After this many deletes under <see cref="LatencyRoot"/> have been SERVED, every further
    /// delete under it never answers — the store took the call and went silent. That is the
    /// production shape behind the commit stage's no-progress watchdog: a drain that removed some
    /// rows and then stopped removing any (issue #1198). <c>null</c> ⇒ never stall, which is what
    /// every other test in this file relies on.
    /// </summary>
    public int? StallAfterDeletes { get; set; }

    /// <summary>
    /// When true, deletes under <see cref="LatencyRoot"/> are served ONE AT A TIME, each taking
    /// <see cref="DeleteLatency"/> — the cap-1 <c>pg:{provider}</c> write pool every production
    /// commit ends on (issue #6351). A delete that arrives while others are queued waits for all of
    /// them, so a burst of N simultaneous leaf commits makes the last one wait N × latency.
    /// </summary>
    public bool SerializeDeletes { get; set; }

    private long _laneFreeAtTicks;

    private int _deletesServed;

    private int _injected;

    /// <summary>
    /// When set, every delete under <see cref="LatencyRoot"/> is admitted through THIS pool before it reaches the
    /// store - the way a production leaf removal is admitted through the cap-1 pg: write pool (#1198).
    /// </summary>
    public IIoPool? DeleteLane { get; set; }

    private IObservable<T> WithLane<T>(IObservable<T> served)
        => DeleteLane is { } lane ? lane.InvokeObservable(ct => served) : served;

    private bool UnderLatencyRoot(string path)
        => LatencyRoot is { Length: > 0 } root
           && (path.Equals(root, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));

    private IObservable<T> Slow<T>(string path, IObservable<T> operation)
    {
        if (!UnderLatencyRoot(path))
            return operation;

        return Observable.Defer(() =>
        {
            // The store accepted the call and never answers. Never a Task.Delay and never a long
            // timer: silence IS the subject, so the watchdog's own budget is what ends the wait.
            if (StallAfterDeletes is { } stallAfter
                && Interlocked.Increment(ref _deletesServed) > stallAfter)
                return Observable.Never<T>();

            if (InjectOnFirstDelete is { } node && Interlocked.Exchange(ref _injected, 1) == 0)
                // Straight into the store of record: the guard decorators above this adapter refuse
                // in-process writes under a subtree being deleted, and a CROSS-process writer is the
                // case the drain loop exists for.
                inner.Write(node, new JsonSerializerOptions()).Subscribe();

            // Rx, never Task.Delay: this is the SUBJECT's latency, not the test waiting for
            // propagation. It is what makes a drain that is removing rows steadily still take
            // longer than the operation budget.
            if (SerializeDeletes && DeleteLatency > TimeSpan.Zero)
            {
                // Reserve the next slot on a single virtual lane: this delete starts when the one
                // ahead of it finishes. A lock-free reservation, not a gate — nothing waits on it;
                // the Timer below is the subject's latency.
                var now = DateTime.UtcNow.Ticks;
                long reservedEnd;
                long seen;
                do
                {
                    seen = Interlocked.Read(ref _laneFreeAtTicks);
                    reservedEnd = Math.Max(now, seen) + DeleteLatency.Ticks;
                }
                while (Interlocked.CompareExchange(ref _laneFreeAtTicks, reservedEnd, seen) != seen);
                return Observable.Timer(TimeSpan.FromTicks(reservedEnd - now)).SelectMany(_ => operation);
            }

            return WithLane(DeleteLatency > TimeSpan.Zero
                ? Observable.Timer(DeleteLatency).SelectMany(_ => operation)
                : operation);
        });
    }

    /// <inheritdoc />
    public IObservable<string> Delete(string path) => Slow(path, inner.Delete(path));

    /// <inheritdoc />
    public IObservable<bool> DeleteIfExists(string path) => Slow(path, inner.DeleteIfExists(path));

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => inner.Read(path, options);

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => inner.Write(node, options);

    /// <inheritdoc />
    public IObservable<bool?> WriteIfVersion(
        MeshNode node, long expectedVersion, JsonSerializerOptions options)
        => inner.WriteIfVersion(node, expectedVersion, options);

    /// <inheritdoc />
    public IObservable<bool> Exists(string path) => inner.Exists(path);

    /// <inheritdoc />
    public IObservable<bool> ExistsInWritableStorage(string path)
        => ((IStorageAdapter)inner).ExistsInWritableStorage(path);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => inner.FindBestPrefixMatch(fullPath, options);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => inner.ResolvePath(fullPath, options);

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => inner.ListChildPaths(parentPath);

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => inner.ListDescendantPaths(rootPath);

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => ((IStorageAdapter)inner).ListPartitionSubPaths(nodePath);

    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => inner.GetPartitionObjects(nodePath, subPath, options);

    /// <inheritdoc />
    public IObservable<System.Reactive.Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects,
        JsonSerializerOptions options)
        => inner.SavePartitionObjects(nodePath, subPath, objects, options);

    /// <inheritdoc />
    public IObservable<System.Reactive.Unit> DeletePartitionObjects(
        string nodePath, string? subPath = null)
        => inner.DeletePartitionObjects(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(
        string nodePath, string? subPath = null)
        => inner.GetPartitionMaxTimestamp(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DataChangeNotification> Changes => inner.Changes;
}

/// <summary>
/// SYMPTOM A of issue #3392 — a COMPLETED delete reported as a failure.
///
/// <para>The commit stage bounds <c>DeleteSubtreeUntilDrained</c> with an inter-emission
/// <c>Observable.Timeout</c>, but that observable emits exactly ONCE, at the very end of a
/// multi-pass drain. An inter-emission bound over a single-emission source is a TOTAL-DURATION cap:
/// a delete that was removing rows steadily was killed at the budget purely for taking the budget,
/// and the message then compared its removals against the pre-drain plan SNAPSHOT — producing the
/// report that named the defect, <c>did not drain within 30s — 162 of 161 planned path(s) were
/// already removed from storage</c>. Removing MORE than planned is normal (anything created between
/// the snapshot and the drain), and it is precisely the state in which the work is most certainly
/// done.</para>
///
/// <para>This suite reproduces both halves at test scale: a chain deep enough that the bottom-up
/// fan-out is strictly sequential, a per-delete backend latency that pushes the total past the
/// 5 s operation budget, and one node created mid-flight so the drain removes plan + 1. The bound
/// is now a NO-PROGRESS watchdog — every removal resets it — so the delete succeeds, and the
/// injected node is gone too.</para>
/// </summary>
public class DeleteDrainOutlivesTheBudgetWhileProgressingTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RootId = "drain-budget";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    // Deep enough that the bottom-up walk cannot parallelise the latency away: HierarchicalPathDeletion
    // merges SIBLINGS, so only DEPTH serialises. 14 × 500 ms ≈ 7 s > the 5 s budget.
    private const int Depth = 14;
    private static readonly TimeSpan PerDeleteLatency = TimeSpan.FromMilliseconds(500);

    private readonly LatentDeleteStorageAdapter _storage = new(new InMemoryStorageAdapter());

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            // Registered BEFORE the bootstrap's TryAddSingleton, so this IS the store of record and
            // the version-writing / monotonic-guard decorators wrap it exactly as in production.
            services.AddSingleton<IStorageAdapter>(_storage);
            // A 5 s operation budget keeps the test quick; the ladder derives every nested rung
            // from it (2.5 s per cascade leaf), which is ample for a single 500 ms delete.
            services.AddSingleton(new MeshOperationOptions { Timeout = Budget });
            return services;
        }));

    [Fact]
    public async Task DeleteThatRemovesMoreThanItPlanned_Succeeds_EvenWhenItOutlivesTheBudget()
    {
        var rootPath = $"{TestPartition}/{RootId}";
        await NodeFactory.CreateNode(
                new MeshNode(RootId, TestPartition) { Name = "Drain root", NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit();

        var parent = rootPath;
        for (var i = 0; i < Depth; i++)
        {
            var id = $"l{i}";
            await NodeFactory.CreateNode(
                    new MeshNode(id, parent) { Name = id, NodeType = "Markdown" })
                .Should().Within(30.Seconds()).Emit();
            parent = $"{parent}/{id}";
        }

        // The mid-flight creation: a sibling of the deepest leaf, written into the store of record
        // the moment the first delete lands — i.e. AFTER the plan snapshot. The drain must notice it,
        // remove it in a follow-up pass, and still report success.
        var injectedPath = $"{parent}/injected-mid-flight";
        _storage.InjectOnFirstDelete =
            new MeshNode("injected-mid-flight", parent) { Name = "mid-flight", NodeType = "Markdown" };
        _storage.LatencyRoot = rootPath;
        _storage.DeleteLatency = PerDeleteLatency;

        var startedAt = DateTime.UtcNow;
        var deleted = await NodeFactory.DeleteNode(rootPath).Should().Within(60.Seconds()).Emit(
            "a drain that keeps removing rows must never be failed for taking longer than the budget");
        var elapsed = DateTime.UtcNow - startedAt;

        deleted.Should().BeTrue();
        elapsed.Should().BeGreaterThan(Budget,
            "the test only discriminates if the delete really outlived the operation budget — "
            + $"it took {elapsed.TotalSeconds:0.0}s against a {Budget.TotalSeconds:0}s budget");

        // Success now MEANS drained: the plan's 15 paths and the one that appeared mid-flight.
        (await _storage.Inner.Exists(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeFalse("the root must be gone");
        (await _storage.Inner.Exists(injectedPath).Should().Within(10.Seconds()).Emit())
            .Should().BeFalse(
                "the node created mid-flight is what made the removals exceed the plan; the drain "
                + "must have removed it too");
    }
}

/// <summary>
/// SYMPTOM B of issue #3392 — an INCOMPLETE delete reported as success.
///
/// <para>The drain verified its work with <c>IStorageAdapter.ListDescendantPaths</c>, which is
/// STRICT descendants by contract, and then removed the root from the survivor set a second time.
/// So the ROOT — the one path the whole operation is for — was never checked at all: a root row
/// that survived the cascade (re-created mid-flight, or a writable provider that accepted the
/// delete without removing anything) left the subtree with exactly ONE node in it and the delete
/// still answered success. That is the shape the issue measured: seven Spaces logged <c>deleted</c>
/// and found with one node left.</para>
///
/// <para>The stand-in for "the root survived" is a cross-process writer that puts the root row back
/// the instant the cascade removes it — the same race <c>DeleteSubtreeUntilDrained</c> already
/// handles for descendants, applied to the path it could not see.</para>
/// </summary>
public class DeleteDrainVerifiesTheRootTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// One-shot resurrection: the drain must NOTICE the root came back, remove it in a follow-up
    /// pass, and only then report success. Success has to mean the node is gone.
    /// </summary>
    [Fact]
    public async Task RootResurrectedOnce_IsDrainedByAFollowUpPass_AndSuccessMeansItIsGone()
    {
        var (raw, rootPath, node) = await SeedTree("root-resurrect-once");

        var recreated = 0;
        using var writer = raw.Changes
            .Where(c => c.Kind == DataChangeKind.Deleted
                        && string.Equals(c.Path, rootPath, StringComparison.OrdinalIgnoreCase))
            .Subscribe(_ =>
            {
                if (Interlocked.Exchange(ref recreated, 1) == 0)
                    raw.Write(node, new JsonSerializerOptions()).Subscribe();
            });

        (await NodeFactory.DeleteNode(rootPath).Should().Within(60.Seconds()).Emit())
            .Should().BeTrue();

        recreated.Should().Be(1, "the test only discriminates if the root really was put back");
        (await raw.Exists(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeFalse(
                "a delete that reports success must leave NOTHING behind — the root included");
    }

    /// <summary>
    /// The unrecoverable half: a root that comes back on every pass. The drain must exhaust its
    /// pass budget and FAIL LOUDLY naming the root, never report success over a live node.
    /// </summary>
    [Fact]
    public async Task RootThatSurvivesEveryPass_FailsLoudly_InsteadOfReportingSuccess()
    {
        var (raw, rootPath, node) = await SeedTree("root-resurrect-always");

        using var writer = raw.Changes
            .Where(c => c.Kind == DataChangeKind.Deleted
                        && string.Equals(c.Path, rootPath, StringComparison.OrdinalIgnoreCase))
            .Subscribe(_ => raw.Write(node, new JsonSerializerOptions()).Subscribe());

        // Producer -> test signal: the delete's ERROR arm completes an AsyncSubject the assertion
        // helpers await. A success emission would leave the subject empty and time the wait out,
        // which is exactly the failure this test must report.
        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(rootPath).Subscribe(
            _ => { },
            ex =>
            {
                failure.OnNext(ex);
                failure.OnCompleted();
            });

        var reported = await failure.Should().Within(60.Seconds()).Emit(
            "a subtree that cannot be drained must FAIL — never answer success over a live node");
        reported.Message.Should().Contain("could not drain");
        reported.Message.Should().Contain("root node itself is still present",
            "the caller has to be told WHICH node survived, and the root is the one the old "
            + "predicate could not see");

        (await raw.Exists(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeTrue("the writer keeps putting it back — the point is that the caller is TOLD");
    }

    private async Task<(InMemoryStorageAdapter Raw, string RootPath, MeshNode Node)> SeedTree(string id)
    {
        var rootPath = $"{TestPartition}/{id}";
        var created = await NodeFactory.CreateNode(
                new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNode(
                new MeshNode("child", rootPath) { Name = "child", NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var raw = Mesh.ServiceProvider.GetRawStorageAdapter<InMemoryStorageAdapter>()!;
        return (raw, rootPath, created);
    }
}

/// <summary>
/// Issue #6351 — a WIDE recursive delete whose leaf commits all end on ONE serialised write lane.
///
/// <para>Production shape (control portal, 2026-10-09 13:38:58Z): the 1,383-path <c>Marketing</c>
/// delete failed with <c>[DeleteNode:commit] the bottom-up delete of 'Marketing/_Activity/dacd7d46'
/// made no progress for 25s — 0 of 1 planned path(s) removed … pg:Postgres(cap 1) 219 waiting</c>.
/// The cascade posted every leaf at once, every leaf's commit queued on the cap-1 write pool, and a
/// leaf at the back of that queue tripped its OWN no-progress watchdog while the cascade was
/// removing rows steadily. The backlog was the operation's own.</para>
///
/// <para>Here: a root with <see cref="Leaves"/> direct children, each delete served one at a time
/// at <see cref="PerDeleteLatency"/>, so the whole lane takes about 8 s against a 2.5 s leaf
/// budget. With the cascade's fan-out bounded the delete succeeds; the negative control
/// (<see cref="WideDeleteUnboundedFanOutTest"/>) runs the same tree unbounded and reproduces the
/// production failure.</para>
/// </summary>
public abstract class WideDeleteOnASerialisedWriteLaneTestBase(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>Direct children of the deleted root — the wide level that floods the lane.</summary>
    protected const int Leaves = 160;

    /// <summary>One pooled write; 160 of them serialised take 8 s, past the 2.5 s leaf budget.</summary>
    protected static readonly TimeSpan PerDeleteLatency = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>The storage adapter, exposed so a test can assert against the store of record.</summary>
    private protected readonly LatentDeleteStorageAdapter Storage = new(new InMemoryStorageAdapter());

    /// <summary>The commit fan-out bound this run is configured with.</summary>
    protected abstract int FanOutConcurrency { get; }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStorageAdapter>(Storage);
            services.AddSingleton(new MeshOperationOptions
            {
                Timeout = Budget,
                CascadeFanOutConcurrency = FanOutConcurrency
            });
            return services;
        }));

    /// <summary>Seeds the wide tree and arms the serialised lane under it.</summary>
    protected async Task<string> SeedWideTree(string rootId)
    {
        var rootPath = $"{TestPartition}/{rootId}";
        await NodeFactory.CreateNode(
                new MeshNode(rootId, TestPartition) { Name = rootId, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit();
        var options = new JsonSerializerOptions();
        // Straight into the store of record: the plan is enumerated from storage and every leaf's
        // own hub reads its node from there, so the leaves need no create round-trip each.
        await Enumerable.Range(0, Leaves)
            .Select(i => Storage.Inner.Write(
                new MeshNode($"leaf{i:D3}", rootPath) { Name = $"leaf{i:D3}", NodeType = "Markdown" },
                options))
            .Merge()
            .ToList()
            .Should().Within(TestTimeouts.Convergence).Emit();
        Storage.LatencyRoot = rootPath;
        Storage.DeleteLatency = PerDeleteLatency;
        Storage.SerializeDeletes = true;
        return rootPath;
    }
}

/// <summary>The fix: the commit keeps at most 8 leaves in flight, so no leaf waits past its budget.</summary>
public class WideDeleteBoundedFanOutTest(ITestOutputHelper output)
    : WideDeleteOnASerialisedWriteLaneTestBase(output)
{
    /// <inheritdoc />
    protected override int FanOutConcurrency => 8;

    [Fact]
    public async Task AWideDelete_OnASerialisedWriteLane_Succeeds_AndRemovesEveryLeaf()
    {
        var rootPath = await SeedWideTree("wide-bounded");

        var deleted = await NodeFactory.DeleteNode(rootPath).Should().Within(90.Seconds()).Emit(
            "a cascade that keeps removing rows must not be failed by a leaf queued behind its own "
            + "siblings' writes");
        deleted.Should().BeTrue();

        (await Storage.Inner.ListDescendantPaths(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeEmpty("every leaf must be gone");
        (await Storage.Inner.Exists(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeFalse("the root must be gone");
    }
}

/// <summary>
/// Negative control: the same tree with the commit fan-out unbounded — the shape before #6351's fix.
/// Every leaf's write lands on the lane at once and the leaves at the back fail their own watchdog,
/// which is the production failure.
/// </summary>
public class WideDeleteUnboundedFanOutTest(ITestOutputHelper output)
    : WideDeleteOnASerialisedWriteLaneTestBase(output)
{
    /// <inheritdoc />
    protected override int FanOutConcurrency => int.MaxValue;

    [Fact]
    public async Task AWideDelete_Unbounded_FailsALeafOnItsOwnSiblingsBacklog()
    {
        var rootPath = await SeedWideTree("wide-unbounded");

        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(rootPath).Subscribe(
            _ => { },
            ex =>
            {
                failure.OnNext(ex);
                failure.OnCompleted();
            });

        var reported = await failure.Should().Within(90.Seconds()).Emit(
            "unbounded, the leaves queue behind each other on the one lane and one of them times out");
        reported.Message.Should().Contain("made no progress",
            "the failure must be the leaf's own commit watchdog — the production line");

        // The leaf's verdict was "unavailable" (its commit ran out of time). Production surfaced it
        // as `UnauthorizedAccessException: Unexpected error: Delete failed for …` because the root
        // read the cascade wrapper's TYPE as the reason (ValidationFailed → access denied).
        (reported is UnauthorizedAccessException).Should().BeFalse(
            $"an availability failure must not reach the caller as a permission denial, got {reported.GetType().Name}");
        reported.Message.Should().NotContain("Unexpected error",
            "the leaf's refusal is classified — the caller is told what it was");
    }
}

/// <summary>
/// The CALLER's half of issue #3392: a recursive delete that keeps removing rows must not lose its
/// reply because it outlived the caller's request deadline.
///
/// <para>Production shape (memex, governed request
/// <c>rbuergi/Requests/crm-5219-prune-retired-client-type</c>): the delete of the retired NodeType
/// <c>Crm/Client</c> — several hundred <c>_Activity/compile-*</c> satellites under it — was still
/// removing leaves when its caller's 60 s <c>RequestTimeout</c> expired. The caller failed with
/// <c>No response received … DeleteNodeRequest → portal/nodeops-…</c>; the delete then finished, and
/// <c>Crm/Client</c> was gone. #3392 had already turned the SERVER's commit bound into a no-progress
/// watchdog; the caller's deadline was still a total-duration cap, so the two disagreed about what
/// "too long" means and the reply of every delete larger than the cap reached nobody.</para>
///
/// <para>Here the caller's deadline is 3 s and the delete takes about 8 s on a serialised write
/// lane (160 leaves × 50 ms), removing a leaf every 50 ms. The handler reports its progress to the
/// caller (<c>RequestProgress</c>), the deadline measures silence rather than duration, and
/// the reply arrives. The negative control stalls the store mid-delete: the caller's deadline still
/// fires, a few seconds after the last removal, so silence is still refused.</para>
/// </summary>
public class LongDeleteKeepsItsCallerTest(ITestOutputHelper output)
    : WideDeleteOnASerialisedWriteLaneTestBase(output)
{
    /// <summary>The caller's deadline, shorter than every server-side bound of the negative control.</summary>
    internal static readonly TimeSpan CallerDeadline = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    protected override int FanOutConcurrency => 8;

    private IMessageHub ShortDeadlineClient()
        => GetClient(c => ConfigureClient(c).WithRequestTimeout(CallerDeadline));

    /// <summary>A recursive delete posted from <paramref name="client"/> to the node-operation hub.</summary>
    internal static IObservable<IMessageDelivery<DeleteNodeResponse>> Delete(IMessageHub client, string path)
        => client.Observe(
            new DeleteNodeRequest(path) { Recursive = true, ConfirmWarnings = true },
            o => o.WithTarget(client.NodeOperationTarget()));

    [Fact]
    public async Task ADeleteThatOutlivesTheCallersDeadline_WhileProgressing_StillAnswersTheCaller()
    {
        var rootPath = await SeedWideTree("outlives-caller");
        var client = ShortDeadlineClient();

        var startedAt = DateTime.UtcNow;
        var reply = await Delete(client, rootPath).Should().Within(90.Seconds()).Emit(
            "a delete that keeps removing rows must reach its caller however long it takes");
        var elapsed = DateTime.UtcNow - startedAt;

        reply.Message.Success.Should().BeTrue(reply.Message.Error ?? "the delete must succeed");
        elapsed.Should().BeGreaterThan(CallerDeadline,
            "the test only discriminates if the delete really outlived the caller's deadline — "
            + $"it took {elapsed.TotalSeconds:0.0}s against {CallerDeadline.TotalSeconds:0}s");
        (await Storage.Inner.ListDescendantPaths(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeEmpty("every leaf must be gone");
        (await Storage.Inner.Exists(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeFalse("the root must be gone");
    }

}

/// <summary>
/// Negative control for <see cref="LongDeleteKeepsItsCallerTest"/>: progress keeps a caller's
/// deadline open only while there IS progress. The store stalls mid-delete and the server-side bounds
/// are far longer than the caller's (30 s operation budget, 25 s per cascade leaf), so the only thing
/// that can end the wait within seconds is the caller's own deadline — and it must, counted from the
/// last progress report.
/// </summary>
public class StalledDeleteStillTimesItsCallerOutTest(ITestOutputHelper output)
    : WideDeleteOnASerialisedWriteLaneTestBase(output)
{
    /// <inheritdoc />
    protected override int FanOutConcurrency => 8;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services =>
            // Registered after the base's options, so this is the one resolved. Derived from the
            // caller's deadline, so the ordering the test depends on cannot drift.
            services.AddSingleton(new MeshOperationOptions
            {
                Timeout = LongDeleteKeepsItsCallerTest.CallerDeadline * 10,
                CascadeFanOutConcurrency = FanOutConcurrency
            }));

    [Fact]
    public async Task ADeleteThatStopsProgressing_StillTimesTheCallerOut()
    {
        var rootPath = await SeedWideTree("stalls-caller");
        // The store takes the 41st delete and never answers: no removal, so no progress, after it.
        Storage.StallAfterDeletes = 40;
        var client = GetClient(c => ConfigureClient(c)
            .WithRequestTimeout(LongDeleteKeepsItsCallerTest.CallerDeadline));

        // Either terminal is recorded, so a server reply that beats the caller's deadline fails the
        // assertion below by name instead of leaving the wait to run out.
        var outcome = new AsyncSubject<object>();
        using var deleting = LongDeleteKeepsItsCallerTest.Delete(client, rootPath).Subscribe(
            reply =>
            {
                outcome.OnNext(reply.Message);
                outcome.OnCompleted();
            },
            ex =>
            {
                outcome.OnNext(ex);
                outcome.OnCompleted();
            });

        var reported = await outcome.Should().Within(20.Seconds()).Emit(
            "progress keeps the caller's deadline open only while there IS progress — silence must "
            + "still be refused, and the server's own bounds are 25 s and more");
        reported.Should().BeOfType<TimeoutException>(
            "the caller's own deadline is what ends the wait once removals stop");
        ((TimeoutException)reported).Message.Should().Contain("No response received");
    }
}
