using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <b>A node deleted and created again is read as the NEW node — a "not found" decided for the deleted
/// one never reaches a reader that asked after the recreate.</b>
///
/// <para>Seen as <c>InboxOpenApprovalsTest.APreferencesNodeDeletedAndRecreated_NeverEndsTheFeed</c>
/// timing out on core main: the inbox's background seed write touched the person's preferences node
/// while it was deleted, which opened the node-stream cache's read entry for the path. That entry's
/// request was answered "no node found" — the truth when it was DECIDED — but the answer was still on
/// its way when the test created the node again. The entry was neither faulted nor resolved, so the
/// recreate's change event left it in place; the inbox, told by its existence listing that the node was
/// back, opened the node's stream, joined that entry, received the deleted node's verdict, and — by the
/// read contract — stopped for good.</para>
///
/// <para><b>The forced interleaving.</b> <see cref="FaultInjectingStorageAdapter.DelayNextBatchReadAnswer"/>
/// lets the store DECIDE the route of the deleted path at once and DELIVER that "not found" only on
/// release — after the recreate and after the second reader asked. Nothing waits on a clock.</para>
/// </summary>
public class ARecreateRetiresTheReadOpenedOnTheDeletedNodeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>The injector goes in BEFORE the base adds in-memory persistence: it is the store of record.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.AddFaultInjectingStorage());

    private static string NewPath() => $"{TestPartition}/rr{Guid.NewGuid().ToString("N")[..10]}";

    private Task<MeshNode> Create(string path, string name) =>
        Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = "Markdown", Name = name, State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit($"'{name}' is created", TestContext.Current.CancellationToken);

    private Task Delete(string path) =>
        Access.RunAsSystem(() => NodeFactory.DeleteNode(path))
            .Should().Within(TestTimeouts.Convergence).Emit("the node is deleted", TestContext.Current.CancellationToken);

    /// <summary>A read of the node through the mesh's stream cache, every notification kept.</summary>
    private ReplaySubject<Notification<MeshNode>> Read(string path, out IDisposable subscription)
    {
        var seen = new ReplaySubject<Notification<MeshNode>>();
        subscription = Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path)).Materialize().Subscribe(seen);
        return seen;
    }

    /// <summary>The reader's first answer: the node's name, or the failure that ended it.</summary>
    private static Task<string> FirstAnswer(IObservable<Notification<MeshNode>> seen, string because)
        => seen.Where(n => n.Kind == NotificationKind.OnError || n.Value is not null)
            .Select(n => n.Kind == NotificationKind.OnNext ? n.Value!.Name ?? string.Empty : $"FAILED: {n.Exception?.Message}")
            .Should().Within(TestTimeouts.Convergence).Emit(because, TestContext.Current.CancellationToken);

    /// <summary>
    /// THE FALSIFIER — a background READ of the deleted path is in flight across the recreate. On main
    /// before the fix, the reader that asks after the recreate joins that read and is told the node does
    /// not exist; so is the background reader. Both must see the recreated node.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AReadInFlightAcrossTheRecreate_NeverHandsTheDeletedNodesVerdictToALaterReader()
    {
        var path = NewPath();
        await Create(path, "first");
        await Delete(path);

        using var delay = Storage.DelayNextBatchReadAnswer(path);
        var background = Read(path, out var backgroundSubscription);
        using var _ = backgroundSubscription;
        await delay.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the background read's route was decided while the node was gone", TestContext.Current.CancellationToken);

        await Create(path, "second");
        var later = Read(path, out var laterSubscription);
        using var __ = laterSubscription;
        // The "no node found" decided before the recreate is delivered only now.
        delay.Release();

        (await FirstAnswer(later, "the reader that asked after the recreate is answered"))
            .Should().Be("second", "the node exists; the deleted node's verdict is not this reader's answer");
        (await FirstAnswer(background, "the background reader is answered"))
            .Should().Be("second", "a read the cache retired is moved to the node that exists, not ended");
    }

    /// <summary>
    /// The same latch opened by a background WRITE — the inbox's own shape: every write pins the path's
    /// read entry, so a write to the deleted path opens the entry whose verdict arrives late.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AWriteInFlightAcrossTheRecreate_NeverHandsTheDeletedNodesVerdictToALaterReader()
    {
        var path = NewPath();
        await Create(path, "first");
        await Delete(path);

        using var delay = Storage.DelayNextBatchReadAnswer(path);
        // The background write's own outcome is not this test's subject (a write to a deleted node fails);
        // its error arm keeps it observed.
        using var write = Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path).Update(n => n with { Name = "background" }))
            .Subscribe(_ => { }, ex => Output.WriteLine($"background write ended: {ex.Message}"));
        await delay.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the background write's read was routed while the node was gone", TestContext.Current.CancellationToken);

        await Create(path, "second");
        var later = Read(path, out var laterSubscription);
        using var _ = laterSubscription;
        delay.Release();

        (await FirstAnswer(later, "the reader that asked after the recreate is answered"))
            .Should().BeOneOf(["second", "background"], "the node exists; the deleted node's verdict is not this reader's answer");
    }

    /// <summary>
    /// Negative control: with NOTHING in flight across the recreate, the same delete-then-create is read
    /// as the new node — the injector alone, released before the recreate, changes nothing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task WithNothingInFlight_ARecreatedNodeIsReadAsTheNewNode()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var path = NewPath();
        await Create(path, "first");
        await Delete(path);
        Storage.DelayNextBatchReadAnswer(path).Release();

        await Create(path, "second");
        var later = Read(path, out var laterSubscription);
        using var _ = laterSubscription;
        (await FirstAnswer(later, "the reader is answered")).Should().Be("second");
    }

    /// <summary>
    /// The tombstone's verdict while a recreate is on its way to the store. The store publishes the row
    /// on its own change feed INSIDE the commit — listings see it — and the tombstone is superseded only
    /// on the write's emission after that, so a reader reacting to the listing could reach the old,
    /// disposing hub while the tombstone still said "gone for good" and be told the node will never
    /// come back. A write the storage guard admitted therefore already answers "not gone"; the
    /// resurrection guard (<see cref="RecentlyDeletedRegistry.IsRecentlyDeleted"/>) is unchanged.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnAdmittedRecreate_IsNeverReportedAsGoneForGood()
    {
        var path = NewPath();
        await Create(path, "first");
        await Delete(path);
        var tombstones = Mesh.ServiceProvider.GetRequiredService<IAddressTombstones>();
        var registry = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
        tombstones.IsDeleted(path).Should().BeTrue("the premise: the address was just deleted");

        using var hold = Storage.HoldWrites(path);
        using var recreate = Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = "Markdown", Name = "second", State = MeshNodeState.Active,
            }))
            .Subscribe(_ => { }, ex => Output.WriteLine($"recreate ended: {ex.Message}"));
        await hold.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the recreate was admitted by the guard and reached the store", TestContext.Current.CancellationToken);

        tombstones.IsDeleted(path).Should().BeFalse(
            "a recreate is on its way to the store: the address is not gone for good, so a disposing hub must answer 'retry'");
        registry.IsRecentlyDeleted(path).Should().BeTrue(
            "the resurrection guard still holds until the write commits");

        hold.Release();
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path)).Where(n => n?.Name == "second")
            .Should().Within(TestTimeouts.Convergence).Emit("the recreate commits", TestContext.Current.CancellationToken);
        tombstones.IsDeleted(path).Should().BeFalse("the recreate superseded the tombstone");
    }

    /// <summary>The registry's own contract, without a mesh: an admitted write lifts "gone for good" only while it is in flight.</summary>
    [Fact]
    public void AnAdmittedWrite_LiftsGoneForGood_OnlyWhileInFlight()
    {
        var registry = new RecentlyDeletedRegistry();
        registry.MarkDeleted("a/b");
        registry.IsDeleted("a/b").Should().BeTrue();
        using (registry.AdmitWrite(["a/other"]))
            registry.IsDeleted("a/b").Should().BeTrue("a write to another path says nothing about this one");
        using (registry.AdmitWrite(["a/b"]))
        {
            registry.IsDeleted("a/b").Should().BeFalse("a write to the path is on its way to the store");
            registry.IsRecentlyDeleted("a/b").Should().BeTrue("a write in flight never licenses a resurrecting save");
        }
        registry.IsDeleted("a/b").Should().BeTrue("a write that settled without superseding (it failed) restores the verdict");
    }
}
