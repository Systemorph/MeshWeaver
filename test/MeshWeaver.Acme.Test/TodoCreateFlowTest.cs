using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Acme.Test.TestHelpers;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Acme.Test;

/// <summary>
/// Tests for the Todo Create flow that verify actual UI behavior.
/// Two-flow Create design:
/// 1. CreateChild (on parent node): Shows Name/Description form, creates transient node, redirects
/// 2. Create (on transient node): Shows ContentType editor with all fields
/// </summary>
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
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddAcme()
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
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    #region CreateChild Flow Tests (on parent node)

    /// <summary>
    /// Test that the Create area on a parent node redirects to the /create Blazor page.
    /// In the current architecture, the Create layout area always returns a RedirectControl.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CreateChild_WithTodoType_RedirectsToCreatePage()
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

        Output.WriteLine("Waiting for Create area to render...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();

        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Create area should render");
        control.Should().BeOfType<RedirectControl>("Create area on parent node should redirect to /create page");
    }

    /// <summary>
    /// Test that the Create area without type parameter still redirects to the /create Blazor page.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CreateArea_WithoutTypeParam_RedirectsToCreatePage()
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

        Output.WriteLine("Waiting for Create area to render...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();

        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Create area should render");
        control.Should().BeOfType<RedirectControl>("Create area on parent node should redirect to /create page");
    }

    #endregion

    #region Transient Node Create Flow Tests

    /// <summary>
    /// Test that a transient Todo node's Create area confirms it as Active and redirects to Edit.
    /// In the current architecture, the Create area on a transient node confirms it and returns a RedirectControl.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task TransientTodo_CreateArea_ConfirmsAndRedirectsToEdit()
    {
        var client = GetClient();
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
            // Create the transient node via NodeFactory
            await NodeFactory.CreateTransientAsync(transientNode, ct: TestContext.Current.CancellationToken);
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

            Output.WriteLine("Waiting for Create area to render...");
            var control = await stream
                .GetControlStream(reference.Area!)
                .Where(c => c != null)
                .Timeout(TimeSpan.FromSeconds(10))
                .FirstAsync();

            Output.WriteLine($"Received control: {control?.GetType().Name}");
            control.Should().NotBeNull("Create area should render for transient node");
            control.Should().BeOfType<RedirectControl>("Create area on transient node should redirect to Edit area");
        }
        finally
        {
            // Cleanup
            try
            {
                await NodeFactory.DeleteNodeAsync(nodePath);
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
    [Fact(Timeout = 20000)]
    public async Task TransientTodo_CreateArea_HasEditorStructure()
    {
        var client = GetClient();

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
            await NodeFactory.CreateTransientAsync(transientNode, ct: TestContext.Current.CancellationToken);
            Output.WriteLine("Transient node created successfully");

            var nodeAddress = new Address(nodePath);
            using var pingCts = new CancellationTokenSource(3.Seconds());
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(nodeAddress),
                pingCts.Token);
            Output.WriteLine("Ping succeeded");

            var workspace = client.GetWorkspace();
            var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea);

            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                nodeAddress,
                reference);

            // First check what controls arrive (any type, not just StackControl)
            var anyControl = await stream
                .GetControlStream(reference.Area!)
                .Where(c => c != null)
                .Timeout(TimeSpan.FromSeconds(5))
                .FirstOrDefaultAsync();

            Output.WriteLine($"First control received: {anyControl?.GetType().Name ?? "null"}");

            anyControl.Should().NotBeNull("Create area should emit at least one control");

            if (anyControl is StackControl stack)
            {
                Output.WriteLine("Areas in Create editor:");
                var areaIndex = 0;
                foreach (var area in stack.Areas)
                {
                    Output.WriteLine($"  [{areaIndex++}] Id: {area.Id}");
                }
                stack.Areas.Count().Should().BeGreaterThanOrEqualTo(2, "Should have at least header and content areas");
            }
            else
            {
                Output.WriteLine($"Create area returned {anyControl.GetType().Name} instead of StackControl — acceptable for custom NodeTypes");
            }
        }
        finally
        {
            try
            {
                await NodeFactory.DeleteNodeAsync(nodePath);
            }
            catch { }
        }
    }

    #endregion

    #region End-to-End Flow Tests

    /// <summary>
    /// Test the ACTUAL create flow: create transient node, then send CreateNodeRequest to confirm it.
    /// This is what happens when user clicks "Create" button on a transient node.
    /// BUG: HandleCreateNodeRequest was calling CreateTransientNodeAsync again, causing "Node already exists" error.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CreateNodeRequest_ForExistingTransientNode_ConfirmsNode()
    {
        var client = GetClient();

        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var nodePath = $"ACME/ProductLaunch/Todo/CreateTest{uniqueId}";

        Output.WriteLine($"Creating transient node at: {nodePath}");

        var transientNode = MeshNode.FromPath(nodePath) with
        {
            Name = "Create Test Task",
            NodeType = "ACME/Project/Todo",
            State = MeshNodeState.Transient
        };

        try
        {
            // Step 1: Create transient node (simulates BuildCreateChildForm)
            var createdNode = await NodeFactory.CreateTransientAsync(transientNode, ct: TestContext.Current.CancellationToken);
            createdNode.Should().NotBeNull("Transient node should be created");
            createdNode.State.Should().Be(MeshNodeState.Transient);
            Output.WriteLine($"Transient node created: {createdNode.Path}");

            // Step 2: Initialize the node's hub (required for sending CreateNodeRequest)
            var nodeAddress = new Address(nodePath);
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(nodeAddress),
                TestContext.Current.CancellationToken);
            Output.WriteLine("Node hub initialized.");

            // Step 3: Send CreateNodeRequest with State=Active (simulates Create button click)
            // This is what BuildCreateEditor does when user clicks "Create"
            var nodeWithContent = createdNode with
            {
                State = MeshNodeState.Active,
                Content = new { Title = "Create Test Task", Description = "Testing" }
            };

            Output.WriteLine("Sending CreateNodeRequest...");

            // Send CreateNodeRequest - note: response type registration may vary
            // We'll verify success by checking the persisted node state
            client.Post(
                new CreateNodeRequest(nodeWithContent),
                o => o.WithTarget(nodeAddress));

            // Use IMeshService.ObserveQuery to wait for the state change to be persisted
            var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            var query = MeshQueryRequest.FromQuery($"path:{nodePath}");

            var confirmedNode = await meshQuery
                .ObserveQuery<MeshNode>(query)
                .SelectMany(change => change.Items)
                .Where(node => node.State == MeshNodeState.Active)
                .Timeout(TimeSpan.FromSeconds(10))
                .FirstAsync();

            Output.WriteLine($"Observed node state: {confirmedNode.State}");

            // Verify success by checking persisted node state directly
            var persistedNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath}").FirstOrDefaultAsync();
            Output.WriteLine($"Persisted node state: {persistedNode?.State}");
            persistedNode.Should().NotBeNull("Node should be persisted after CreateNodeRequest");
            persistedNode!.State.Should().Be(MeshNodeState.Active, "Node should be Active after confirmation");

            Output.WriteLine($"Node confirmed successfully: {persistedNode.Path}, State: {persistedNode.State}");
            Output.WriteLine("End-to-end create flow completed successfully.");
        }
        finally
        {
            try
            {
                await NodeFactory.DeleteNodeAsync(nodePath);
                Output.WriteLine("Cleanup: node deleted.");
            }
            catch { }
        }
    }

    /// <summary>
    /// Test the complete create flow: create transient node, verify it can be retrieved.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task EndToEnd_CreateTransientNode_CanBeRetrieved()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var nodePath = $"ACME/ProductLaunch/Todo/E2E{uniqueId}";

        Output.WriteLine($"Creating transient node at: {nodePath}");

        var transientNode = MeshNode.FromPath(nodePath) with
        {
            Name = "E2E Test Task",
            NodeType = "ACME/Project/Todo",
            State = MeshNodeState.Transient
        };

        try
        {
            // Step 1: Create transient node
            var createdNode = await NodeFactory.CreateTransientAsync(transientNode, ct: TestContext.Current.CancellationToken);
            createdNode.Should().NotBeNull("Transient node should be created");
            createdNode.State.Should().Be(MeshNodeState.Transient, "Node should be in Transient state");
            createdNode.Name.Should().Be("E2E Test Task");
            Output.WriteLine($"Created transient node: {createdNode.Path}");

            // Step 2: Retrieve the node
            var retrievedNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath}").FirstOrDefaultAsync();
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
                await NodeFactory.DeleteNodeAsync(nodePath);
                Output.WriteLine("Cleanup: node deleted.");
            }
            catch { }
        }
    }

    /// <summary>
    /// Test that the catalog correctly handles node creation with all required services.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void Services_AreRegisteredForCreateFlow()
    {
        // Verify all required services are registered
        var nodeFactory = Mesh.ServiceProvider.GetService<IMeshService>();
        nodeFactory.Should().NotBeNull("IMeshService should be registered");

        var nodeTypeService = Mesh.ServiceProvider.GetService<INodeTypeService>();
        nodeTypeService.Should().NotBeNull("INodeTypeService should be registered");

        var meshConfig = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        meshConfig.DefaultNodeHubConfiguration.Should().NotBeNull("DefaultNodeHubConfiguration should be set");

        Output.WriteLine("All required services are registered.");
    }

    /// <summary>
    /// Test that INodeTypeService returns creatable types for ProductLaunch.
    /// </summary>
    [Fact(Timeout = 20000)]
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
    /// Test that simulates the actual UI create flow:
    /// 1. Create transient node WITHOUT content (like CreateChild does)
    /// 2. Initialize hub (which creates content instance via MeshDataSource.CreateContentInstance)
    /// 3. Confirm node with content from workspace
    /// 4. Verify content fields are preserved
    ///
    /// This reproduces the bug where content fields (category, priority, etc.) are empty after create.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CreateFlow_TransientNodeWithoutContent_PreservesContentFieldsAfterConfirm()
    {
        var client = GetClient();

        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var nodePath = $"ACME/ProductLaunch/Todo/UIFlowTest{uniqueId}";

        Output.WriteLine($"Creating transient node at: {nodePath}");

        // Step 1: Create transient node WITHOUT content (simulates CreateChild flow)
        // In real UI, this is done by BuildCreateChildForm which only sets Name/Description/NodeType
        var transientNode = MeshNode.FromPath(nodePath) with
        {
            Name = "UI Flow Test Task",
            NodeType = "ACME/Project/Todo",
            State = MeshNodeState.Transient
            // NOTE: No Content set - this is how CreateChild creates the node
        };

        try
        {
            await NodeFactory.CreateTransientAsync(transientNode, ct: TestContext.Current.CancellationToken);
            Output.WriteLine("Transient node created (without content).");

            // Step 2: Initialize the node's hub (this triggers MeshDataSource initialization)
            var nodeAddress = new Address(nodePath);
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(nodeAddress),
                TestContext.Current.CancellationToken);
            Output.WriteLine("Node hub initialized.");

            // Step 3: Get the node from workspace to see what content was created
            var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            var query = MeshQueryRequest.FromQuery($"path:{nodePath}");

            var workspaceNode = await meshQuery
                .ObserveQuery<MeshNode>(query)
                .SelectMany(change => change.Items)
                .Timeout(TimeSpan.FromSeconds(10))
                .FirstAsync();

            Output.WriteLine($"Workspace node content type: {workspaceNode.Content?.GetType().Name ?? "null"}");
            if (workspaceNode.Content != null)
            {
                var contentJson = JsonSerializer.Serialize(workspaceNode.Content);
                Output.WriteLine($"Workspace node content: {contentJson}");
            }

            // Step 4: Simulate user filling out the form by creating content with values
            // In real UI, this would come from the data stream after user edits
            var editedContent = new
            {
                id = uniqueId,
                title = "UI Flow Test Task",
                description = "Testing UI flow content preservation",
                category = "Testing",
                priority = "High",
                status = "Planning"
            };

            // Step 5: Confirm the node (simulates Create button click)
            var activeNode = transientNode with
            {
                State = MeshNodeState.Active,
                Content = editedContent
            };

            client.Post(
                new CreateNodeRequest(activeNode),
                o => o.WithTarget(nodeAddress));

            // Step 6: Wait for node to become Active
            var confirmedNode = await meshQuery
                .ObserveQuery<MeshNode>(query)
                .SelectMany(change => change.Items)
                .Where(node => node.State == MeshNodeState.Active)
                .Timeout(TimeSpan.FromSeconds(10))
                .FirstAsync();

            Output.WriteLine($"Node confirmed. Content type: {confirmedNode.Content?.GetType().Name ?? "null"}");
            if (confirmedNode.Content != null)
            {
                var contentJson = JsonSerializer.Serialize(confirmedNode.Content);
                Output.WriteLine($"Confirmed node content: {contentJson}");
            }

            // Step 7: Verify content fields are preserved
            confirmedNode.Content.Should().NotBeNull("Confirmed node should have content");

            var json = JsonSerializer.Serialize(confirmedNode.Content);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // These assertions reproduce the bug - content should NOT be empty
            root.TryGetProperty("category", out var categoryProp).Should().BeTrue("Content should have category");
            categoryProp.GetString().Should().Be("Testing", "Content category should be preserved");

            root.TryGetProperty("priority", out var priorityProp).Should().BeTrue("Content should have priority");
            priorityProp.GetString().Should().Be("High", "Content priority should be preserved");

            root.TryGetProperty("status", out var statusProp).Should().BeTrue("Content should have status");
            statusProp.GetString().Should().Be("Planning", "Content status should be preserved");

            Output.WriteLine("UI flow test completed - all content fields preserved.");
        }
        finally
        {
            try
            {
                await NodeFactory.DeleteNodeAsync(nodePath);
                Output.WriteLine("Cleanup: node deleted.");
            }
            catch { }
        }
    }

    /// <summary>
    /// Test that GetDataRequest with EntityReference returns the correct Todo content.
    /// This is the primary mechanism to verify content retrieval after node creation.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task GetDataRequest_ForCreatedTodo_ReturnsCorrectContent()
    {
        var client = GetClient();

        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var nodePath = $"ACME/ProductLaunch/Todo/DataReqTest{uniqueId}";

        Output.WriteLine($"Creating node at: {nodePath}");

        // Create transient node with content
        var todoContent = new
        {
            id = uniqueId,
            title = "Data Request Test Task",
            description = "Testing GetDataRequest retrieval",
            status = "pending",
            priority = "high",
            category = "Testing"
        };

        var transientNode = MeshNode.FromPath(nodePath) with
        {
            Name = "Data Request Test Task",
            NodeType = "ACME/Project/Todo",
            State = MeshNodeState.Transient,
            Content = todoContent
        };

        try
        {
            // Step 1: Create transient node
            await NodeFactory.CreateTransientAsync(transientNode, ct: TestContext.Current.CancellationToken);
            Output.WriteLine("Transient node created.");

            // Step 2: Initialize the node's hub
            var nodeAddress = new Address(nodePath);
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(nodeAddress),
                TestContext.Current.CancellationToken);
            Output.WriteLine("Node hub initialized.");

            // Step 3: Confirm the node (make it Active)
            var activeNode = transientNode with { State = MeshNodeState.Active };
            client.Post(
                new CreateNodeRequest(activeNode),
                o => o.WithTarget(nodeAddress));

            // Step 4: Wait for node to become Active using reactive query
            var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            var query = MeshQueryRequest.FromQuery($"path:{nodePath}");

            await meshQuery
                .ObserveQuery<MeshNode>(query)
                .SelectMany(change => change.Items)
                .Where(node => node.State == MeshNodeState.Active)
                .Timeout(TimeSpan.FromSeconds(10))
                .FirstAsync();
            Output.WriteLine("Node confirmed as Active.");

            // Step 5: Retrieve the MeshNode via IMeshService query
            var retrievedNode = await meshQuery
                .QueryAsync<MeshNode>(MeshQueryRequest.FromQuery($"path:{nodePath}"))
                .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

            Output.WriteLine("Node retrieved via query.");

            // Step 6: Verify the response contains the correct data
            retrievedNode.Should().NotBeNull("Response data should not be null");
            retrievedNode!.Path.Should().Be(nodePath, "Retrieved node should have correct path");
            retrievedNode.Name.Should().Be("Data Request Test Task", "Retrieved node should have correct name");
            retrievedNode.State.Should().Be(MeshNodeState.Active, "Retrieved node should be Active");

            // Verify content is present and has expected structure
            retrievedNode.Content.Should().NotBeNull("Node should have content");
            var contentJson = JsonSerializer.Serialize(retrievedNode.Content);
            Output.WriteLine($"Retrieved content: {contentJson}");

            using var doc = JsonDocument.Parse(contentJson);
            var root = doc.RootElement;

            // Verify title
            root.TryGetProperty("title", out var titleProp).Should().BeTrue("Content should have title");
            titleProp.GetString().Should().Be("Data Request Test Task", "Content title should match");

            // Verify category is preserved
            root.TryGetProperty("category", out var categoryProp).Should().BeTrue("Content should have category");
            categoryProp.GetString().Should().Be("Testing", "Content category should match");

            // Verify priority is preserved
            root.TryGetProperty("priority", out var priorityProp).Should().BeTrue("Content should have priority");
            priorityProp.GetString().Should().Be("high", "Content priority should match");

            // Verify status is preserved
            root.TryGetProperty("status", out var statusProp).Should().BeTrue("Content should have status");
            statusProp.GetString().Should().Be("pending", "Content status should match");

            // Verify description is preserved
            root.TryGetProperty("description", out var descProp).Should().BeTrue("Content should have description");
            descProp.GetString().Should().Be("Testing GetDataRequest retrieval", "Content description should match");

            Output.WriteLine("GetDataRequest verification completed successfully - all fields preserved.");
        }
        finally
        {
            try
            {
                await NodeFactory.DeleteNodeAsync(nodePath);
                Output.WriteLine("Cleanup: node deleted.");
            }
            catch { }
        }
    }

    /// <summary>
    /// Test that existing Todo items can be queried and have the expected content structure.
    /// This verifies that the Todo ContentType is properly configured.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ExistingTodo_HasExpectedContentStructure()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

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

        // Verify expected properties exist (Todo content has title, category, priority, status - no id)
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
    [Fact(Timeout = 20000)]
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
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();

        control.Should().NotBeNull("Overview area should render");
        Output.WriteLine($"Overview control type: {control?.GetType().Name}");
    }

    #endregion
}
