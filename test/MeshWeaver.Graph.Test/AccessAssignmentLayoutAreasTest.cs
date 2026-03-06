using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for AccessAssignmentLayoutAreas — verifies that workspace.RequestChange
/// correctly updates the MeshNode stream, enabling reactive UI updates
/// for ToggleDenied, RemoveRole, and AddRole operations.
/// </summary>
public class AccessAssignmentLayoutAreasTest(ITestOutputHelper output) : HubTestBase(output)
{
    private InMemoryPersistenceService _persistence = null!;
    private static readonly JsonSerializerOptions JsonOptions = new();

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
    {
        _persistence = new InMemoryPersistenceService();

        return conf
            .WithServices(services => services
                .AddInMemoryPersistence(_persistence))
            .WithRoutes(forward => forward
                .RouteAddressToHostedHub(HostType, ConfigureHost)
                .RouteAddressToHostedHub(ClientType, ConfigureClient));
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddMeshDataSource(ds => ds.WithContentType<AccessAssignment>())
            .AddAccessAssignmentViews();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private string GetHubPath(string hostId = "1") => $"{HostType}/{hostId}";

    private async Task<MeshNode> SetupAssignmentNode(string hubPath, AccessAssignment assignment)
    {
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = $"{assignment.DisplayName ?? "Test"} Access",
            NodeType = "AccessAssignment",
            Content = assignment
        };
        await _persistence.SaveNodeAsync(node, JsonOptions);
        return node;
    }

    // ============================================================
    // Data-layer tests: verify workspace.RequestChange updates stream
    // ============================================================

    [HubFact]
    public async Task ToggleDenied_UpdatesNodeInStream()
    {
        var hubPath = GetHubPath();
        var assignment = new AccessAssignment
        {
            AccessObject = "User/alice",
            DisplayName = "Alice",
            Roles = [new RoleAssignment { Role = "Editor", Denied = false }]
        };
        await SetupAssignmentNode(hubPath, assignment);

        var host = GetHost();
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>();
        nodeStream.Should().NotBeNull();

        var initialNodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        var node = initialNodes.First();
        var initial = AccessControlLayoutArea.DeserializeAssignment(node)!;
        initial.Roles[0].Denied.Should().BeFalse("initial state should be not-denied");

        // Act — toggle denied
        var roles = initial.Roles.ToList();
        roles[0] = roles[0] with { Denied = true };
        var updatedNode = node with { Content = initial with { Roles = roles } };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Assert — stream should reflect the change
        var updatedNodes = await nodeStream!
            .Where(items =>
            {
                var n = items?.FirstOrDefault();
                if (n == null) return false;
                var a = AccessControlLayoutArea.DeserializeAssignment(n);
                return a?.Roles.FirstOrDefault()?.Denied == true;
            })
            .Timeout(5.Seconds())
            .FirstAsync();

        var result = AccessControlLayoutArea.DeserializeAssignment(updatedNodes.First())!;
        result.Roles[0].Denied.Should().BeTrue("Denied flag should have been toggled to true");
        result.Roles[0].Role.Should().Be("Editor");
    }

    [HubFact]
    public async Task ToggleDenied_TogglesBackToFalse()
    {
        var hubPath = GetHubPath("toggle-back");
        var assignment = new AccessAssignment
        {
            AccessObject = "User/bob",
            DisplayName = "Bob",
            Roles = [new RoleAssignment { Role = "Viewer", Denied = true }]
        };
        await SetupAssignmentNode(hubPath, assignment);

        var host = Mesh.GetHostedHub(new Address(HostType, "toggle-back"), ConfigureHost);
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>();
        var initialNodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        var node = initialNodes.First();
        var initial = AccessControlLayoutArea.DeserializeAssignment(node)!;
        initial.Roles[0].Denied.Should().BeTrue("initial state should be denied");

        var roles = initial.Roles.ToList();
        roles[0] = roles[0] with { Denied = false };
        var updatedNode = node with { Content = initial with { Roles = roles } };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        var updatedNodes = await nodeStream!
            .Where(items =>
            {
                var n = items?.FirstOrDefault();
                if (n == null) return false;
                var a = AccessControlLayoutArea.DeserializeAssignment(n);
                return a?.Roles.FirstOrDefault()?.Denied == false;
            })
            .Timeout(5.Seconds())
            .FirstAsync();

        var result = AccessControlLayoutArea.DeserializeAssignment(updatedNodes.First())!;
        result.Roles[0].Denied.Should().BeFalse();
    }

    [HubFact]
    public async Task RemoveRole_RemovesRoleFromAssignment()
    {
        var hubPath = GetHubPath("remove-role");
        var assignment = new AccessAssignment
        {
            AccessObject = "User/charlie",
            DisplayName = "Charlie",
            Roles =
            [
                new RoleAssignment { Role = "Admin", Denied = false },
                new RoleAssignment { Role = "Editor", Denied = false }
            ]
        };
        await SetupAssignmentNode(hubPath, assignment);

        var host = Mesh.GetHostedHub(new Address(HostType, "remove-role"), ConfigureHost);
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>();
        var initialNodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        var node = initialNodes.First();
        var initial = AccessControlLayoutArea.DeserializeAssignment(node)!;
        initial.Roles.Should().HaveCount(2);

        var roles = initial.Roles.ToList();
        roles.RemoveAt(0);
        var updatedNode = node with { Content = initial with { Roles = roles } };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        var updatedNodes = await nodeStream!
            .Where(items =>
            {
                var n = items?.FirstOrDefault();
                if (n == null) return false;
                var a = AccessControlLayoutArea.DeserializeAssignment(n);
                return a?.Roles.Count == 1;
            })
            .Timeout(5.Seconds())
            .FirstAsync();

        var result = AccessControlLayoutArea.DeserializeAssignment(updatedNodes.First())!;
        result.Roles.Should().HaveCount(1);
        result.Roles[0].Role.Should().Be("Editor");
    }

    [HubFact]
    public async Task RemoveLastRole_DeletesNode()
    {
        var hubPath = GetHubPath("delete-node");
        var assignment = new AccessAssignment
        {
            AccessObject = "User/dave",
            DisplayName = "Dave",
            Roles = [new RoleAssignment { Role = "Viewer", Denied = false }]
        };
        await SetupAssignmentNode(hubPath, assignment);

        var host = Mesh.GetHostedHub(new Address(HostType, "delete-node"), ConfigureHost);
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>();
        var initialNodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        initialNodes.Should().HaveCount(1);
        var node = initialNodes.First();

        workspace.RequestChange(new DataChangeRequest().WithDeletions(node), null, null);

        var updatedNodes = await nodeStream!
            .Where(items => items == null || items.Length == 0)
            .Timeout(5.Seconds())
            .FirstAsync();

        (updatedNodes?.Length ?? 0).Should().Be(0);
    }

    [HubFact]
    public async Task AddRole_AddsRoleToAssignment()
    {
        var hubPath = GetHubPath("add-role");
        var assignment = new AccessAssignment
        {
            AccessObject = "User/eve",
            DisplayName = "Eve",
            Roles = [new RoleAssignment { Role = "Viewer", Denied = false }]
        };
        await SetupAssignmentNode(hubPath, assignment);

        var host = Mesh.GetHostedHub(new Address(HostType, "add-role"), ConfigureHost);
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>();
        var initialNodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        var node = initialNodes.First();
        var initial = AccessControlLayoutArea.DeserializeAssignment(node)!;
        initial.Roles.Should().HaveCount(1);

        var roles = initial.Roles.ToList();
        roles.Add(new RoleAssignment { Role = "Editor", Denied = false });
        var updatedNode = node with { Content = initial with { Roles = roles } };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        var updatedNodes = await nodeStream!
            .Where(items =>
            {
                var n = items?.FirstOrDefault();
                if (n == null) return false;
                var a = AccessControlLayoutArea.DeserializeAssignment(n);
                return a?.Roles.Count == 2;
            })
            .Timeout(5.Seconds())
            .FirstAsync();

        var result = AccessControlLayoutArea.DeserializeAssignment(updatedNodes.First())!;
        result.Roles.Should().HaveCount(2);
        result.Roles[0].Role.Should().Be("Viewer");
        result.Roles[1].Role.Should().Be("Editor");
    }

    [HubFact]
    public async Task ToggleDenied_WithMultipleRoles_OnlyTogglesSpecifiedRole()
    {
        var hubPath = GetHubPath("multi-toggle");
        var assignment = new AccessAssignment
        {
            AccessObject = "User/frank",
            DisplayName = "Frank",
            Roles =
            [
                new RoleAssignment { Role = "Admin", Denied = false },
                new RoleAssignment { Role = "Editor", Denied = false },
                new RoleAssignment { Role = "Viewer", Denied = true }
            ]
        };
        await SetupAssignmentNode(hubPath, assignment);

        var host = Mesh.GetHostedHub(new Address(HostType, "multi-toggle"), ConfigureHost);
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>();
        var initialNodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        var node = initialNodes.First();
        var initial = AccessControlLayoutArea.DeserializeAssignment(node)!;

        var roles = initial.Roles.ToList();
        roles[1] = roles[1] with { Denied = true };
        var updatedNode = node with { Content = initial with { Roles = roles } };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        var updatedNodes = await nodeStream!
            .Where(items =>
            {
                var n = items?.FirstOrDefault();
                if (n == null) return false;
                var a = AccessControlLayoutArea.DeserializeAssignment(n);
                return a?.Roles[1].Denied == true;
            })
            .Timeout(5.Seconds())
            .FirstAsync();

        var result = AccessControlLayoutArea.DeserializeAssignment(updatedNodes.First())!;
        result.Roles[0].Denied.Should().BeFalse("Admin should remain unchanged");
        result.Roles[1].Denied.Should().BeTrue("Editor should now be denied");
        result.Roles[2].Denied.Should().BeTrue("Viewer should remain denied");
    }

    // ============================================================
    // Layout rendering + click tests: verify Thumbnail re-renders
    // ============================================================

    [HubFact]
    public async Task Thumbnail_RendersStackControl()
    {
        var hubPath = GetHubPath("thumb-render");
        var assignment = new AccessAssignment
        {
            AccessObject = "User/grace",
            DisplayName = "Grace",
            Roles = [new RoleAssignment { Role = "Editor", Denied = false }]
        };
        await SetupAssignmentNode(hubPath, assignment);

        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ThumbnailArea);
        var hub = GetClient();
        var hostAddress = new Address(HostType, "thumb-render");

        // Force-create the host to load data
        Mesh.GetHostedHub(hostAddress, ConfigureHost);

        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress, reference);

        // Get initial control — should be a Stack (the card)
        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        control.Should().BeOfType<StackControl>("Thumbnail should render a StackControl");
    }

    [HubFact]
    public async Task Thumbnail_ClickRoleChip_TogglesStrikethrough()
    {
        var hubPath = GetHubPath("thumb-click");
        var assignment = new AccessAssignment
        {
            AccessObject = "User/hank",
            DisplayName = "Hank",
            Roles = [new RoleAssignment { Role = "Editor", Denied = false }]
        };
        await SetupAssignmentNode(hubPath, assignment);

        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ThumbnailArea);
        var hub = GetClient();
        var hostAddress = new Address(HostType, "thumb-click");

        // Create host
        Mesh.GetHostedHub(hostAddress, ConfigureHost);

        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress, reference);

        // Wait for initial render
        var initialControl = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        var outerStack = initialControl.Should().BeOfType<StackControl>().Subject;

        // Find the role chip button — walk the area tree
        // Card structure: topRow (icon + name + "+" button), chipsRow (role buttons + × buttons)
        // The chipsRow is the second area in the outer stack
        var outerAreas = outerStack.Areas.ToList();
        outerAreas.Count.Should().BeGreaterThanOrEqualTo(2, "should have topRow + chipsRow");

        // Get the chips row area
        var chipsRowAreaName = outerAreas[1].Area?.ToString();
        chipsRowAreaName.Should().NotBeNullOrEmpty("chipsRow area should have a name");

        var chipsRowControl = await stream
            .GetControlStream(chipsRowAreaName!)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);

        var chipsRow = chipsRowControl.Should().BeOfType<StackControl>().Subject;
        var chipAreas = chipsRow.Areas.ToList();
        chipAreas.Should().NotBeEmpty("should have at least one role chip");

        // First chip area is the role toggle button
        var roleChipArea = chipAreas[0].Area?.ToString();
        roleChipArea.Should().NotBeNullOrEmpty();

        // Verify initial button exists and has no line-through
        var initialChip = await stream
            .GetControlStream(roleChipArea!)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
        initialChip.Should().BeOfType<ButtonControl>();
        var initialStyle = ((ButtonControl)initialChip!).Style?.ToString() ?? "";
        initialStyle.Should().NotContain("line-through", "initial state should NOT have strikethrough");

        // Click the role chip to toggle denied
        hub.Post(new ClickedEvent(roleChipArea!, stream.StreamId),
            o => o.WithTarget(hostAddress));

        // Wait for re-render — the button style should now have line-through
        var updatedChip = await stream
            .GetControlStream(roleChipArea!)
            .Where(c =>
            {
                if (c is not ButtonControl btn) return false;
                var style = btn.Style?.ToString() ?? "";
                return style.Contains("line-through");
            })
            .Timeout(5.Seconds())
            .FirstAsync();

        updatedChip.Should().NotBeNull("role chip should re-render with strikethrough after toggle");
        var updatedStyle = ((ButtonControl)updatedChip!).Style?.ToString() ?? "";
        updatedStyle.Should().Contain("line-through", "denied role should show strikethrough");
    }

    [HubFact]
    public async Task Thumbnail_ClickRemoveRole_RemovesChip()
    {
        var hubPath = GetHubPath("thumb-remove");
        var assignment = new AccessAssignment
        {
            AccessObject = "User/iris",
            DisplayName = "Iris",
            Roles =
            [
                new RoleAssignment { Role = "Admin", Denied = false },
                new RoleAssignment { Role = "Editor", Denied = false }
            ]
        };
        await SetupAssignmentNode(hubPath, assignment);

        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ThumbnailArea);
        var hub = GetClient();
        var hostAddress = new Address(HostType, "thumb-remove");

        Mesh.GetHostedHub(hostAddress, ConfigureHost);

        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress, reference);

        // Wait for initial render
        var initialControl = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        var outerStack = initialControl.Should().BeOfType<StackControl>().Subject;
        var outerAreas = outerStack.Areas.ToList();
        outerAreas.Count.Should().BeGreaterThanOrEqualTo(2);

        // Get chips row
        var chipsRowAreaName = outerAreas[1].Area?.ToString();
        var chipsRowControl = await stream
            .GetControlStream(chipsRowAreaName!)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);

        var chipsRow = chipsRowControl.Should().BeOfType<StackControl>().Subject;
        var chipAreas = chipsRow.Areas.ToList();
        // With 2 roles, should have 4 areas: (role1, ×1, role2, ×2)
        chipAreas.Count.Should().BeGreaterThanOrEqualTo(4, "should have 2 role chips + 2 × buttons");

        // Click the × button for the first role (index 1 in the chip areas)
        var removeButtonArea = chipAreas[1].Area?.ToString();
        removeButtonArea.Should().NotBeNullOrEmpty();

        hub.Post(new ClickedEvent(removeButtonArea!, stream.StreamId),
            o => o.WithTarget(hostAddress));

        // Verify the underlying data changed — one role removed
        var hostHub = Mesh.GetHostedHub(hostAddress, ConfigureHost);
        var nodeStream = hostHub.GetWorkspace().GetStream<MeshNode>();

        var updatedNodes = await nodeStream!
            .Where(items =>
            {
                var n = items?.FirstOrDefault();
                if (n == null) return false;
                var a = AccessControlLayoutArea.DeserializeAssignment(n);
                return a?.Roles.Count == 1;
            })
            .Timeout(5.Seconds())
            .FirstAsync();

        var result = AccessControlLayoutArea.DeserializeAssignment(updatedNodes.First())!;
        result.Roles.Should().HaveCount(1);
        result.Roles[0].Role.Should().Be("Editor", "Admin should have been removed");
    }
}
