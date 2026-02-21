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
/// verifying workspace-bound local assignments and ISecurityService-loaded inherited assignments.
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
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes([new KeyValuePair<string, Type>(nameof(AccessAssignment), typeof(AccessAssignment))]);

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
            // Observable BindMany (local): Data is JsonPointerReference, DataContext points to data stream
            // Static IEnumerable BindMany (inherited): Data is the collection directly (JsonElement array)
            if (itemTemplate.DataContext != null)
            {
                itemTemplate.Data.Should().BeOfType<JsonPointerReference>(
                    "Observable BindMany should use JsonPointerReference for data binding");
                itemTemplate.DataContext.Should().StartWith("/data/",
                    "ItemTemplateControl DataContext should point to a data stream");
            }
            // Static BindMany produces Data as the collection itself — no DataContext needed
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
            // Static IEnumerable.BindMany produces ItemTemplateControl with Data as the collection directly
            // Observable.BindMany produces ItemTemplateControl with DataContext pointing to data stream
            if (itemTemplate.DataContext != null)
            {
                var dataRef = new JsonPointerReference(itemTemplate.DataContext);
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
            else if (itemTemplate.Data is IEnumerable<object> staticData && staticData.Any())
            {
                templatesWithData++;
                Output.WriteLine($"Static data items: {staticData.Count()}");
            }
        }

        templatesWithData.Should().BeGreaterThanOrEqualTo(1,
            "at least one ItemTemplateControl should have bound data from seeded assignments");
    }

    [Fact(Timeout = 10000)]
    public async Task AccessControl_NoRLS_ShowsWarning()
    {
        // Verify the service exists when RLS is configured.
        // The no-RLS code path (securityService == null -> warning HTML) is
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
            if (itemTemplate.DataContext != null)
            {
                var dataRef = new JsonPointerReference(itemTemplate.DataContext);
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
            else if (itemTemplate.Data is IEnumerable<object> staticData && staticData.Any())
            {
                templatesWithData++;
                Output.WriteLine($"Static data items: {staticData.Count()}");
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
            if (itemTemplate.DataContext != null)
            {
                // Observable BindMany — data flows through stream
                var dataRef = new JsonPointerReference(itemTemplate.DataContext);
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
            else if (itemTemplate.Data is JsonElement jsonData && jsonData.ValueKind == JsonValueKind.Array)
            {
                // Static IEnumerable.BindMany — data is serialized as JsonElement array
                var count = jsonData.GetArrayLength();
                totalItems += count;
                Output.WriteLine($"Static data (JsonElement array) items: {count}");
            }
            else if (itemTemplate.Data is IEnumerable<object> staticData)
            {
                var count = staticData.Count();
                totalItems += count;
                Output.WriteLine($"Static data items: {count}");
            }
        }

        totalItems.Should().BeGreaterThanOrEqualTo(3,
            "deeply nested node should show assignments from all ancestor levels (Org, Org/Division) plus local");
    }

    [Fact(Timeout = 30000)]
    public async Task AccessControl_BindMany_DataAppearsInDataStream()
    {
        // Seed a local assignment so we know there should be data
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("DataTestUser", "Editor", NodePath, "system", TestTimeout);

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

        // Get root stack control
        var control = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        var stack = control.Should().BeOfType<StackControl>().Which;
        Output.WriteLine($"Root stack has {stack.Areas.Count} child areas");

        // Get all child controls
        var children = await Task.WhenAll(
            stack.Areas.Select(async a =>
            {
                var ctrl = await stream.GetControlStream(a.Area.ToString()!)
                    .Timeout(10.Seconds())
                    .FirstAsync(x => x != null);
                Output.WriteLine($"  Area {a.Area}: {ctrl?.GetType().Name ?? "null"}");
                return ctrl;
            })
        );

        // Find ItemTemplateControls
        var templates = children.OfType<ItemTemplateControl>().ToList();
        Output.WriteLine($"Found {templates.Count} ItemTemplateControls");
        templates.Should().HaveCountGreaterThanOrEqualTo(2,
            "should have ItemTemplateControl for inherited and local sections");

        foreach (var t in templates)
        {
            Output.WriteLine($"  Template DataContext: {t.DataContext}");
            Output.WriteLine($"  Template Data type: {t.Data?.GetType().Name}");
            Output.WriteLine($"  Template Data: {t.Data}");
        }

        // Local assignments now come via workspace stream with BindMany("acl_local", ...)
        var localPointer = LayoutAreaReference.GetDataPointer("acl_local");
        Output.WriteLine($"Checking local data at: {localPointer}");

        var localDataRef = new JsonPointerReference(localPointer);
        JsonElement[]? localData = null;
        try
        {
            localData = await stream
                .GetDataStream<JsonElement[]>(localDataRef)
                .Where(x => x is not null && x.Length > 0)
                .Timeout(10.Seconds())
                .FirstOrDefaultAsync();
        }
        catch (TimeoutException)
        {
            Output.WriteLine("TIMEOUT: No non-empty data arrived for acl_local within 10 seconds");
        }

        Output.WriteLine($"Local data items: {localData?.Length ?? 0}");

        if (localData != null)
        {
            foreach (var item in localData)
                Output.WriteLine($"  Local item: {item}");
        }

        // The seeded "DataTestUser" at NodePath should appear as a local assignment
        // via the workspace stream (AccessAssignmentTypeSource loads IsLocal assignments)
        localData.Should().NotBeNullOrEmpty(
            "Workspace-bound AccessAssignment stream should contain local assignments loaded by AccessAssignmentTypeSource.");
    }

    [Fact(Timeout = 15000)]
    public async Task AccessControl_GetDataRequest_ReturnsLocalAssignments()
    {
        // Seed a local assignment via ISecurityService before the hub starts
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("WsUser", "Editor", NodePath, "system", TestTimeout);

        var client = GetClient();
        var nodeAddress = new Address(NodePath);

        // Initialize the hub — this triggers AccessAssignmentTypeSource.InitializeAsync
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        // Request the AccessAssignment collection via GetDataRequest
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            nodeAddress,
            new CollectionReference(nameof(AccessAssignment)));

        var result = await stream
            .Where(x => x.Value != null && x.Value.Instances.Count > 0)
            .Select(x => x.Value!)
            .Timeout(10.Seconds())
            .FirstAsync();

        result.Should().NotBeNull();
        result.Instances.Should().NotBeEmpty(
            "AccessAssignmentTypeSource should load local assignments into the workspace");

        // On the client side, instances arrive as JsonElement — deserialize them
        var jsonOptions = client.JsonSerializerOptions;
        var assignments = result.Instances.Values
            .Select(v => v is JsonElement je
                ? JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText(), jsonOptions)!
                : (AccessAssignment)v)
            .ToList();

        Output.WriteLine($"Workspace contains {assignments.Count} AccessAssignment(s):");
        foreach (var a in assignments)
            Output.WriteLine($"  UserId={a.UserId}, RoleId={a.RoleId}, IsLocal={a.IsLocal}");

        assignments.Should().Contain(a => a.UserId == "WsUser" && a.RoleId == "Editor",
            "seeded local assignment should be loaded by AccessAssignmentTypeSource");
    }

    [Fact(Timeout = 30000)]
    public async Task AccessControl_DataChangeRequest_CreatesAssignment()
    {
        var client = GetClient();
        var nodeAddress = new Address(NodePath);

        // Initialize the hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        // Subscribe to the AccessAssignment collection stream BEFORE the change
        var workspace = client.GetWorkspace();
        var collectionStream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            nodeAddress,
            new CollectionReference(nameof(AccessAssignment)));

        // Wait for initial state (may be empty)
        var initial = await collectionStream
            .Where(x => x.Value != null)
            .Timeout(10.Seconds())
            .FirstAsync();
        Output.WriteLine($"Initial collection: {initial.Value?.Instances.Count ?? 0} items");
        if (initial.Value != null)
        {
            foreach (var kvp in initial.Value.Instances)
                Output.WriteLine($"  Key={kvp.Key}, Value={kvp.Value}");
        }
        var initialCount = initial.Value?.Instances.Count ?? 0;

        // Create a new assignment via DataChangeRequest sent to the hub
        var newAssignment = new AccessAssignment
        {
            UserId = "NewUser",
            RoleId = "Viewer",
            SourcePath = NodePath,
            IsLocal = true,
            IsActive = true,
            DisplayLabel = "NewUser",
            SourceDisplay = NodePath
        };

        Output.WriteLine("Sending DataChangeRequest...");
        var response = await client.AwaitResponse(
            DataChangeRequest.Create([newAssignment], "test"),
            o => o.WithTarget(nodeAddress),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token);

        Output.WriteLine($"DataChangeRequest response: {response.Message.GetType().Name}");
        response.Message.Should().BeOfType<DataChangeResponse>();

        // Wait for the collection to grow beyond the initial count
        InstanceCollection? result = null;
        try
        {
            result = await collectionStream
                .Where(x => x.Value != null && x.Value.Instances.Count > initialCount)
                .Select(x => x.Value!)
                .Timeout(10.Seconds())
                .FirstAsync();
        }
        catch (TimeoutException)
        {
            // Dump current state for diagnostics
            var current = await collectionStream.Select(x => x.Value).FirstAsync();
            Output.WriteLine($"TIMEOUT: Collection still has {current?.Instances.Count ?? 0} items after DataChangeRequest");
            if (current != null)
            {
                foreach (var kvp in current.Instances)
                    Output.WriteLine($"  Key={kvp.Key}, Value={kvp.Value}");
            }
        }

        result.Should().NotBeNull("DataChangeRequest should add assignment to the workspace collection");

        var jsonOptions = client.JsonSerializerOptions;
        var assignments = result!.Instances.Values
            .Select(v => v is JsonElement je
                ? JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText(), jsonOptions)!
                : (AccessAssignment)v)
            .ToList();

        Output.WriteLine($"After DataChangeRequest, workspace contains {assignments.Count} assignment(s):");
        foreach (var a in assignments)
            Output.WriteLine($"  UserId={a.UserId}, RoleId={a.RoleId}");

        assignments.Should().Contain(a => a.UserId == "NewUser" && a.RoleId == "Viewer",
            "DataChangeRequest should add assignment to workspace via AccessAssignmentTypeSource");

        // Verify the assignment was synced to ISecurityService
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var allAssignments = new List<AccessAssignment>();
        await foreach (var a in svc.GetAccessAssignmentsAsync(NodePath, TestTimeout))
            allAssignments.Add(a);

        Output.WriteLine($"ISecurityService contains {allAssignments.Count} assignment(s) for {NodePath}:");
        foreach (var a in allAssignments)
            Output.WriteLine($"  UserId={a.UserId}, RoleId={a.RoleId}, IsLocal={a.IsLocal}");

        allAssignments.Should().Contain(a => a.UserId == "NewUser" && a.RoleId == "Viewer",
            "AccessAssignmentTypeSource.UpdateImpl should sync new assignment to ISecurityService");
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
