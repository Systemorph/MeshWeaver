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

namespace MeshWeaver.Hosting.Monolith.Test.Security;

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
            .AddRowLevelSecurity()
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

    private async Task<IReadOnlyList<NodeMenuItemDefinition>> FetchMenuItemsAsync(IMessageHub client, Address nodeAddress)
    {
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress,
            reference);

        // Read the $Menu control from the layout stream
        var menuControl = await stream.GetControlStream(MenuControl.MenuArea)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);

        var menu = menuControl.Should().BeOfType<MenuControl>().Which;
        return menu.Items;
    }

    [Fact(Timeout = 5000)]
    public async Task Menu_NoRoles_ShowsOnlyUnrestrictedItems()
    {
        // With RLS enabled but no roles seeded, user has Permission.None.
        // Only items with Permission.None requirement should appear.
        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(3.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        var items = await FetchMenuItemsAsync(client, nodeAddress);

        Output.WriteLine($"Menu items returned: {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} (Area={item.Area}, Permission={item.RequiredPermission})");

        items.Select(i => i.Label).Should().BeEquivalentTo(
            ["Threads"],
            "no roles assigned — only unrestricted items should appear; Settings requires Read");
    }

    [Fact(Timeout = 5000)]
    public async Task Menu_ReadOnlyUser_ShowsOnlyUnrestrictedItems()
    {
        // Viewer role: Read only → no Create, Update, or Delete
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync(TestUserId, "Viewer", NodePath, "system",
            TestContext.Current.CancellationToken);

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(3.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        var items = await FetchMenuItemsAsync(client, nodeAddress);

        Output.WriteLine($"Menu items for Viewer: {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} (Area={item.Area})");

        items.Select(i => i.Label).Should().BeEquivalentTo(
            ["Files", "Threads", "Settings"],
            "Viewer has only Read — no Create, Update, or Delete items");
    }

    [Fact(Timeout = 5000)]
    public async Task Menu_Editor_ShowsCreateItems()
    {
        // Editor role: Read|Create|Update|Comment → has Create but not Delete
        // No "Edit" — replaced by node-name item which requires a MeshNode with NodeType
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync(TestUserId, "Editor", NodePath, "system",
            TestContext.Current.CancellationToken);

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(3.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        var items = await FetchMenuItemsAsync(client, nodeAddress);

        Output.WriteLine($"Menu items for Editor: {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} (Area={item.Area})");

        // Editor gets Create + Import (no node-name because no MeshNode seeded)
        items.Select(i => i.Label).Should().BeEquivalentTo(
            ["Create", "Import", "Files", "Threads", "Settings"],
            "Editor has Read|Create|Update|Comment — Create/Import plus always-visible items");
    }

    [Fact(Timeout = 5000)]
    public async Task Menu_Admin_ShowsAllItems()
    {
        // Admin role: All permissions
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync(TestUserId, "Admin", NodePath, "system",
            TestContext.Current.CancellationToken);

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(3.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        var items = await FetchMenuItemsAsync(client, nodeAddress);

        Output.WriteLine($"Menu items for Admin: {items.Count}");
        foreach (var item in items)
            Output.WriteLine($"  {item.Label} (Area={item.Area})");

        // No "Edit" — replaced by node-name which requires MeshNode with NodeType
        items.Should().HaveCount(6, "Admin should see all default menu items");
        items.Select(i => i.Label).Should().BeEquivalentTo(
            ["Create", "Import", "Files", "Threads", "Settings", "Delete"]);
    }

    [Fact(Timeout = 5000)]
    public async Task Menu_ItemsAreSortedByOrder()
    {
        // Seed Admin so we get all items for sorting verification
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync(TestUserId, "Admin", NodePath, "system",
            TestContext.Current.CancellationToken);

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(3.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        var items = await FetchMenuItemsAsync(client, nodeAddress);

        items.Should().BeInAscendingOrder(i => i.Order,
            "menu items should be sorted by Order");
    }

    [Fact(Timeout = 5000)]
    public async Task Menu_ImportAreaIsImportMeshNodes()
    {
        // Seed Editor to get Import item (requires Create permission)
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync(TestUserId, "Editor", NodePath, "system",
            TestContext.Current.CancellationToken);

        var client = GetClientWithUser();
        var nodeAddress = new Address(NodePath);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        pingCts.CancelAfter(3.Seconds());
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            pingCts.Token);

        var items = await FetchMenuItemsAsync(client, nodeAddress);

        var importItem = items.FirstOrDefault(i => i.Label == "Import");
        importItem.Should().NotBeNull("Import menu item should exist for Editor");
        importItem!.Area.Should().Be(MeshNodeLayoutAreas.ImportMeshNodesArea,
            "Import should navigate to ImportMeshNodes area, not $Import");
    }

    [Fact(Timeout = 5000)]
    public async Task StaticRoles_AppearInNodeTypeRoleQuery()
    {
        // Static built-in roles should appear when querying with nodeType:Role
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var roles = await meshQuery
            .QueryAsync<MeshNode>($"path:{NodePath} nodeType:Role scope:ancestorsAndSelf")
            .ToListAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Roles returned: {roles.Count}");
        foreach (var r in roles)
            Output.WriteLine($"  {r.Path} ({r.Name})");

        roles.Should().HaveCount(4,
            "built-in roles (Admin, Editor, Viewer, Commenter) should appear as static nodes");
        roles.Select(r => r.Name).Should().Contain(
            ["Admin", "Editor", "Viewer", "Commenter"]);
    }

    [Fact(Timeout = 5000)]
    public async Task StaticRoles_NotIncludedInGenericChildrenQuery()
    {
        // Static roles should NOT appear in unfiltered children queries
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var children = await meshQuery
            .QueryAsync<MeshNode>($"path:{NodePath} scope:children")
            .ToListAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Children returned: {children.Count}");
        foreach (var c in children)
            Output.WriteLine($"  {c.Path} ({c.Name})");

        children.Select(c => c.Name).Should().NotContain(
            ["Administrator", "Editor", "Viewer", "Commenter"],
            "static roles should not leak into generic children queries");
    }
}
