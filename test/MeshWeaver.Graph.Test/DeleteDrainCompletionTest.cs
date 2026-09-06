using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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

    private int _injected;

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
            if (InjectOnFirstDelete is { } node && Interlocked.Exchange(ref _injected, 1) == 0)
                // Straight into the store of record: the guard decorators above this adapter refuse
                // in-process writes under a subtree being deleted, and a CROSS-process writer is the
                // case the drain loop exists for.
                inner.Write(node, new JsonSerializerOptions()).Subscribe();

            // Rx, never Task.Delay: this is the SUBJECT's latency, not the test waiting for
            // propagation. It is what makes a drain that is removing rows steadily still take
            // longer than the operation budget.
            return DeleteLatency > TimeSpan.Zero
                ? Observable.Timer(DeleteLatency).SelectMany(_ => operation)
                : operation;
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
            .Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(
                new MeshNode("child", rootPath) { Name = "child", NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit();

        var raw = Mesh.ServiceProvider.GetRawStorageAdapter<InMemoryStorageAdapter>()!;
        return (raw, rootPath, created);
    }
}
