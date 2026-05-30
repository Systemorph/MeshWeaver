using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for AccessAssignmentLayoutAreas â€” verifies that workspace.RequestChange
/// correctly updates the MeshNode stream, enabling reactive UI updates
/// for ToggleDenied, RemoveRole, and AddRole operations.
/// </summary>
public class AccessAssignmentLayoutAreasTest(ITestOutputHelper output) : HubTestBase(output)
{
    private InMemoryStorageAdapter _persistence = null!;
    private static readonly JsonSerializerOptions JsonOptions = new();

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
    {
        _persistence = new InMemoryStorageAdapter();

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

    private string GetHubPath(string hostId = "1") => $"{HostType}/{hostId}";

    private MeshNode SetupAssignmentNode(string hubPath, AccessAssignment assignment)
    {
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = $"{assignment.DisplayName ?? "Test"} Access",
            NodeType = "AccessAssignment",
            Content = assignment
        };
        _persistence.SaveNode(node, JsonOptions).Should().Emit();
        return node;
    }

    // ============================================================
    // Data-layer tests: verify workspace.RequestChange updates stream
    // ============================================================

    [HubFact]
    public void ToggleDenied_UpdatesNodeInStream()
    {
        var hubPath = GetHubPath();
        var assignment = new AccessAssignment
        {
            AccessObject = "User/alice",
            DisplayName = "Alice",
            Roles = [new RoleAssignment { Role = "Editor", Denied = false }]
        };
        SetupAssignmentNode(hubPath, assignment);

        var host = GetHost();
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>()!;

        var initialNodes = nodeStream
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true)!;

        var node = initialNodes!.First();
        var initial = AccessControlLayoutArea.DeserializeAssignment(node)!;
        initial.Roles[0].Denied.Should().BeFalse("initial state should be not-denied");

        // Act â€” toggle denied
        var roles = initial.Roles.ToList();
        roles[0] = roles[0] with { Denied = true };
        var updatedNode = node with { Content = initial with { Roles = roles } };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Assert â€” stream should reflect the change
        var updatedNodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items =>
            {
                var n = items?.FirstOrDefault();
                if (n == null) return false;
                var a = AccessControlLayoutArea.DeserializeAssignment(n);
                return a?.Roles.FirstOrDefault()?.Denied == true;
            });

        var result = AccessControlLayoutArea.DeserializeAssignment(updatedNodes!.First())!;
        result.Roles[0].Denied.Should().BeTrue("Denied flag should have been toggled to true");
        result.Roles[0].Role.Should().Be("Editor");
    }

    [HubFact]
    public void ToggleDenied_TogglesBackToFalse()
    {
        var hubPath = GetHubPath("toggle-back");
        var assignment = new AccessAssignment
        {
            AccessObject = "User/bob",
            DisplayName = "Bob",
            Roles = [new RoleAssignment { Role = "Viewer", Denied = true }]
        };
        SetupAssignmentNode(hubPath, assignment);

        var host = Mesh.GetHostedHub(new Address(HostType, "toggle-back"), ConfigureHost);
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>();
        var initialNodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true);

        var node = initialNodes!.First();
        var initial = AccessControlLayoutArea.DeserializeAssignment(node)!;
        initial.Roles[0].Denied.Should().BeTrue("initial state should be denied");

        var roles = initial.Roles.ToList();
        roles[0] = roles[0] with { Denied = false };
        var updatedNode = node with { Content = initial with { Roles = roles } };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        var updatedNodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items =>
            {
                var n = items?.FirstOrDefault();
                if (n == null) return false;
                var a = AccessControlLayoutArea.DeserializeAssignment(n);
                return a?.Roles.FirstOrDefault()?.Denied == false;
            });

        var result = AccessControlLayoutArea.DeserializeAssignment(updatedNodes!.First())!;
        result.Roles[0].Denied.Should().BeFalse();
    }

    [HubFact]
    public void RemoveRole_RemovesRoleFromAssignment()
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
        SetupAssignmentNode(hubPath, assignment);

        var host = Mesh.GetHostedHub(new Address(HostType, "remove-role"), ConfigureHost);
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>();
        var initialNodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true);

        var node = initialNodes!.First();
        var initial = AccessControlLayoutArea.DeserializeAssignment(node)!;
        initial.Roles.Should().HaveCount(2);

        var roles = initial.Roles.ToList();
        roles.RemoveAt(0);
        var updatedNode = node with { Content = initial with { Roles = roles } };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        var updatedNodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items =>
            {
                var n = items?.FirstOrDefault();
                if (n == null) return false;
                var a = AccessControlLayoutArea.DeserializeAssignment(n);
                return a?.Roles.Count == 1;
            });

        var result = AccessControlLayoutArea.DeserializeAssignment(updatedNodes!.First())!;
        result.Roles.Should().HaveCount(1);
        result.Roles[0].Role.Should().Be("Editor");
    }

    [HubFact]
    public void RemoveLastRole_DeletesNode()
    {
        var hubPath = GetHubPath("delete-node");
        var assignment = new AccessAssignment
        {
            AccessObject = "User/dave",
            DisplayName = "Dave",
            Roles = [new RoleAssignment { Role = "Viewer", Denied = false }]
        };
        SetupAssignmentNode(hubPath, assignment);

        var host = Mesh.GetHostedHub(new Address(HostType, "delete-node"), ConfigureHost);
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>();
        var initialNodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true);

        initialNodes.Should().HaveCount(1);
        var node = initialNodes!.First();

        workspace.RequestChange(new DataChangeRequest().WithDeletions(node), null, null);

        var updatedNodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items == null || items.Length == 0);

        (updatedNodes?.Length ?? 0).Should().Be(0);
    }

    [HubFact]
    public void AddRole_AddsRoleToAssignment()
    {
        var hubPath = GetHubPath("add-role");
        var assignment = new AccessAssignment
        {
            AccessObject = "User/eve",
            DisplayName = "Eve",
            Roles = [new RoleAssignment { Role = "Viewer", Denied = false }]
        };
        SetupAssignmentNode(hubPath, assignment);

        var host = Mesh.GetHostedHub(new Address(HostType, "add-role"), ConfigureHost);
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>();
        var initialNodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true);

        var node = initialNodes!.First();
        var initial = AccessControlLayoutArea.DeserializeAssignment(node)!;
        initial.Roles.Should().HaveCount(1);

        var roles = initial.Roles.ToList();
        roles.Add(new RoleAssignment { Role = "Editor", Denied = false });
        var updatedNode = node with { Content = initial with { Roles = roles } };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        var updatedNodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items =>
            {
                var n = items?.FirstOrDefault();
                if (n == null) return false;
                var a = AccessControlLayoutArea.DeserializeAssignment(n);
                return a?.Roles.Count == 2;
            });

        var result = AccessControlLayoutArea.DeserializeAssignment(updatedNodes!.First())!;
        result.Roles.Should().HaveCount(2);
        result.Roles[0].Role.Should().Be("Viewer");
        result.Roles[1].Role.Should().Be("Editor");
    }

    [HubFact]
    public void ToggleDenied_WithMultipleRoles_OnlyTogglesSpecifiedRole()
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
        SetupAssignmentNode(hubPath, assignment);

        var host = Mesh.GetHostedHub(new Address(HostType, "multi-toggle"), ConfigureHost);
        var workspace = host.GetWorkspace();

        var nodeStream = workspace.GetStream<MeshNode>();
        var initialNodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true);

        var node = initialNodes!.First();
        var initial = AccessControlLayoutArea.DeserializeAssignment(node)!;

        var roles = initial.Roles.ToList();
        roles[1] = roles[1] with { Denied = true };
        var updatedNode = node with { Content = initial with { Roles = roles } };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        var updatedNodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items =>
            {
                var n = items?.FirstOrDefault();
                if (n == null) return false;
                var a = AccessControlLayoutArea.DeserializeAssignment(n);
                return a?.Roles[1].Denied == true;
            });

        var result = AccessControlLayoutArea.DeserializeAssignment(updatedNodes!.First())!;
        result.Roles[0].Denied.Should().BeFalse("Admin should remain unchanged");
        result.Roles[1].Denied.Should().BeTrue("Editor should now be denied");
        result.Roles[2].Denied.Should().BeTrue("Viewer should remain denied");
    }

    // NOTE: Thumbnail layout rendering + click tests have been moved to
    // MeshWeaver.AccessControl.Test (requires MonolithMeshTestBase for IMeshService resolution)
}
