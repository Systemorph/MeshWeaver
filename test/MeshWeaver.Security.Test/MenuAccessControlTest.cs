using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests that menu items are filtered server-side by the provider/permission system.
/// Renders a layout area for the target node and reads MenuControl from the $Menu slot.
/// <para>
/// 🚨 The menu is <b>reactive</b> (see <see cref="NodeMenuItemsExtensions"/>): the renderer seeds
/// each provider with <c>StartWith(empty)</c> and re-emits the merged <see cref="MenuControl"/>
/// whenever the viewer's effective permissions enrich (a runtime <c>AccessAssignment</c> only
/// reaches the menu on the <c>enriched</c> permission stream — after the synced query catches up).
/// So a test must NOT grab the first non-null menu (that's the empty/pre-propagation render) — it
/// waits on the stream until the menu reaches the expected state via <c>.Where(predicate)</c>. A
/// timeout (never a wrong snapshot) is the failure signal. This is the fix for the old
/// <c>Menu_Editor_ShowsCreateItems</c> flake, which raced role propagation.
/// </para>
/// </summary>
public class MenuAccessControlTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodePath = "TestOrg/TestProject";
    private const string TestUserId = "TestUser";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode("TestOrg") { Name = "Test Organization" },
                new MeshNode("TestProject", "TestOrg") { Name = "Test Project" }
            )
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas());

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    /// <summary>
    /// Creates a client hub with an AccessContext for the test user.
    /// The AccessContext flows through the message delivery pipeline to the node hub,
    /// enabling server-side permission checks via SecurityService.
    /// </summary>
    private IMessageHub GetClientWithUser(string userId = TestUserId)
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });
        return client;
    }

    /// <summary>
    /// Live stream of a single menu context's items, projected from the node's Overview layout
    /// stream. Re-emits on every menu re-render (e.g. when permissions enrich).
    /// </summary>
    private static IObservable<IReadOnlyList<NodeMenuItemDefinition>> MenuStream(
        IMessageHub client, Address nodeAddress, string menuContext)
    {
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);
        return stream.GetControlStream(MenuControl.GetMenuArea(menuContext))
            .Where(x => x is MenuControl)
            .Select(x => (IReadOnlyList<NodeMenuItemDefinition>)((MenuControl)x!).Items);
    }

    /// <summary>
    /// Waits until a single context's menu satisfies <paramref name="until"/>, then returns it.
    /// Times out (failing the test) if the menu never reaches the expected state — that's the
    /// signal a permission never propagated, not a wrong snapshot grabbed too early.
    /// </summary>
    private IReadOnlyList<NodeMenuItemDefinition> FetchMenuItems(
        IMessageHub client, Address nodeAddress, string menuContext,
        Func<IReadOnlyList<NodeMenuItemDefinition>, bool> until)
        => MenuStream(client, nodeAddress, menuContext)
            .Should().Within(20.Seconds()).Match(until);

    /// <summary>
    /// Combines the Node and Mesh menu streams and waits until their merged, flattened, sorted set
    /// satisfies <paramref name="until"/>. <c>StartWith([])</c> on each so the merge fires before
    /// both contexts have rendered.
    /// </summary>
    private IReadOnlyList<NodeMenuItemDefinition> FetchAllMenuItems(
        IMessageHub client, Address nodeAddress,
        Func<IReadOnlyList<NodeMenuItemDefinition>, bool> until)
    {
        var node = MenuStream(client, nodeAddress, NodeMenuItemsExtensions.NodeMenuContext)
            .StartWith((IReadOnlyList<NodeMenuItemDefinition>)[]);
        var mesh = MenuStream(client, nodeAddress, NodeMenuItemsExtensions.MeshMenuContext)
            .StartWith((IReadOnlyList<NodeMenuItemDefinition>)[]);
        return node
            .CombineLatest(mesh, (n, m) => FlattenMenuItems([.. n, .. m]))
            .Should().Within(20.Seconds()).Match(until);
    }

    /// <summary>Predicate: the menu's label set equals <paramref name="expected"/> exactly.</summary>
    private static Func<IReadOnlyList<NodeMenuItemDefinition>, bool> LabelsAre(params string[] expected)
        => items => items.Select(i => i.Label).ToHashSet().SetEquals(expected);

    /// <summary>
    /// Flattens menu items by expanding group items into their children, sorted by Order.
    /// </summary>
    private static IReadOnlyList<NodeMenuItemDefinition> FlattenMenuItems(IReadOnlyList<NodeMenuItemDefinition> items)
    {
        var flat = new List<NodeMenuItemDefinition>();
        foreach (var item in items)
        {
            if (item.Children is { Count: > 0 })
                flat.AddRange(item.Children);
            else
                flat.Add(item);
        }
        flat.Sort((a, b) => a.Order.CompareTo(b.Order));
        return flat;
    }

    [Fact(Timeout = 30000)]
    public void Menu_NoRoles_SubscriptionDenied()
    {
        // With RLS enabled but no roles seeded, user has Permission.None.
        // The AccessControlPipeline blocks SubscribeRequest (requires Read),
        // so the stream receives a DeliveryFailure.
        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Within(15.Seconds()).Emit();

        // Subscribe to the RAW menu stream (no StartWith seed) so the denied SubscribeRequest
        // surfaces as an error instead of being masked by a seeded empty emission. The positive
        // tests seed StartWith([]) to wait for population; the negative test must not.
        Action act = () => MenuStream(client, nodeAddress, NodeMenuItemsExtensions.NodeMenuContext)
            .Timeout(15.Seconds())
            .Wait();
        var ex = act.Should().Throw<DeliveryFailureException>().Which;
        ex.Message.Should().Contain("Access denied",
            "user with no roles should be denied Read access on the hub");
    }

    [Fact(Timeout = 30000)]
    public void Menu_ReadOnlyUser_ShowsOnlyUnrestrictedItems()
    {
        // Viewer role: Read only → no Create, Update, or Delete
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUserId, "Viewer", NodePath))
            .Should().Emit();

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Within(15.Seconds()).Emit();

        // Wait until the reactive menu settles on exactly the Viewer set.
        var items = FetchAllMenuItems(client, nodeAddress,
            LabelsAre("Files", "Threads", "Versions", "Pin"));

        Output.WriteLine($"Menu items for Viewer: {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} (Area={item.Area})");

        items.Select(i => i.Label).Should().BeEquivalentTo(
            new[] { "Files", "Threads", "Versions", "Pin" },
            JsonSerializerOptions.Default,
            because: "Viewer has only Read — no Create, Update, Delete, or Export items (Pin requires no permission; Settings is a dedicated header button)");
    }

    [Fact(Timeout = 30000)]
    public void Menu_Editor_ShowsCreateItems()
    {
        // Editor role: Read|Create|Update|Comment → has Create but not Delete
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUserId, "Editor", NodePath)).Should().Emit();

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Within(15.Seconds()).Emit();

        // Editor gets Edit, Create, Copy, Import, Export, Recycle (Update), Pin (None), plus
        // always-visible items. Wait until the reactive menu reaches exactly that set — this is the
        // fix for the old flake, where the menu was read before the Editor role propagated.
        var expected = new[]
        {
            "Edit", "Create", "Copy", "Import", "Files", "Export", "Threads", "Versions", "Pin", "Recycle"
        };
        var items = FetchAllMenuItems(client, nodeAddress, LabelsAre(expected));

        Output.WriteLine($"Menu items for Editor: {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} (Area={item.Area})");

        items.Select(i => i.Label).Should().BeEquivalentTo(expected,
            JsonSerializerOptions.Default,
            because: "Editor has Read|Create|Update|Comment|Export — Edit/Create/Copy/Import/Export/Recycle plus always-visible items and Pin (Settings is a dedicated header button)");
    }

    [Fact(Timeout = 30000)]
    public void Menu_Admin_ShowsAllItems()
    {
        // Admin role: All permissions
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUserId, "Admin", NodePath)).Should().Emit();

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Within(15.Seconds()).Emit();

        var expected = new[]
        {
            "Edit", "Create", "Copy", "Move", "Import", "Files", "Export", "Threads", "Versions", "Delete", "Pin", "Recycle"
        };
        var items = FetchAllMenuItems(client, nodeAddress, LabelsAre(expected));

        Output.WriteLine($"Menu items for Admin: {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} (Area={item.Area})");

        items.Should().HaveCount(12, "Admin should see all default menu items across Node and Mesh contexts (Settings is a dedicated header button)");
        items.Select(i => i.Label).Should().BeEquivalentTo(expected, JsonSerializerOptions.Default);
    }

    [Fact(Timeout = 30000)]
    public void Menu_ItemsAreSortedByOrder()
    {
        // Seed Admin so we get all items for sorting verification
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUserId, "Admin", NodePath)).Should().Emit();

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Within(15.Seconds()).Emit();

        // Each menu context is sorted independently by Order — verify both. Wait for each context to
        // reach its Admin-complete state (Delete = Node-only Admin item; Create = Mesh Admin item).
        var nodeItems = FetchMenuItems(client, nodeAddress, NodeMenuItemsExtensions.NodeMenuContext,
            items => items.Any(i => i.Label == "Delete"));
        var meshItems = FetchMenuItems(client, nodeAddress, NodeMenuItemsExtensions.MeshMenuContext,
            items => items.Any(i => i.Label == "Create"));

        nodeItems.Should().BeInAscendingOrder(i => i.Order, "Node menu items should be sorted by Order");
        meshItems.Should().BeInAscendingOrder(i => i.Order, "Mesh menu items should be sorted by Order");
    }

    [Fact(Timeout = 30000)]
    public void Menu_ImportAreaIsImportMeshNodes()
    {
        // Seed Editor to get Import item (requires Create permission)
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUserId, "Editor", NodePath)).Should().Emit();

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Within(15.Seconds()).Emit();

        // Import lives in the Mesh menu — wait until it appears (Editor role propagated).
        var meshItems = FetchMenuItems(client, nodeAddress, NodeMenuItemsExtensions.MeshMenuContext,
            items => items.Any(i => i.Label == "Import"));

        var importItem = meshItems.FirstOrDefault(i => i.Label == "Import");
        importItem.Should().NotBeNull("Import menu item should exist in the Mesh menu for Editor");
        importItem!.Area.Should().Be(MeshNodeLayoutAreas.ImportMeshNodesArea,
            "Import should navigate to ImportMeshNodes area, not $Import");
    }

    [Fact(Timeout = 30000)]
    public void StaticRoles_AppearInNodeTypeRoleQuery()
    {
        // Static built-in roles should appear when querying namespace:Role with nodeType:Role
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var roles = meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:Role nodeType:Role"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Count >= 4).Items;

        Output.WriteLine($"Roles returned: {roles.Count}");
        foreach (var r in roles)
            Output.WriteLine($"  {r.Path} ({r.Name})");

        roles.Should().HaveCountGreaterThanOrEqualTo(4,
            "built-in roles (Admin, Editor, Viewer, Commenter) should appear as static nodes");
        roles.Select(r => r.Name).Should().Contain(
            ["Admin", "Editor", "Viewer", "Commenter"]);
    }

    [Fact(Timeout = 30000)]
    public void StaticRoles_NotIncludedInGenericChildrenQuery()
    {
        // Static roles should NOT appear in unfiltered children queries
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var children = meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{NodePath}"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        Output.WriteLine($"Children returned: {children.Count}");
        foreach (var c in children)
            Output.WriteLine($"  {c.Path} ({c.Name})");

        children.Select(c => c.Name).Should().NotContain(
            ["Administrator", "Editor", "Viewer", "Commenter"],
            "static roles should not leak into generic children queries");
    }
}
