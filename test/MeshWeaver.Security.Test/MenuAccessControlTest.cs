using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests that menu items are filtered server-side by the provider/permission system.
/// Renders a layout area for the target node and reads MenuControl from the $Menu slot.
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
    /// enabling server-side permission checks via ISecurityService.
    /// </summary>
    private IMessageHub GetClientWithUser(string userId = TestUserId)
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });
        return client;
    }

    private async Task<IReadOnlyList<NodeMenuItemDefinition>> FetchMenuItemsAsync(
        IMessageHub client, Address nodeAddress, string menuContext)
    {
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress,
            reference);

        // Read the $Menu:{context} control from the layout stream
        var menuControl = await stream.GetControlStream(MenuControl.GetMenuArea(menuContext))
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        var menu = menuControl.Should().BeOfType<MenuControl>().Which;
        return menu.Items;
    }

    /// <summary>
    /// Fetches both Node and Mesh menus in parallel and returns their items merged and sorted by Order.
    /// Running in parallel keeps total elapsed time inside the per-fetch timeout budget.
    /// </summary>
    private async Task<IReadOnlyList<NodeMenuItemDefinition>> FetchAllMenuItemsAsync(
        IMessageHub client, Address nodeAddress)
    {
        var nodeTask = FetchMenuItemsAsync(client, nodeAddress, NodeMenuItemsExtensions.NodeMenuContext);
        var meshTask = FetchMenuItemsAsync(client, nodeAddress, NodeMenuItemsExtensions.MeshMenuContext);
        await Task.WhenAll(nodeTask, meshTask);
        return FlattenMenuItems([.. nodeTask.Result, .. meshTask.Result]);
    }

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
    public async Task Menu_NoRoles_SubscriptionDenied()
    {
        // With RLS enabled but no roles seeded, user has Permission.None.
        // The AccessControlPipeline blocks SubscribeRequest (requires Read),
        // so the stream receives a DeliveryFailure.
        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(15.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        var act = () => FetchAllMenuItemsAsync(client, nodeAddress);
        var ex = await Assert.ThrowsAsync<DeliveryFailureException>(act);
        ex.Message.Should().Contain("Access denied",
            "user with no roles should be denied Read access on the hub");
    }

    [Fact(Timeout = 30000)]
    public async Task Menu_ReadOnlyUser_ShowsOnlyUnrestrictedItems()
    {
        // Viewer role: Read only → no Create, Update, or Delete
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync(TestUserId, "Viewer", NodePath, "system");

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(15.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        var items = await FetchAllMenuItemsAsync(client, nodeAddress);

        Output.WriteLine($"Menu items for Viewer: {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} (Area={item.Area})");

        items.Select(i => i.Label).Should().BeEquivalentTo(
            ["Files", "Threads", "Versions", "Pin"],
            "Viewer has only Read — no Create, Update, Delete, or Export items (Pin requires no permission; Settings is a dedicated header button)");
    }

    [Fact(Timeout = 30000)]
    public async Task Menu_Editor_ShowsCreateItems()
    {
        // Editor role: Read|Create|Update|Comment → has Create but not Delete
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync(TestUserId, "Editor", NodePath, "system");

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(15.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        var items = await FetchAllMenuItemsAsync(client, nodeAddress);

        Output.WriteLine($"Menu items for Editor: {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} (Area={item.Area})");

        // Editor gets Edit, Create, Copy, Import, Export, Recycle (Update), Pin (None), plus always-visible items
        items.Select(i => i.Label).Should().BeEquivalentTo(
            ["Edit", "Create", "Copy", "Import", "Files", "Export", "Threads", "Versions", "Pin", "Recycle"],
            "Editor has Read|Create|Update|Comment|Export — Edit/Create/Copy/Import/Export/Recycle plus always-visible items and Pin (Settings is a dedicated header button)");
    }

    [Fact(Timeout = 30000)]
    public async Task Menu_Admin_ShowsAllItems()
    {
        // Admin role: All permissions
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync(TestUserId, "Admin", NodePath, "system");

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(15.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        var items = await FetchAllMenuItemsAsync(client, nodeAddress);

        Output.WriteLine($"Menu items for Admin: {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} (Area={item.Area})");

        items.Should().HaveCount(12, "Admin should see all default menu items across Node and Mesh contexts (Settings is a dedicated header button)");
        items.Select(i => i.Label).Should().BeEquivalentTo(
            ["Edit", "Create", "Copy", "Move", "Import", "Files", "Export", "Threads", "Versions", "Delete", "Pin", "Recycle"]);
    }

    [Fact(Timeout = 30000)]
    public async Task Menu_ItemsAreSortedByOrder()
    {
        // Seed Admin so we get all items for sorting verification
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync(TestUserId, "Admin", NodePath, "system");

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(15.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        // Each menu context is sorted independently by Order — verify both
        var nodeItems = await FetchMenuItemsAsync(client, nodeAddress, NodeMenuItemsExtensions.NodeMenuContext);
        var meshItems = await FetchMenuItemsAsync(client, nodeAddress, NodeMenuItemsExtensions.MeshMenuContext);

        nodeItems.Should().BeInAscendingOrder(i => i.Order, "Node menu items should be sorted by Order");
        meshItems.Should().BeInAscendingOrder(i => i.Order, "Mesh menu items should be sorted by Order");
    }

    [Fact(Timeout = 30000)]
    public async Task Menu_ImportAreaIsImportMeshNodes()
    {
        // Seed Editor to get Import item (requires Create permission)
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync(TestUserId, "Editor", NodePath, "system");

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(15.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        // Import lives in the Mesh menu
        var meshItems = await FetchMenuItemsAsync(client, nodeAddress, NodeMenuItemsExtensions.MeshMenuContext);

        var importItem = meshItems.FirstOrDefault(i => i.Label == "Import");
        importItem.Should().NotBeNull("Import menu item should exist in the Mesh menu for Editor");
        importItem!.Area.Should().Be(MeshNodeLayoutAreas.ImportMeshNodesArea,
            "Import should navigate to ImportMeshNodes area, not $Import");
    }

    [Fact(Timeout = 30000)]
    public async Task StaticRoles_AppearInNodeTypeRoleQuery()
    {
        // Static built-in roles should appear when querying namespace:Role with nodeType:Role
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var roles = await meshQuery
            .QueryAsync<MeshNode>("namespace:Role nodeType:Role")
            .ToListAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Roles returned: {roles.Count}");
        foreach (var r in roles)
            Output.WriteLine($"  {r.Path} ({r.Name})");

        roles.Should().HaveCountGreaterThanOrEqualTo(4,
            "built-in roles (Admin, Editor, Viewer, Commenter) should appear as static nodes");
        roles.Select(r => r.Name).Should().Contain(
            ["Admin", "Editor", "Viewer", "Commenter"]);
    }

    [Fact(Timeout = 30000)]
    public async Task StaticRoles_NotIncludedInGenericChildrenQuery()
    {
        // Static roles should NOT appear in unfiltered children queries
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var children = await meshQuery
            .QueryAsync<MeshNode>($"namespace:{NodePath}")
            .ToListAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Children returned: {children.Count}");
        foreach (var c in children)
            Output.WriteLine($"  {c.Path} ({c.Name})");

        children.Select(c => c.Name).Should().NotContain(
            ["Administrator", "Editor", "Viewer", "Commenter"],
            "static roles should not leak into generic children queries");
    }
}
