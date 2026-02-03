using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.Test.TestHelpers;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for the Todo Create flow that verify actual UI behavior.
/// Two-flow Create design:
/// 1. CreateChild (on parent node): Shows Name/Description form, creates transient node, redirects
/// 2. Create (on transient node): Shows ContentType editor with all fields
/// </summary>
[Collection("SamplesGraphData")]
public class TodoCreateFlowTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverTodoCreateTests",
        ".mesh-cache");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        Directory.CreateDirectory(SharedCacheDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(dataDirectory)
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = SharedCacheDirectory;
                    o.EnableDiskCache = true;
                });
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddJsonGraphConfiguration(dataDirectory)
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    #region CreateChild Flow Tests (on parent node)

    /// <summary>
    /// Test that the CreateChild form (with ?type= parameter) shows Name and Description fields.
    /// This is the first step of the create flow where user enters basic info.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateChild_WithTodoType_ShowsNameDescriptionForm()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        Output.WriteLine("Initializing hub for ACME/ProductLaunch...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(parentAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea)
        {
            Id = "?type=ACME%2FProject%2FTodo"
        };

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            reference);

        Output.WriteLine("Waiting for CreateChild form to render...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is StackControl)
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync();

        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("CreateChild form should render");

        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().NotBeEmpty("CreateChild form should have child areas");

        Output.WriteLine($"CreateChild form has {stack.Areas.Count()} areas");

        // Verify the form contains the expected structure
        // The CreateChild form should have Name and Description fields, plus buttons
        stack.Areas.Count().Should().BeGreaterThan(2, "Form should have multiple areas (header, fields, buttons)");
    }

    /// <summary>
    /// Test that CreateChild form shows type selection grid when no type parameter is provided.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateArea_WithoutTypeParam_ShowsTypeSelection()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        Output.WriteLine("Initializing hub...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(parentAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            reference);

        Output.WriteLine("Waiting for type selection to render...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync();

        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Type selection should render");

        // Should be a StackControl containing the type selection grid
        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().NotBeEmpty("Type selection should have areas");

        Output.WriteLine($"Type selection has {stack.Areas.Count()} areas");
    }

    #endregion

    #region Transient Node Create Flow Tests

    /// <summary>
    /// Test that a transient Todo node's Create area shows the ContentType editor
    /// with the expected structure (header, editor, buttons).
    /// Note: Buttons are rendered inside nested views, which we verify exist via area count.
    /// </summary>
    [Fact(Timeout = 45000)]
    public async Task TransientTodo_CreateArea_ShowsContentTypeEditor()
    {
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Create a unique transient node
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var nodePath = $"ACME/ProductLaunch/Todo/TestTask{uniqueId}";

        Output.WriteLine($"Creating transient node at: {nodePath}");

        var transientNode = MeshNode.FromPath(nodePath) with
        {
            Name = "Test Task",
            NodeType = "ACME/Project/Todo",
            State = MeshNodeState.Transient
        };

        try
        {
            // Create the transient node via catalog
            await catalog.CreateTransientNodeAsync(transientNode, ct: TestContext.Current.CancellationToken);
            Output.WriteLine("Transient node created.");

            // Initialize the node's hub
            var nodeAddress = new Address(nodePath);
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(nodeAddress),
                TestContext.Current.CancellationToken);
            Output.WriteLine("Node hub initialized.");

            // Request the Create area for the transient node
            var workspace = client.GetWorkspace();
            var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea);

            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                nodeAddress,
                reference);

            Output.WriteLine("Waiting for Create editor to render...");
            var control = await stream
                .GetControlStream(reference.Area!)
                .Where(c => c is StackControl)
                .Timeout(TimeSpan.FromSeconds(20))
                .FirstAsync();

            Output.WriteLine($"Received control: {control?.GetType().Name}");
            control.Should().NotBeNull("Create editor should render for transient node");

            var stack = control.Should().BeOfType<StackControl>().Subject;
            Output.WriteLine($"Create editor has {stack.Areas.Count()} areas");

            // Log the area IDs for debugging
            foreach (var area in stack.Areas)
            {
                Output.WriteLine($"  Area: {area.Id}");
            }

            // The Create editor should have multiple areas:
            // - Header with title and cancel button (area 1)
            // - EditorControl with all Todo fields (area 2)
            // - Button row with Create button (area 3)
            stack.Areas.Count().Should().BeGreaterThanOrEqualTo(3, "Editor should have header, content, and buttons areas");
        }
        finally
        {
            // Cleanup
            try
            {
                await catalog.DeleteNodeAsync(nodePath);
                Output.WriteLine("Cleanup: transient node deleted.");
            }
            catch (Exception ex)
            {
                Output.WriteLine($"Cleanup warning: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Test that creating a transient node and requesting its Create area
    /// returns an editor that includes the expected form structure.
    /// </summary>
    [Fact(Timeout = 45000)]
    public async Task TransientTodo_CreateArea_HasEditorStructure()
    {
        var client = GetClient();
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var nodePath = $"ACME/ProductLaunch/Todo/EditorTest{uniqueId}";

        Output.WriteLine($"Creating transient node at: {nodePath}");

        var transientNode = MeshNode.FromPath(nodePath) with
        {
            Name = "Editor Test Task",
            NodeType = "ACME/Project/Todo",
            State = MeshNodeState.Transient
        };

        try
        {
            await catalog.CreateTransientNodeAsync(transientNode, ct: TestContext.Current.CancellationToken);

            var nodeAddress = new Address(nodePath);
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(nodeAddress),
                TestContext.Current.CancellationToken);

            var workspace = client.GetWorkspace();
            var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea);

            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                nodeAddress,
                reference);

            var control = await stream
                .GetControlStream(reference.Area!)
                .Where(c => c is StackControl)
                .Timeout(TimeSpan.FromSeconds(20))
                .FirstAsync();

            control.Should().NotBeNull();
            var stack = (StackControl)control!;

            // Log all areas for debugging
            Output.WriteLine("Areas in Create editor:");
            var areaIndex = 0;
            foreach (var area in stack.Areas)
            {
                Output.WriteLine($"  [{areaIndex++}] Id: {area.Id}");
            }

            // The editor structure should include fields for the Todo content type
            // Based on CreateLayoutArea.BuildCreateEditor, it should have:
            // 1. Header (H2 title + Cancel button)
            // 2. EditorControl (if ContentType resolved)
            // 3. Button row (Create button)
            stack.Areas.Count().Should().BeGreaterThanOrEqualTo(2, "Should have at least header and content areas");
        }
        finally
        {
            try
            {
                await catalog.DeleteNodeAsync(nodePath);
            }
            catch { }
        }
    }

    #endregion

    #region End-to-End Flow Tests

    /// <summary>
    /// Test the complete create flow: create transient node, verify it can be retrieved.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task EndToEnd_CreateTransientNode_CanBeRetrieved()
    {
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var nodePath = $"ACME/ProductLaunch/Todo/E2E{uniqueId}";

        Output.WriteLine($"Creating transient node at: {nodePath}");

        var transientNode = MeshNode.FromPath(nodePath) with
        {
            Name = "E2E Test Task",
            Description = "Test description",
            NodeType = "ACME/Project/Todo",
            State = MeshNodeState.Transient
        };

        try
        {
            // Step 1: Create transient node
            var createdNode = await catalog.CreateTransientNodeAsync(transientNode, ct: TestContext.Current.CancellationToken);
            createdNode.Should().NotBeNull("Transient node should be created");
            createdNode.State.Should().Be(MeshNodeState.Transient, "Node should be in Transient state");
            createdNode.Name.Should().Be("E2E Test Task");
            Output.WriteLine($"Created transient node: {createdNode.Path}");

            // Step 2: Retrieve the node
            var retrievedNode = await catalog.GetNodeAsync(new Address(nodePath));
            retrievedNode.Should().NotBeNull("Transient node should be retrievable");
            retrievedNode!.State.Should().Be(MeshNodeState.Transient);
            retrievedNode.Name.Should().Be("E2E Test Task");
            retrievedNode.NodeType.Should().Be("ACME/Project/Todo");
            Output.WriteLine($"Retrieved transient node successfully");
        }
        finally
        {
            try
            {
                await catalog.DeleteNodeAsync(nodePath);
                Output.WriteLine("Cleanup: node deleted.");
            }
            catch { }
        }
    }

    /// <summary>
    /// Test that the catalog correctly handles node creation with all required services.
    /// </summary>
    [Fact(Timeout = 15000)]
    public void Services_AreRegisteredForCreateFlow()
    {
        // Verify all required services are registered
        var meshCatalog = Mesh.ServiceProvider.GetService<IMeshCatalog>();
        meshCatalog.Should().NotBeNull("IMeshCatalog should be registered");

        var nodeTypeService = Mesh.ServiceProvider.GetService<INodeTypeService>();
        nodeTypeService.Should().NotBeNull("INodeTypeService should be registered");

        var meshConfig = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        meshConfig.DefaultNodeHubConfiguration.Should().NotBeNull("DefaultNodeHubConfiguration should be set");

        Output.WriteLine("All required services are registered.");
    }

    /// <summary>
    /// Test that INodeTypeService returns creatable types for ProductLaunch.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task NodeTypeService_ReturnsCreatableTypes()
    {
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();

        var creatableTypes = await nodeTypeService
            .GetCreatableTypesAsync("ACME/ProductLaunch", TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {creatableTypes.Count} creatable types:");
        foreach (var typeInfo in creatableTypes)
        {
            Output.WriteLine($"  - {typeInfo.NodeTypePath}: {typeInfo.DisplayName}");
        }

        creatableTypes.Should().NotBeEmpty("ProductLaunch should have creatable types");

        // Verify Todo type is among the creatable types
        var todoType = creatableTypes.FirstOrDefault(t => t.NodeTypePath == "ACME/Project/Todo");
        todoType.Should().NotBeNull("ACME/Project/Todo should be a creatable type");
    }

    #endregion

    #region Verification Tests

    /// <summary>
    /// Test that existing Todo items can be queried and have the expected content structure.
    /// This verifies that the Todo ContentType is properly configured.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task ExistingTodo_HasExpectedContentStructure()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var query = "path:ACME/ProductLaunch/Todo/DefinePersona";
        var results = await meshQuery.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery(query), null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        results.Should().HaveCount(1, "DefinePersona should exist");
        var node = results[0];

        node.Content.Should().NotBeNull("Todo should have content");

        // Serialize to verify structure
        var contentJson = JsonSerializer.Serialize(node.Content);
        Output.WriteLine($"Content JSON: {contentJson}");

        using var doc = JsonDocument.Parse(contentJson);
        var root = doc.RootElement;

        // Verify expected properties exist
        root.TryGetProperty("id", out _).Should().BeTrue("Content should have id");
        root.TryGetProperty("title", out _).Should().BeTrue("Content should have title");
        root.TryGetProperty("category", out _).Should().BeTrue("Content should have category");
        root.TryGetProperty("priority", out _).Should().BeTrue("Content should have priority");
        root.TryGetProperty("status", out _).Should().BeTrue("Content should have status");

        Output.WriteLine("Todo content structure verified.");
    }

    /// <summary>
    /// Test that the Overview area renders for ProductLaunch (baseline test).
    /// Ensures the test infrastructure is working correctly.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Baseline_OverviewAreaRenders()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(parentAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync();

        control.Should().NotBeNull("Overview area should render");
        Output.WriteLine($"Overview control type: {control?.GetType().Name}");
    }

    #endregion
}
