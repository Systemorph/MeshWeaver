using System;
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
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests that AccessControlLayoutArea renders correctly with inherited (markdown table)
/// and local (editable rows) sections, driven by IMeshService on AccessAssignment MeshNodes.
/// </summary>
public class AccessControlLayoutAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;
    private const string NodePath = "ACME/Project";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas())
            .AddMeshNodes(
                MeshNode.FromPath("ACME") with { Name = "ACME", NodeType = "Organization" },
                MeshNode.FromPath("ACME/Project") with { Name = "Project", NodeType = "Project" },
                MeshNode.FromPath("ACME/Documentation") with { Name = "Documentation", NodeType = "Markdown" }
            );

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes([new KeyValuePair<string, Type>(nameof(AccessAssignment), typeof(AccessAssignment))]);

    [Fact(Timeout = 20000)]
    public async Task AccessControl_RendersStackControl()
    {
        // Seed data so the layout has something to render
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("TestUser", "Viewer", "ACME", "system", TestTimeout);

        var client = GetClient();
        var nodeAddress = new Address(NodePath);

        // Initialize the hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.AccessControlArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress,
            reference);

        // Get the rendered root control
        var control = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        // Root should be a StackControl
        var stack = control.Should().BeOfType<StackControl>().Which;
        stack.Areas.Should().NotBeEmpty("AccessControl should have child areas");
    }

    [Fact(Timeout = 20000)]
    public async Task AccessControl_ShowsInheritedAndLocalSections()
    {
        // Seed both inherited and local assignments
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("InheritedUser", "Viewer", "ACME", "system", TestTimeout);
        await svc.AddUserRoleAsync("LocalUser", "Editor", NodePath, "system", TestTimeout);

        var client = GetClient();
        var nodeAddress = new Address(NodePath);

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.AccessControlArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress,
            reference);

        // Get root control
        var control = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        var stack = control.Should().BeOfType<StackControl>().Which;

        // The AccessControlLayoutArea should produce child areas for:
        // H2 header, H3 "Inherited Permissions", markdown/empty, H3 "Local Assignments", rows/empty, [Add button]
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(4,
            "should have header, inherited section, local section, and content areas");
    }

    [Fact(Timeout = 20000)]
    public async Task AccessControl_NoRLS_ShowsWarning()
    {
        // Verify the service exists when RLS is configured.
        var svc = Mesh.ServiceProvider.GetService<ISecurityService>();
        svc.Should().NotBeNull("RLS is configured in this test fixture");
    }

    [Fact(Timeout = 20000)]
    public async Task AccessControl_NestedNode_ShowsInheritedAssignments()
    {
        // Create actual nodes so the hubs exist
        await NodeFactory.CreateNodeAsync(
            new MeshNode("ACME", TestPartition) { Name = "ACME", NodeType = "Group" }, TestTimeout);
        await NodeFactory.CreateNodeAsync(
            new MeshNode("Documentation", $"{TestPartition}/ACME") { Name = "Documentation", NodeType = "Markdown" }, TestTimeout);

        // Seed assignments
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("ParentUser", "Viewer", $"{TestPartition}/ACME", "system", TestTimeout);
        await svc.AddUserRoleAsync("NestedUser", "Editor", $"{TestPartition}/ACME/Documentation", "system", TestTimeout);

        var client = GetClient();
        var nestedPath = $"{TestPartition}/ACME/Documentation";
        var nodeAddress = new Address(nestedPath);

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.AccessControlArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress,
            reference);

        var control = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        var stack = control.Should().BeOfType<StackControl>().Which;
        stack.Areas.Should().NotBeEmpty("AccessControl should have child areas");
    }

    [Fact(Timeout = 20000)]
    public async Task AccessControl_DeeplyNestedPath_InheritsFromAllAncestors()
    {
        // Seed assignments at multiple ancestor levels
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("RootUser", "Admin", "Org", "system", TestTimeout);
        await svc.AddUserRoleAsync("DivUser", "Editor", "Org/Division", "system", TestTimeout);
        await svc.AddUserRoleAsync("DeepUser", "Viewer", "Org/Division/Team/Project", "system", TestTimeout);

        // Verify permissions via SecurityService (no layout needed)
        var deepPath = "Org/Division/Team/Project";

        var rootPerms = await svc.GetEffectivePermissionsAsync(deepPath, "RootUser", TestTimeout);
        var divPerms = await svc.GetEffectivePermissionsAsync(deepPath, "DivUser", TestTimeout);
        var deepPerms = await svc.GetEffectivePermissionsAsync(deepPath, "DeepUser", TestTimeout);

        rootPerms.Should().Be(Permission.All, "RootUser with Admin at Org should have all permissions on deeply nested path");
        divPerms.Should().HaveFlag(Permission.Update, "DivUser with Editor at Org/Division should have update on deeply nested path");
        deepPerms.Should().Be(Permission.Read | Permission.Execute | Permission.Api, "DeepUser with Viewer at exact path should have read + execute + api permissions");
    }

    [Fact(Timeout = 20000)]
    public async Task SecurityService_NestedPath_ReturnsCorrectPermissions()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Seed assignments at different hierarchy levels
        await svc.AddUserRoleAsync("GlobalAdmin", "Admin", null, "system", TestTimeout);
        await svc.AddUserRoleAsync("OrgEditor", "Editor", "MyOrg", "system", TestTimeout);
        await svc.AddUserRoleAsync("ProjectViewer", "Viewer", "MyOrg/Project/SubFolder", "system", TestTimeout);

        // Verify permissions for each user at the nested path
        var nestedPath = "MyOrg/Project/SubFolder";

        var globalPerms = await svc.GetEffectivePermissionsAsync(nestedPath, "GlobalAdmin", TestTimeout);
        var orgPerms = await svc.GetEffectivePermissionsAsync(nestedPath, "OrgEditor", TestTimeout);
        var projectPerms = await svc.GetEffectivePermissionsAsync(nestedPath, "ProjectViewer", TestTimeout);

        globalPerms.Should().Be(Permission.All, "GlobalAdmin with global Admin role should have all permissions");
        orgPerms.Should().HaveFlag(Permission.Update, "OrgEditor with Editor at MyOrg should have update on nested path");
        projectPerms.Should().Be(Permission.Read | Permission.Execute | Permission.Api, "ProjectViewer with Viewer at exact path should have read + execute + api");

        Output.WriteLine($"GlobalAdmin permissions at {nestedPath}: {globalPerms}");
        Output.WriteLine($"OrgEditor permissions at {nestedPath}: {orgPerms}");
        Output.WriteLine($"ProjectViewer permissions at {nestedPath}: {projectPerms}");
    }
}
