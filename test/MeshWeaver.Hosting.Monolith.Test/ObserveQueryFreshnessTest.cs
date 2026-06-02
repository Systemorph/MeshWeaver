using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pinpoints two Query contracts that NodeCopyHelper relies on:
///
/// 1. After CreateNode completes, an Query for the just-created path
///    must emit it in the initial result set. No "wait for index", no race.
///
/// 2. After UpdateNode completes, an Query covering the path must emit
///    the LATEST content in its initial result set (not the pre-update copy
///    that some lagged read-side index might still hold).
///
/// Failure modes these tests catch:
/// - Stale catalog index: query lags behind writes → first Query sees
///   nothing or sees the old content.
/// - Provider eventual consistency: the provider rebuilds asynchronously and
///   the Initial emission is computed from a snapshot taken before the write.
/// </summary>
public class ObserveQueryFreshnessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Ns = "TestData/Freshness";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact]
    public void ObserveQuery_AfterCreate_ReturnsTheJustCreatedNode()
    {
        var path = $"{Ns}/created-node";

        MeshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "Created",
            NodeType = "Markdown",
            Content = MarkdownContent.Parse("Hello", "", path)
        }).Should().Emit();

        var change = MeshService.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{path}"))
            .Should().Within(5.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Initial);

        change.Items.Should().ContainSingle("the just-created node must appear in the initial result set");
        change.Items.Single().Path.Should().Be(path);
        change.Items.Single().Name.Should().Be("Created");
    }

    [Fact]
    public void ObserveQuery_AfterUpdate_ReturnsTheLatestContent()
    {
        var path = $"{Ns}/updated-node";

        var created = MeshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "v1",
            NodeType = "Markdown",
            Content = MarkdownContent.Parse("first", "", path)
        }).Should().Emit();

        MeshService.UpdateNode(created with
        {
            Name = "v2",
            Content = MarkdownContent.Parse("second", "", path)
        }).Should().Emit();

        var change = MeshService.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{path}"))
            .Should().Within(5.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Initial);

        change.Items.Should().ContainSingle();
        change.Items.Single().Name.Should().Be("v2", "Query initial set must reflect the most recent UpdateNode");
        var content = change.Items.Single().Content as MarkdownContent;
        content.Should().NotBeNull();
        content!.Content.Should().Be("second");
    }

    [Fact]
    public void ObserveQuery_DescendantsAfterUpdate_ReturnsLatestContentForEachItem()
    {
        MeshService.CreateNode(MeshNode.FromPath(Ns) with { Name = "Root", NodeType = "Markdown" }).Should().Emit();
        MeshService.CreateNode(MeshNode.FromPath($"{Ns}/A") with
        {
            Name = "v1-A", NodeType = "Markdown",
            Content = MarkdownContent.Parse("a-first", "", $"{Ns}/A")
        }).Should().Emit();
        MeshService.CreateNode(MeshNode.FromPath($"{Ns}/B") with
        {
            Name = "v1-B", NodeType = "Markdown",
            Content = MarkdownContent.Parse("b-first", "", $"{Ns}/B")
        }).Should().Emit();

        // Mutate B only.
        MeshService.UpdateNode(MeshNode.FromPath($"{Ns}/B") with
        {
            Name = "v2-B", NodeType = "Markdown",
            Content = MarkdownContent.Parse("b-second", "", $"{Ns}/B")
        }).Should().Emit();

        var change = MeshService.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{Ns} scope:descendants"))
            .Should().Within(5.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Initial
                && c.Items.Any(n => n.Path == $"{Ns}/B" && n.Name == "v2-B"));

        var byPath = change.Items.ToDictionary(n => n.Path);
        byPath.Should().ContainKey($"{Ns}/A");
        byPath.Should().ContainKey($"{Ns}/B");

        byPath[$"{Ns}/A"].Name.Should().Be("v1-A", "A was not modified");
        byPath[$"{Ns}/B"].Name.Should().Be("v2-B",
            "Query descendants initial set must carry the post-UpdateNode content for B — " +
            "if it returns 'v1-B', the read-side index is lagging the writes (CQRS staleness bug).");
    }

    /// <summary>
    /// Regression test for the ObserveQueryInternal scheduler-capture deadlock:
    /// before the Task.Run fix, subscribing Query from within a hub-
    /// reachable code path captured the Orleans TaskScheduler and the
    /// await-foreach continuation was posted back to a scheduler that was busy
    /// — the 2-second Timeout in SecurityService fired and menus showed as
    /// inaccessible (Permission.None). This test pins that change notifications
    /// arrive after initial, exercising both the initial query (Task.Run path)
    /// and the debounce subscription (also wrapped in Task.Run).
    /// </summary>
    [Fact]
    public void ObserveQuery_ReceivesAddNotification_WhenNodeCreatedAfterSubscription()
    {
        const string ns = "TestData/Freshness/LiveAdd";

        // Replay() makes the stream hot and buffers all events so that multiple
        // .Where(...).Match() chains on the same stream see every event.
        // Without Replay, each chain creates a new cold subscription and misses
        // events that were emitted on a sibling subscription.
        var hotChanges = MeshService.Query<MeshNode>(
            MeshQueryRequest.FromQuery($"namespace:{ns} nodeType:Markdown"))
            .Replay();
        using var conn = hotChanges.Connect();

        // Wait for the initial (empty) emission.
        hotChanges.Should().Within(5.Seconds()).Match(c => c.ChangeType == QueryChangeType.Initial);

        // Now create a node — the change-notifier debounce should deliver an Add.
        MeshService.CreateNode(MeshNode.FromPath($"{ns}/live-1") with
        {
            Name = "Live-1", NodeType = "Markdown",
            Content = MarkdownContent.Parse("hello", "", $"{ns}/live-1")
        }).Should().Emit();

        var addChange = hotChanges
            .Should().Within(10.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Added && c.Items.Any(n => n.Path == $"{ns}/live-1"));

        addChange.Items.Should().ContainSingle(n => n.Path == $"{ns}/live-1",
            "the debounce subscription must deliver an Added event after CreateNode");
    }

    [Fact]
    public void ObserveQuery_ReceivesUpdateNotification_WhenNodeUpdatedAfterSubscription()
    {
        const string ns = "TestData/Freshness/LiveUpdate";
        var path = $"{ns}/live-upd";

        // Seed the node first.
        MeshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "v1", NodeType = "Markdown",
            Content = MarkdownContent.Parse("original", "", path)
        }).Should().Emit();

        var hotChanges = MeshService.Query<MeshNode>(
            MeshQueryRequest.FromQuery($"path:{path}"))
            .Replay();
        using var conn = hotChanges.Connect();

        // Wait for initial result containing v1.
        hotChanges.Should().Within(5.Seconds()).Match(c => c.ChangeType == QueryChangeType.Initial);

        // Update after subscription.
        MeshService.UpdateNode(MeshNode.FromPath(path) with
        {
            Name = "v2", NodeType = "Markdown",
            Content = MarkdownContent.Parse("updated", "", path)
        }).Should().Emit();

        var updChange = hotChanges
            .Should().Within(10.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Updated && c.Items.Any(n => n.Name == "v2"));

        updChange.Items.Should().ContainSingle(n => n.Name == "v2",
            "the debounce subscription must deliver an Updated event after UpdateNode");
    }

    /// <summary>
    /// Isolated repro for the WaitForPermissionAsync timeout pattern observed
    /// in CreateNodeViaEventTest / EffectivePermissionPostgresTest:
    /// 1. Subscribe to a synced query for AccessAssignment nodes under a
    ///    given _Access namespace BEFORE any are created.
    /// 2. CreateNode an AccessAssignment via meshService.CreateNode.
    /// 3. Verify the subscription receives an Added event with the new node.
    ///
    /// If this fails, the cross-hub change propagation (assignment write on
    /// its owning per-node hub → IDataChangeNotifier mesh singleton → synced
    /// query subscriber on the test mesh hub) is broken — that's the
    /// production-side bug behind the test cluster, not a test-pattern flaw.
    /// </summary>
    [Fact(Timeout = 15_000)]
    public void ObserveQuery_AccessAssignment_AddedEventArrives_AfterCreateNode()
    {
        const string scope = "RepoTest";
        const string ns = scope + "/_Access";

        // Hot subscription on the AccessAssignment-namespace query — same shape
        // as SecurityService.ObserveScopeAssignments uses internally.
        var hot = MeshService.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"namespace:{ns} nodeType:AccessAssignment"))
            .Replay();
        using var conn = hot.Connect();

        // Wait for Initial (empty — no AccessAssignments at this scope yet).
        var initial = hot.Should().Within(5.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Initial);
        initial.Items.Should().BeEmpty("no AccessAssignments seeded at this scope");

        // Now create an AccessAssignment via the standard CreateNode flow.
        MeshService.CreateNode(AssignmentNodeFactory.UserRole(
                "test-user", "Admin", scope))
            .Should().Emit();

        // The hot subscription must receive an Added event with the new node.
        var added = hot
            .Should().Within(8.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Added
                && c.Items.Any(n => n.Path == $"{ns}/test-user_Access"));

        added.Items.Should().ContainSingle(n => n.Path == $"{ns}/test-user_Access",
            "the cross-hub change propagation (write on the AccessAssignment's own per-node " +
            "hub → mesh-singleton IDataChangeNotifier → synced-query subscriber on this hub) " +
            "must deliver the Added event after meshService.CreateNode completes.");
    }

    /// <summary>
    /// The OPPOSITE order — the pattern that CreateNodeViaEventTest /
    /// EffectivePermissionPostgresTest actually use:
    /// 1. CreateNode an AccessAssignment FIRST.
    /// 2. THEN subscribe to a synced query for AccessAssignments at that scope.
    /// 3. Initial emission must include the just-created node.
    ///
    /// If this fails, it means the storage adapter completed the write but the
    /// synced query's Initial query reads a stale snapshot — exactly the
    /// production-side bug behind the test cluster.
    /// </summary>
    [Fact(Timeout = 15_000)]
    public void ObserveQuery_AccessAssignment_InitialIncludesPriorCreate()
    {
        const string scope = "RepoTest2";
        const string ns = scope + "/_Access";

        // Create FIRST.
        MeshService.CreateNode(AssignmentNodeFactory.UserRole(
                "test-user", "Admin", scope))
            .Should().Emit();

        // THEN subscribe — Initial must reflect the prior write.
        var initial = MeshService.Query<MeshNode>(
                MeshQueryRequest.FromQuery($"namespace:{ns} nodeType:AccessAssignment"))
            .Should().Within(5.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Initial
                && c.Items.Any(n => n.Path == $"{ns}/test-user_Access"));

        initial.Items.Should().ContainSingle(n => n.Path == $"{ns}/test-user_Access",
            "the synced query's Initial must include AccessAssignments written before subscribe.");
    }

    /// <summary>
    /// End-to-end repro using SecurityService.GetEffectivePermissions, the
    /// API that WaitForPermissionAsync (and AccessControlPipeline) actually
    /// consume. If the same write-then-observe pattern works with SecurityService,
    /// then the failing CreateNodeViaEventTest cluster is a test-pattern bug,
    /// not a framework bug.
    /// </summary>
    [Fact(Timeout = 15_000)]
    public void SecurityService_AfterCreate_GrantsPermission_OnFirstSubscribe()
    {
        const string scope = "RepoTest3";
        const string userId = "test-user-3";
        // permission check on Mesh hub

        // Create AccessAssignment first.
        MeshService.CreateNode(AssignmentNodeFactory.UserRole(userId, "Admin", scope))
            .Should().Emit();

        // SecurityService subscribe — must emit a permission-set with Create within 5s.
        // This is the EXACT chain WaitForPermissionAsync depends on.
        var perm = Mesh.GetEffectivePermissions(scope, userId)
            .Should().Within(5.Seconds())
            .Match(p => p.HasFlag(MeshWeaver.Mesh.Security.Permission.Create));

        perm.HasFlag(MeshWeaver.Mesh.Security.Permission.Create).Should().BeTrue();
    }

    /// <summary>
    /// Reproduces the EXACT pattern from CreateNodeViaEventTest using the
    /// hub address (with @ + / separators) as the userId — that's the only
    /// thing different from <see cref="SecurityService_AfterCreate_GrantsPermission_OnFirstSubscribe"/>.
    /// If THIS fails, the AccessObject vs. userId matching breaks for hub-shaped IDs.
    /// </summary>
    [Fact(Timeout = 15_000)]
    public void SecurityService_HubAddressUserId_GrantsPermission()
    {
        var meshAddress = Mesh.Address.ToFullString();
        // permission check on Mesh hub

        Output.WriteLine($"meshAddress = '{meshAddress}'");

        MeshService.CreateNode(AssignmentNodeFactory.UserRole(meshAddress, "Admin", "Impersonate"))
            .Should().Emit();

        var perm = Mesh.GetEffectivePermissions("Impersonate", meshAddress)
            .Should().Within(5.Seconds())
            .Match(p => p.HasFlag(MeshWeaver.Mesh.Security.Permission.Create));

        perm.HasFlag(MeshWeaver.Mesh.Security.Permission.Create).Should().BeTrue();
    }
}
