using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A whole partition is torn down as ONE operation, whatever its size</b> —
/// <see cref="PartitionTeardown.TearDownPartition"/>.
///
/// <para><b>The incident.</b> A governed <c>DeleteSpace</c> on the control instance ran eight steps
/// as system and then stalled in step 9: the recursive <c>DeleteNodeRequest</c> of a 31,138-descendant
/// space pre-validates every descendant with a <c>ValidateDeleteRequest</c>, and the fan-out did not
/// settle within its 25 s bound (64 of 1,740 posted requests outstanding). A whole-space deletion has
/// no per-node invariant worth validating 31,000 times — the space and all its access go together —
/// so the content goes with the partition's store in one drop.</para>
///
/// <para>The store stand-in is the shape <c>StrandedPartitionRecordTeardownTest</c> uses, with one
/// addition: its drop REMOVES the partition's rows from the in-memory store the way
/// <c>DROP SCHEMA … CASCADE</c> removes them from Postgres — or, switched to "cannot tell", does
/// nothing, which is the backend with no per-partition store that the teardown must sweep itself.</para>
/// </summary>
public class PartitionTeardownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>More descendants than the per-node delete could ever pre-validate in its bound.</summary>
    private const int Descendants = 3000;

    private sealed class PartitionStore(IStorageAdapter adapter) : IPartitionStorageProvider
    {
        private readonly ConcurrentDictionary<string, byte> provisioned = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> events = new();

        public string Name => "test-teardown-store";
        public bool IsReadOnly => true;
        public IStorageAdapter Adapter => adapter;
        public IReadOnlyList<string> Events => events.ToArray();

        /// <summary>When false the provider answers "cannot tell" and its drop does nothing — a backend with no per-partition store.</summary>
        public bool HoldsStores { get; set; } = true;

        public string? FaultDropFor { get; set; }

        /// <summary>Observes each drop as it starts — a probe of what holds while the store is dropped.</summary>
        public Action<string>? OnDrop { get; set; }

        public bool IsProvisioned(string ns) => provisioned.ContainsKey(ns);

        public IObservable<System.Reactive.Unit> EnsurePartitionProvisioned(string ns) => Observable.Defer(() =>
        {
            provisioned[ns] = 1;
            return Observable.Return(System.Reactive.Unit.Default);
        });

        public IObservable<bool?> PartitionExists(string ns) =>
            Observable.Defer(() => Observable.Return<bool?>(HoldsStores ? provisioned.ContainsKey(ns) : null));

        public IObservable<System.Reactive.Unit> DeletePartition(string ns) => Observable.Defer(() =>
        {
            OnDrop?.Invoke(ns);
            if (string.Equals(ns, FaultDropFor, StringComparison.OrdinalIgnoreCase))
            {
                events.Enqueue($"drop-faulted:{ns}");
                return Observable.Throw<System.Reactive.Unit>(new InvalidOperationException("the store is unreachable"));
            }
            events.Enqueue($"drop:{ns}");
            if (!HoldsStores)
                return Observable.Return(System.Reactive.Unit.Default);
            provisioned.TryRemove(ns, out _);
            // DROP SCHEMA … CASCADE: every row of the partition goes, satellites included.
            return adapter.ListDescendantPaths(ns).Take(1)
                .SelectMany(paths => adapter.DeleteMany(paths.Append(ns).ToList()).Take(1))
                .Select(_ => System.Reactive.Unit.Default);
        });
    }

    /// <summary>A shipped content partition: read-only, with a FIXED partition definition, its nodes children of it.</summary>
    private sealed class ShippedContent(IStorageAdapter adapter) : IPartitionStorageProvider
    {
        public const string Partition = "ShippedTeardownContent";
        public string Name => "test-shipped-content";
        public bool IsReadOnly => true;
        public IStorageAdapter Adapter => adapter;
        public PartitionDefinition? PartitionDefinition => new() { Namespace = Partition };
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton<IPartitionStorageProvider>(sp =>
                    new PartitionStore(sp.GetRequiredService<IStorageAdapter>()))
                .AddSingleton<IPartitionStorageProvider>(sp =>
                    new ShippedContent(sp.GetRequiredService<IStorageAdapter>())));

    private PartitionStore Store => Mesh.ServiceProvider.GetServices<IPartitionStorageProvider>().OfType<PartitionStore>().Single();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Persistence => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private static string NewPartition() => "teardown" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 🚨 THE INCIDENT'S SHAPE, at a size the per-node delete cannot pre-validate: a space with a root,
    /// thousands of descendants, a grant, a thread and a record, torn down as system — within ONE
    /// convergence bound, leaving no row, no record and no store.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task ALargeSpace_IsTornDownAsOneOperation_WithinOneBound()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = await Seed(ct);

        var clock = Stopwatch.StartNew();
        var outcome = await Access.RunAsSystem(() => Mesh.TearDownPartition(partition, "test: whole-space teardown"))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        clock.Stop();
        Output.WriteLine($"tore down {partition} ({Descendants} descendants) in {clock.ElapsedMilliseconds} ms: {outcome}");

        outcome.Partition.Should().Be(partition);
        outcome.RecordDeleted.Should().BeTrue("the partition's record is the last thing the teardown removes");
        Store.IsProvisioned(partition).Should().BeFalse("the store is dropped");
        Store.Events.Count(e => e == $"drop:{partition}").Should().Be(1,
            "ONE drop — the record delete inside the claim must stand down, not drop a second time");
        await AssertNothingLeft(partition, ct);
    }

    /// <summary>
    /// A backend with NO per-partition store (the in-memory store): the provider drop removes nothing,
    /// so the teardown removes the rows itself, below the pipeline — still one operation, no per-node
    /// validation, satellites included.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task OnABackendWithNoPerPartitionStore_TheRowsAreSweptBelowThePipeline()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = await Seed(ct);
        Store.HoldsStores = false;
        try
        {
            await Access.RunAsSystem(() => Mesh.TearDownPartition(partition, "test: sweep"))
                .Timeout(TestTimeouts.Convergence).Await(ct);
            await AssertNothingLeft(partition, ct);
        }
        finally
        {
            Store.HoldsStores = true;
        }
    }

    /// <summary>🚨 Not a user verb: without the system identity it refuses by name and touches nothing.</summary>
    [Fact(Timeout = 240000)]
    public async Task WithoutTheSystemIdentity_ItRefuses_AndTouchesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = await Seed(ct, descendants: 3);

        PartitionTeardown.Refusal(Mesh, partition).Should().Contain("system identity");
        var failure = await Mesh.TearDownPartition(partition, "test: no identity")
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .Timeout(TestTimeouts.Quick).Await(ct);
        failure.Should().NotBeNull("a teardown without the system identity must be refused");
        failure!.Message.Should().Contain("system identity");
        Store.IsProvisioned(partition).Should().BeTrue("nothing was touched");
        Store.Events.Should().NotContain($"drop:{partition}");
    }

    /// <summary>Invalid segments, the database-populated mirrors and shipped read-only content are refused by name, whatever the identity.</summary>
    [Fact(Timeout = 60000)]
    public void InvalidSegmentsMirrorsAndShippedContent_AreRefusedByName()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        PartitionTeardown.Refusal(Mesh, "a/b", requireSystem: false).Should().Contain("not a valid partition segment");
        PartitionTeardown.Refusal(Mesh, "_Access", requireSystem: false).Should().Contain("not a valid partition segment");
        PartitionTeardown.Refusal(Mesh, "Auth", requireSystem: false).Should().Contain("mirror");
        PartitionTeardown.Refusal(Mesh, ShippedContent.Partition, requireSystem: false).Should().Contain("read-only provider",
            "shipped content (Doc, …) is served by a read-only provider with a fixed definition — there is no store to drop, "
            + "and a teardown would only remove its record");
        PartitionTeardown.Refusal(Mesh, NewPartition(), requireSystem: false).Should().BeNull("an ordinary partition may be torn down");
    }

    /// <summary>
    /// 🚨 THE OUTCOME IS DELIVERED AFTER THE CLAIM IS RELEASED. The outcome is the caller's "torn down"
    /// signal, and a caller may act on it at once — the live <c>DeleteSpace</c> probe's cleanup issues a
    /// second, idempotent teardown of the same partition. Under <c>Observable.Using</c> the claim was
    /// released only after the subscriber had processed the outcome, so that follow-up was refused with
    /// "a deletion … is already in flight — one teardown at a time" (MeshWeaver.Plugins#2651, Gate shard
    /// 1/3, intermittent because the probe hops through an inventory read first). Here the follow-up is
    /// subscribed SYNCHRONOUSLY inside the outcome's delivery, which is the race with the hop removed:
    /// it fails every time if the claim is still held there.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task AFollowUpTeardownIssuedOnTheOutcome_IsNotRefusedAsInFlight()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = await Seed(ct, descendants: 3);
        var registry = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
        var heldDuringDrop = false;
        Store.OnDrop = ns => heldDuringDrop |= string.Equals(ns, partition, StringComparison.OrdinalIgnoreCase)
                                             && registry.IsUnderActiveDeletion(ns, out _);
        try
        {
            var result = await Access.RunAsSystem(() => Mesh.TearDownPartition(partition, "test: first teardown")
                    .Select(first => (first, inFlightAtDelivery: registry.IsUnderActiveDeletion(partition, out _)))
                    .SelectMany(r => Access.RunAsSystem(() => Mesh.TearDownPartition(partition, "test: follow-up on the outcome"))
                        .Select(second => (r.first, r.inFlightAtDelivery, second: (PartitionTeardownOutcome?)second, failure: (Exception?)null))
                        .Catch((Exception ex) => Observable.Return((r.first, r.inFlightAtDelivery, second: (PartitionTeardownOutcome?)null, failure: (Exception?)ex)))))
                .Timeout(TestTimeouts.Convergence).Await(ct);
            Output.WriteLine($"first={result.first} inFlightAtDelivery={result.inFlightAtDelivery} second={result.second} failure={result.failure?.Message}");

            heldDuringDrop.Should().BeTrue("CONTROL: the claim IS held while the store is dropped — the scope is armed");
            result.inFlightAtDelivery.Should().BeFalse("the claim is released before the outcome reaches the caller");
            result.failure.Should().BeNull("a follow-up teardown issued on the outcome is idempotent and must not be refused as in flight");
            var second = result.second;
            Assert.NotNull(second);
            second.RecordDeleted.Should().BeFalse("the follow-up finds the record already gone — it is a no-op, not a second teardown");
            await AssertNothingLeft(partition, ct);
        }
        finally
        {
            Store.OnDrop = null;
        }
    }

    /// <summary>
    /// A drop that faults keeps the record (the retry handle), lifts the tombstone (the partition is still
    /// there) and propagates the cause.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task AFailedDrop_KeepsTheRecord_LiftsTheTombstone_AndPropagates()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = await Seed(ct, descendants: 3);
        Store.FaultDropFor = partition;
        try
        {
            var registry = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
            var inFlightAtFailure = true;
            var failure = await Access.RunAsSystem(() => Mesh.TearDownPartition(partition, "test: faulted drop"))
                .Select(_ => (Exception?)null)
                .Catch((Exception ex) =>
                {
                    inFlightAtFailure = registry.IsUnderActiveDeletion(partition, out _);
                    return Observable.Return<Exception?>(ex);
                })
                .Timeout(TestTimeouts.Convergence).Await(ct);
            inFlightAtFailure.Should().BeFalse("the claim is released before the failure reaches the caller, so a retry on it is not refused");
            failure.Should().NotBeNull();
            failure!.Message.Should().Contain("unreachable");
            var record = await Persistence.Read($"{PartitionNodeType.Namespace}/{partition}", Mesh.JsonSerializerOptions)
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
            record.Should().NotBeNull("the record is the only handle a retry has");
            Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>().IsRecentlyDeleted(partition)
                .Should().BeFalse("the partition was not torn down, so no tombstone may stand");
        }
        finally
        {
            Store.FaultDropFor = null;
        }
    }

    // ——— helpers ———

    /// <summary>A provisioned partition with a record, a root, <paramref name="descendants"/> content rows,
    /// a grant and a thread — the content written below the pipeline, as a large space's rows sit in its store.</summary>
    private async Task<string> Seed(CancellationToken ct, int descendants = Descendants)
    {
        var partition = NewPartition();
        await Store.EnsurePartitionProvisioned(partition).Timeout(TestTimeouts.Quick).Await(ct);
        var response = await Access
            .RunAsSystem(() => ObserveNodeOperation(new CreateNodeRequest(new MeshNode(partition, PartitionNodeType.Namespace)
            {
                Name = partition,
                NodeType = PartitionNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new PartitionDefinition { Namespace = partition, Schema = partition.ToLowerInvariant() },
            })))
            .FirstAsync().Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        response.Success.Should().BeTrue($"the record create must succeed: {response.Error}");

        var rows = new List<MeshNode>
        {
            new(partition) { Name = "Large space", NodeType = SpaceNodeType.NodeType, State = MeshNodeState.Active, CreatedBy = "owner" },
            new("owner_Access", $"{partition}/_Access")
            {
                Name = "owner", NodeType = CreateNodesRequest.AccessAssignmentNodeType, State = MeshNodeState.Active,
                Content = new AccessAssignment { AccessObject = "owner", Roles = [new RoleAssignment { Role = "Admin" }] },
            },
            new("t1", $"{partition}/Section0/_Thread") { Name = "t1", NodeType = "Thread", State = MeshNodeState.Active },
        };
        for (var i = 0; i < descendants; i++)
            rows.Add(new MeshNode($"page{i}", $"{partition}/Section{i % 30}")
            {
                Name = $"page {i}", NodeType = "Markdown", State = MeshNodeState.Active,
            });
        await Access.RunAsSystem(() => Persistence.WriteMany(rows, Mesh.JsonSerializerOptions).Take(1))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var written = await Persistence.ListDescendantPaths(partition).Take(1).Timeout(TestTimeouts.Quick).Await(ct);
        written.Count.Should().BeGreaterThanOrEqualTo(descendants + 2,
            "CONTROL: the rows are in the store before the teardown, or its emptiness afterwards proves nothing");
        Output.WriteLine($"seeded '{partition}' with {written.Count} descendant row(s)");
        return partition;
    }

    private async Task AssertNothingLeft(string partition, CancellationToken ct)
    {
        var left = await Persistence.ListDescendantPaths(partition).Take(1).Timeout(TestTimeouts.Quick).Await(ct);
        left.Should().BeEmpty("no descendant row — content, grant or thread — may outlive the teardown");
        var root = await Persistence.Read(partition, Mesh.JsonSerializerOptions).Take(1).Timeout(TestTimeouts.Quick).Await(ct);
        root.Should().BeNull("the root goes with the partition");
        var record = await Persistence.Read($"{PartitionNodeType.Namespace}/{partition}", Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TestTimeouts.Quick).Await(ct);
        record.Should().BeNull("the record goes last");
    }
}
