using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.InstanceSync.Test;

/// <summary>
/// The push direction end-to-end: initial full replication, incremental change propagation,
/// path remapping onto a differently-named remote space, delete propagation, and the core
/// offline story — changes accumulate in the durable manifest while the remote is unreachable
/// and drain on the first successful reconnect probe.
/// </summary>
public class InstanceSyncPushTest(ITestOutputHelper output) : InstanceSyncTestBase(output)
{
    [Fact]
    public async Task Initial_replication_pushes_space_subtree_to_remote()
    {
        await CreateSpace("alpha", "Alpha");
        await CreateMarkdown("alpha/notes", "Notes", "hello world");

        await AddConfiguredSource("alpha");

        var cfg = await WaitForConfig("alpha", "partner",
            c => c.InitialSyncAt is not null && c.Status == InstanceSyncStatus.Syncing);
        cfg.LastSyncedAt.Should().NotBeNull();
        cfg.LastError.Should().BeNull();

        Remote.Node("alpha").Should().NotBeNull("the space root replicates first");
        Remote.Node("alpha")!.NodeType.Should().Be("Space");
        Remote.Node("alpha/notes").Should().NotBeNull();
        MarkdownBody(Remote.Node("alpha/notes")).Should().Be("hello world");
        // The sync registry itself must never replicate (it holds the remote token).
        Remote.Store.Keys.Should().NotContain(k => k.Contains("/_Sync"));
    }

    [Fact]
    public async Task Local_changes_push_incrementally_after_initial_sync()
    {
        await CreateSpace("beta");
        await AddConfiguredSource("beta");
        await WaitForConfig("beta", "partner", c => c.InitialSyncAt is not null);

        await CreateMarkdown("beta/doc1", "Doc 1", "v1");
        await WaitForRemote(r => r.Node("beta/doc1") is not null);

        await Mesh.GetWorkspace().GetMeshNodeStream("beta/doc1")
            .Update(n => n with { Content = new MarkdownContent { Content = "v2" } })
            .Timeout(30.Seconds()).ToTask();
        await WaitForRemote(r => MarkdownBody(r.Node("beta/doc1")) == "v2");

        var cfg = await WaitForConfig("beta", "partner", c => c.PendingChanges.IsEmpty);
        cfg.Status.Should().Be(InstanceSyncStatus.Syncing);
    }

    [Fact]
    public async Task Remote_space_name_remaps_the_whole_subtree()
    {
        await CreateSpace("gamma");
        await CreateMarkdown("gamma/page", "Page", "content");

        await AddConfiguredSource("gamma", remoteSpace: "mirror");
        await WaitForConfig("gamma", "partner", c => c.InitialSyncAt is not null);

        Remote.Node("mirror").Should().NotBeNull();
        Remote.Node("mirror/page").Should().NotBeNull();
        Remote.Store.Keys.Should().NotContain("gamma");
    }

    [Fact]
    public async Task Local_delete_propagates_to_remote()
    {
        await CreateSpace("delta");
        await CreateMarkdown("delta/temp", "Temp", "to be removed");
        await AddConfiguredSource("delta");
        await WaitForConfig("delta", "partner", c => c.InitialSyncAt is not null);
        await WaitForRemote(r => r.Node("delta/temp") is not null);

        await NodeFactory.DeleteNode("delta/temp").Timeout(30.Seconds()).ToTask();

        await WaitForRemote(r => r.Node("delta/temp") is null);
    }

    /// <summary>
    /// 🚨 THE COUPLING GUARD. A pending NON-DELETE manifest entry whose local node is being deleted
    /// must push a DELETE to the remote — never the content.
    ///
    /// <para>This pins what makes <c>OnLocalChange</c>'s echo-suppression slot safe to consume on a
    /// <c>Deleted</c> event. Doing so lets the pull-apply's echo escape suppression, and because
    /// the manifest coalesces per path with the latest entry winning, that echo can replace the
    /// <c>Deleted</c> entry with a content change. The remote still converges on the deletion for
    /// exactly ONE reason: <c>DrainOne</c> resolves every entry against the CURRENT local state
    /// instead of trusting the recorded kind, so a node that is gone degrades the push to a delete.
    /// That is a real coupling between two methods, and the sort of thing a later "we already know
    /// the kind, skip the read" refactor removes without noticing — hence a test rather than a
    /// comment.</para>
    ///
    /// <para>Note this is a REGRESSION GUARD, not a fail-before test: the pre-fix code reached the
    /// same delete through the collapsed <c>null</c> it is the point of this PR to remove. Its job
    /// is to stop the property being lost in the future, now that something else leans on it.</para>
    ///
    /// <para>The setup is deterministic and production-shaped: the remote is offline so the
    /// manifest ACCUMULATES a content entry, and the delete is put in flight with the same
    /// synchronous tombstone the delete handler writes — which emits no change event, so the
    /// manifest keeps its non-delete entry. That is exactly the escaped-echo shape, without having
    /// to win a race to produce it.</para>
    /// </summary>
    [Fact]
    public async Task A_pending_content_change_pushes_a_DELETE_when_the_local_node_is_being_deleted()
    {
        await CreateSpace("theta");
        await CreateMarkdown("theta/doc", "Doc", "v1");
        await AddConfiguredSource("theta");
        await WaitForConfig("theta", "partner", c => c.InitialSyncAt is not null);
        await WaitForRemote(r => r.Node("theta/doc") is not null);

        // Offline: the content change accumulates instead of draining immediately.
        Remote.Unreachable = true;
        await Mesh.GetWorkspace().GetMeshNodeStream("theta/doc")
            .Update(n => n with { Content = new MarkdownContent { Content = "v2" } })
            .Should().Within(30.Seconds()).Emit();
        var offline = await WaitForConfig("theta", "partner", c => c.PendingChanges.Count == 1);
        offline.PendingChanges[0].Kind.Should().NotBe(MeshChangeKind.Deleted,
            "the entry under test is a CONTENT change — the delete fast-path in DrainOne would "
            + "make this test pass without ever reading the local node");

        // 🔻 The delete goes in flight. MarkDeleted is what the delete handler does synchronously,
        // and it emits no change event — so the manifest keeps its non-delete entry.
        Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>().MarkDeleted("theta/doc");

        Remote.Unreachable = false;
        await WaitForRemote(r => r.Node("theta/doc") is null);
    }

    [Fact]
    public async Task Unreachable_remote_accumulates_changes_and_drains_on_reconnect()
    {
        await CreateSpace("epsilon");
        await AddConfiguredSource("epsilon");
        await WaitForConfig("epsilon", "partner", c => c.InitialSyncAt is not null);

        Remote.Unreachable = true;

        await CreateMarkdown("epsilon/a", "A", "a1");
        await CreateMarkdown("epsilon/b", "B", "b1");
        // Two writes to the same path coalesce to ONE manifest entry (latest state wins).
        await Mesh.GetWorkspace().GetMeshNodeStream("epsilon/a")
            .Update(n => n with { Content = new MarkdownContent { Content = "a2" } })
            .Timeout(30.Seconds()).ToTask();

        // Offline: the manifest holds exactly the two touched paths, durably on the node.
        var offline = await WaitForConfig("epsilon", "partner",
            c => c.Status == InstanceSyncStatus.Offline && c.PendingChanges.Count == 2);
        offline.PendingChanges.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal)
            .Should().Equal("epsilon/a", "epsilon/b");
        offline.LastError.Should().NotBeNullOrEmpty();
        Remote.Node("epsilon/a").Should().BeNull("nothing reaches an unreachable remote");

        // Reconnect: the retry probe drains the manifest without any new local change.
        Remote.Unreachable = false;

        var synced = await WaitForConfig("epsilon", "partner",
            c => c.Status == InstanceSyncStatus.Syncing && c.PendingChanges.IsEmpty);
        synced.LastError.Should().BeNull();
        MarkdownBody(Remote.Node("epsilon/a")).Should().Be("a2", "the drain pushes the LATEST content");
        MarkdownBody(Remote.Node("epsilon/b")).Should().Be("b1");
    }

    [Fact]
    public async Task Pending_manifest_survives_restart_and_drains_on_startup()
    {
        await CreateSpace("eta");
        await AddConfiguredSource("eta");
        await WaitForConfig("eta", "partner", c => c.InitialSyncAt is not null);

        Remote.Unreachable = true;
        await CreateMarkdown("eta/offline-doc", "Offline Doc", "written while down");
        await WaitForConfig("eta", "partner", c => c.PendingChanges.Count == 1);

        // "Restart": stop the coordinator (its workers die with it), reconnect the remote,
        // start again — the manifest lives on the config NODE, not in worker memory.
        await Coordinator.StopAsync(TestContext.Current.CancellationToken);
        Remote.Unreachable = false;
        await Coordinator.StartAsync(TestContext.Current.CancellationToken);

        // Boot discovery resumes the registration and drains the persisted manifest.
        await WaitForRemote(r => r.Node("eta/offline-doc") is not null);
        await WaitForConfig("eta", "partner",
            c => c.PendingChanges.IsEmpty && c.Status == InstanceSyncStatus.Syncing);
    }

    [Fact]
    public async Task Paused_source_accumulates_but_transfers_nothing()
    {
        await CreateSpace("zeta");
        await AddConfiguredSource("zeta");
        await WaitForConfig("zeta", "partner", c => c.InitialSyncAt is not null);

        await Sync.UpdateConfig("zeta/_Sync/partner", c => c with { Active = false })
            .Timeout(30.Seconds()).ToTask();
        await WaitForConfig("zeta", "partner", c => c.Status == InstanceSyncStatus.Paused);

        await CreateMarkdown("zeta/held", "Held", "while paused");
        await WaitForConfig("zeta", "partner", c => c.PendingChanges.Count == 1);
        Remote.Node("zeta/held").Should().BeNull("paused sources transfer nothing");

        // Resume: the accumulated change drains.
        await Sync.UpdateConfig("zeta/_Sync/partner", c => c with { Active = true })
            .Timeout(30.Seconds()).ToTask();
        await WaitForRemote(r => r.Node("zeta/held") is not null);
        await WaitForConfig("zeta", "partner",
            c => c.Status == InstanceSyncStatus.Syncing && c.PendingChanges.IsEmpty);
    }
}
