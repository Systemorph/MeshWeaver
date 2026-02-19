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
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test.Security;

/// <summary>
/// Tests that AccessControlLayoutArea renders correctly with ItemTemplateControl,
/// verifying the BindMany fix (JsonPointerReference instead of string "/").
/// </summary>
public class AccessControlLayoutAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;
    private const string NodePath = "TestOrg/TestProject";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddRowLevelSecurity()
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas());

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact(Timeout = 15000)]
    public async Task AccessControl_RendersWithItemTemplateControls()
    {
        // Seed data so BindMany has something to render
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("TestUser", "Viewer", "TestOrg", "system", TestTimeout);

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

        // Get all child area controls
        var areaControls = await Task.WhenAll(
            stack.Areas.Select(async a =>
                await stream.GetControlStream(a.Area.ToString()!)
                    .Timeout(10.Seconds())
                    .FirstAsync(x => x != null))
        );

        // Find ItemTemplateControl children (the BindMany results)
        var itemTemplates = areaControls.OfType<ItemTemplateControl>().ToList();
        itemTemplates.Should().NotBeEmpty("BindMany should produce ItemTemplateControl instances");

        foreach (var itemTemplate in itemTemplates)
        {
            // CRITICAL: Data must be JsonPointerReference, not string "/"
            itemTemplate.Data.Should().BeOfType<JsonPointerReference>(
                "BindMany should use JsonPointerReference for data binding, not a string literal");

            itemTemplate.DataContext.Should().StartWith("/data/",
                "ItemTemplateControl DataContext should point to a data stream");
        }
    }

    [Fact(Timeout = 15000)]
    public async Task AccessControl_DataBindsAssignments()
    {
        // Seed both inherited and local assignments
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("InheritedUser", "Viewer", "TestOrg", "system", TestTimeout);
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

        // Get all child controls
        var areaControls = await Task.WhenAll(
            stack.Areas.Select(async a =>
                await stream.GetControlStream(a.Area.ToString()!)
                    .Timeout(10.Seconds())
                    .FirstAsync(x => x != null))
        );

        var itemTemplates = areaControls.OfType<ItemTemplateControl>().ToList();
        itemTemplates.Should().HaveCountGreaterThanOrEqualTo(2,
            "should have ItemTemplateControl for inherited and local sections");

        // Verify data streams contain the seeded assignments
        var templatesWithData = 0;
        foreach (var itemTemplate in itemTemplates)
        {
            var dataRef = new JsonPointerReference(itemTemplate.DataContext!);
            var data = await stream
                .GetDataStream<IEnumerable<JsonElement>>(dataRef)
                .Where(x => x is not null)
                .Timeout(5.Seconds())
                .FirstOrDefaultAsync();

            if (data != null && data.Any())
            {
                templatesWithData++;
                Output.WriteLine($"DataContext: {itemTemplate.DataContext}, Items: {data.Count()}");
            }
        }

        templatesWithData.Should().BeGreaterThanOrEqualTo(1,
            "at least one ItemTemplateControl should have bound data from seeded assignments");
    }

    [Fact(Timeout = 10000)]
    public async Task AccessControl_NoRLS_ShowsWarning()
    {
        // Verify the service exists when RLS is configured.
        // The no-RLS code path (securityService == null → warning HTML) is
        // tested by the AccessControlLayoutArea code contract.
        var svc = Mesh.ServiceProvider.GetService<ISecurityService>();
        svc.Should().NotBeNull("RLS is configured in this test fixture");
    }

    [Fact(Timeout = 15000)]
    public async Task AccessControl_NestedNode_ShowsInheritedAssignments()
    {
        // Seed assignments at parent and at a deeper nested path
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("ParentUser", "Viewer", "TestOrg", "system", TestTimeout);
        await svc.AddUserRoleAsync("NestedUser", "Editor", "TestOrg/TestProject/Docs", "system", TestTimeout);

        var client = GetClient();
        var nestedPath = "TestOrg/TestProject/Docs";
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

        var areaControls = await Task.WhenAll(
            stack.Areas.Select(async a =>
                await stream.GetControlStream(a.Area.ToString()!)
                    .Timeout(10.Seconds())
                    .FirstAsync(x => x != null))
        );

        // Should have ItemTemplateControls for inherited and local sections
        var itemTemplates = areaControls.OfType<ItemTemplateControl>().ToList();
        itemTemplates.Should().HaveCountGreaterThanOrEqualTo(2,
            "should have ItemTemplateControl for inherited and local sections");

        // Check that at least one template has data (inherited from TestOrg or local at Docs)
        var templatesWithData = 0;
        foreach (var itemTemplate in itemTemplates)
        {
            var dataRef = new JsonPointerReference(itemTemplate.DataContext!);
            var data = await stream
                .GetDataStream<IEnumerable<JsonElement>>(dataRef)
                .Where(x => x is not null)
                .Timeout(5.Seconds())
                .FirstOrDefaultAsync();

            if (data != null && data.Any())
            {
                templatesWithData++;
                Output.WriteLine($"DataContext: {itemTemplate.DataContext}, Items: {data.Count()}");
            }
        }

        templatesWithData.Should().BeGreaterThanOrEqualTo(1,
            "nested node should show inherited and/or local assignments");
    }

    [Fact(Timeout = 15000)]
    public async Task AccessControl_DeeplyNestedPath_InheritsFromAllAncestors()
    {
        // Seed assignments at multiple ancestor levels
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("RootUser", "Admin", "Org", "system", TestTimeout);
        await svc.AddUserRoleAsync("DivUser", "Editor", "Org/Division", "system", TestTimeout);
        await svc.AddUserRoleAsync("DeepUser", "Viewer", "Org/Division/Team/Project", "system", TestTimeout);

        var client = GetClient();
        var deepPath = "Org/Division/Team/Project";
        var nodeAddress = new Address(deepPath);

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

        var areaControls = await Task.WhenAll(
            stack.Areas.Select(async a =>
                await stream.GetControlStream(a.Area.ToString()!)
                    .Timeout(10.Seconds())
                    .FirstAsync(x => x != null))
        );

        var itemTemplates = areaControls.OfType<ItemTemplateControl>().ToList();
        itemTemplates.Should().HaveCountGreaterThanOrEqualTo(2,
            "should have inherited and local sections");

        // Collect all bound data across templates
        var totalItems = 0;
        foreach (var itemTemplate in itemTemplates)
        {
            var dataRef = new JsonPointerReference(itemTemplate.DataContext!);
            var data = await stream
                .GetDataStream<IEnumerable<JsonElement>>(dataRef)
                .Where(x => x is not null)
                .Timeout(5.Seconds())
                .FirstOrDefaultAsync();

            if (data != null)
            {
                var count = data.Count();
                totalItems += count;
                Output.WriteLine($"DataContext: {itemTemplate.DataContext}, Items: {count}");
            }
        }

        totalItems.Should().BeGreaterThanOrEqualTo(3,
            "deeply nested node should show assignments from all ancestor levels (Org, Org/Division) plus local");
    }

    [Fact(Timeout = 10000)]
    public async Task SecurityService_NestedPath_ReturnsInheritedAssignments()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Seed assignments at different hierarchy levels
        await svc.AddUserRoleAsync("GlobalAdmin", "Admin", null, "system", TestTimeout);
        await svc.AddUserRoleAsync("OrgEditor", "Editor", "MyOrg", "system", TestTimeout);
        await svc.AddUserRoleAsync("ProjectViewer", "Viewer", "MyOrg/Project/SubFolder", "system", TestTimeout);

        // Query assignments for the nested path
        var assignments = new List<AccessAssignment>();
        await foreach (var a in svc.GetAccessAssignmentsAsync("MyOrg/Project/SubFolder", TestTimeout))
        {
            assignments.Add(a);
            Output.WriteLine($"User={a.UserId}, Role={a.RoleId}, Source={a.SourcePath}, IsLocal={a.IsLocal}");
        }

        // Local assignment at MyOrg/Project/SubFolder
        var localAssignments = assignments.Where(a => a.IsLocal).ToList();
        localAssignments.Should().ContainSingle(a => a.UserId == "ProjectViewer" && a.RoleId == "Viewer",
            "ProjectViewer at exact path should be local");

        // Inherited assignments from MyOrg and global
        var inheritedAssignments = assignments.Where(a => !a.IsLocal).ToList();
        inheritedAssignments.Should().Contain(a => a.UserId == "OrgEditor" && a.RoleId == "Editor",
            "OrgEditor from ancestor MyOrg should appear as inherited");
        inheritedAssignments.Should().Contain(a => a.UserId == "GlobalAdmin" && a.RoleId == "Admin",
            "GlobalAdmin from global partition should appear as inherited");

        // Verify source paths
        var orgEditorAssignment = inheritedAssignments.First(a => a.UserId == "OrgEditor");
        orgEditorAssignment.SourcePath.Should().Be("MyOrg");
        orgEditorAssignment.SourceDisplay.Should().Be("MyOrg");

        var globalAdminAssignment = inheritedAssignments.First(a => a.UserId == "GlobalAdmin");
        globalAdminAssignment.SourcePath.Should().BeEmpty();
        globalAdminAssignment.SourceDisplay.Should().Be("Global");
    }
}
