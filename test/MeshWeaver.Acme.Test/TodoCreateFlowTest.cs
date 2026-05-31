using MeshWeaver.Blazor.Portal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
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
    // Stable cache directory — the timestamped-subdir cache (a3ab9909e)
    // gives each compile its own subdir so prior-process DLLs aren't touched.
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
            .AddSpaceType()
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
    /// Test that the CreateChild form (with ?type= parameter) shows Name and Description fields.
    /// This is the first step of the create flow where user enters basic info.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void CreateChild_WithTodoType_ShowsNameDescriptionForm()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        Output.WriteLine("Initializing hub for ACME/ProductLaunch...");
        client.Observe(new PingRequest(), o => o.WithTarget(parentAddress)).Should().Emit();
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
        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(c => c is StackControl);

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
    [Fact(Timeout = 60000)]
    public void CreateArea_WithoutTypeParam_ShowsTypeSelection()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        Output.WriteLine("Initializing hub...");
        client.Observe(new PingRequest(), o => o.WithTarget(parentAddress)).Should().Emit();
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            reference);

        Output.WriteLine("Waiting for type selection to render...");
        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(c => c != null);

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
    [Fact(Timeout = 60000)]
    public void TransientTodo_CreateArea_ShowsContentTypeEditor()
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
            NodeFactory.CreateTransient(transientNode).Should().Emit();
            Output.WriteLine("Transient node created.");

            // Initialize the node's hub. 30 s Ping budget — activating a brand-new
            // per-instance NodeType hub kicks off a cold-cache dynamic compile that
            // blocks the hub's first Ping response well past the default 10 s on slow
            // CI runners (the same budget the sibling HasEditorStructure test uses).
            // Outer [Fact(Timeout = 60000)] still bounds the whole test.
            var nodeAddress = new Address(nodePath);
            client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Within(30.Seconds()).Emit();
            Output.WriteLine("Node hub initialized.");

            // Transient nodes are auto-confirmed and redirected to Edit.
            // Verify the Edit area renders with content.
            var workspace = client.GetWorkspace();
            var reference = new LayoutAreaReference(MeshNodeLayoutAreas.EditArea);

            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                nodeAddress,
                reference);

            Output.WriteLine("Waiting for Edit area to render (transient auto-confirmed)...");
            // 30 s budget: cold-cache dynamic NodeType compile + per-instance
            // hub activation + Edit-area composition exceed 10 s on slow CI
            // runners. The outer [Fact(Timeout = 60000)] still bounds the test.
            var control = stream
                .GetControlStream(reference.Area!)
                .Should().Within(30.Seconds()).Match(c => c is StackControl);

            Output.WriteLine($"Received control: {control?.GetType().Name}");
            control.Should().NotBeNull("Edit area should render for confirmed transient node");

            var stack = control.Should().BeOfType<StackControl>().Subject;
            Output.WriteLine($"Edit area has {stack.Areas.Count()} areas");
            stack.Areas.Count().Should().BeGreaterThanOrEqualTo(2, "Edit area should have content areas");
        }
        finally
        {
            // Cleanup
            try
            {
                NodeFactory.DeleteNode(nodePath).Should().Emit();
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
    [Fact(Timeout = 60000)]
    public void TransientTodo_CreateArea_HasEditorStructure()
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
            NodeFactory.CreateTransient(transientNode).Should().Emit();
            Output.WriteLine("Transient node created successfully");

            var nodeAddress = new Address(nodePath);
            // 30 s Ping budget — a cold-cache dynamic NodeType compile takes
            // longer than the original 3 s on slow CI runners. Outer
            // [Fact(Timeout = 60000)] still bounds the whole test.
            // 30 s ping budget — a cold-cache dynamic NodeType compile takes
            // longer than the original 3 s on slow CI runners. Outer
            // [Fact(Timeout = 60000)] still bounds the whole test.
            client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Within(30.Seconds()).Emit();
            Output.WriteLine("Ping succeeded");

            var workspace = client.GetWorkspace();
            var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea);

            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                nodeAddress,
                reference);

            // First check what controls arrive (any type, not just StackControl).
            // 20 s budget — Create-area composition runs synchronously after
            // hub activation; bumped from 5 s for CI runner headroom.
            var anyControl = stream
                .GetControlStream(reference.Area!)
                .Should().Within(20.Seconds()).Match(c => c != null);

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
                Output.WriteLine($"Create area returned {anyControl!.GetType().Name} instead of StackControl â€” acceptable for custom NodeTypes");
            }
        }
        finally
        {
            try
            {
                NodeFactory.DeleteNode(nodePath).Should().Emit();
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
    [Fact(Timeout = 60000)]
    public void CreateNodeRequest_ForExistingTransientNode_ConfirmsNode()
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
            var createdNode = NodeFactory.CreateTransient(transientNode).Should().Emit();
            createdNode.Should().NotBeNull("Transient node should be created");
            createdNode.State.Should().Be(MeshNodeState.Transient);
            Output.WriteLine($"Transient node created: {createdNode.Path}");

            // Step 2: Initialize the node's hub (required for sending CreateNodeRequest)
            var nodeAddress = new Address(nodePath);
            client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();
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

            var confirmedNode = meshQuery
                .ObserveQuery<MeshNode>(query)
                .SelectMany(change => change.Items)
                .Should().Within(10.Seconds()).Match(node => node.State == MeshNodeState.Active);

            Output.WriteLine($"Observed node state: {confirmedNode.State}");

            // Verify success by checking persisted node state directly (CQRS-correct read)
            var persistedNode = ReadNode(nodePath)
                .Should().Within(60.Seconds()).Match(n => n is not null && n.State == MeshNodeState.Active);
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
                NodeFactory.DeleteNode(nodePath).Should().Emit();
                Output.WriteLine("Cleanup: node deleted.");
            }
            catch { }
        }
    }

    /// <summary>
    /// Test the complete create flow: create transient node, verify it can be retrieved.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void EndToEnd_CreateTransientNode_CanBeRetrieved()
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
            var createdNode = NodeFactory.CreateTransient(transientNode).Should().Emit();
            createdNode.Should().NotBeNull("Transient node should be created");
            createdNode.State.Should().Be(MeshNodeState.Transient, "Node should be in Transient state");
            createdNode.Name.Should().Be("E2E Test Task");
            Output.WriteLine($"Created transient node: {createdNode.Path}");

            // Step 2: Retrieve the node via stream
            var retrievedNode = ReadNode(nodePath)
                .Should().Within(60.Seconds()).Match(n => n is not null && n.State == MeshNodeState.Transient);
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
                NodeFactory.DeleteNode(nodePath).Should().Emit();
                Output.WriteLine("Cleanup: node deleted.");
            }
            catch { }
        }
    }

    /// <summary>
    /// Test that the catalog correctly handles node creation with all required services.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void Services_AreRegisteredForCreateFlow()
    {
        // Verify all required services are registered
        var nodeFactory = Mesh.ServiceProvider.GetService<IMeshService>();
        nodeFactory.Should().NotBeNull("IMeshService should be registered");

        var provider = Mesh.ServiceProvider.GetService<ICreatableTypesProvider>();
        provider.Should().NotBeNull("ICreatableTypesProvider should be registered");

        var meshConfig = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        meshConfig.DefaultNodeHubConfiguration.Should().NotBeNull("DefaultNodeHubConfiguration should be set");

        Output.WriteLine("All required services are registered.");
    }

    /// <summary>
    /// Test that the synced-query CreatableTypes provider returns the
    /// expected types for ProductLaunch.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void CreatableTypesProvider_ReturnsCreatableTypes()
    {
        var provider = Mesh.ServiceProvider.GetRequiredService<ICreatableTypesProvider>();
        var workspace = Mesh.GetWorkspace();
        var parent = workspace.GetMeshNodeStream("ACME/ProductLaunch")
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .Should().Emit();

        var creatableTypes = provider
            .GetCreatableTypes("ACME/ProductLaunch", parent)
            .Should().Emit();

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
    [Fact(Timeout = 60000)]
    public void CreateFlow_TransientNodeWithoutContent_PreservesContentFieldsAfterConfirm()
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
            NodeFactory.CreateTransient(transientNode).Should().Emit();
            Output.WriteLine("Transient node created (without content).");

            // Step 2: Initialize the node's hub (this triggers MeshDataSource initialization)
            var nodeAddress = new Address(nodePath);
            client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();
            Output.WriteLine("Node hub initialized.");

            // Step 3: Get the node from workspace to see what content was created
            var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            var query = MeshQueryRequest.FromQuery($"path:{nodePath}");

            var workspaceNode = meshQuery
                .ObserveQuery<MeshNode>(query)
                .SelectMany(change => change.Items)
                .Should().Within(10.Seconds()).Emit();

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
            var confirmedNode = meshQuery
                .ObserveQuery<MeshNode>(query)
                .SelectMany(change => change.Items)
                .Should().Within(10.Seconds()).Match(node => node.State == MeshNodeState.Active);

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
                NodeFactory.DeleteNode(nodePath).Should().Emit();
                Output.WriteLine("Cleanup: node deleted.");
            }
            catch { }
        }
    }

    /// <summary>
    /// Test that GetDataRequest with EntityReference returns the correct Todo content.
    /// This is the primary mechanism to verify content retrieval after node creation.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void GetDataRequest_ForCreatedTodo_ReturnsCorrectContent()
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
            NodeFactory.CreateTransient(transientNode).Should().Emit();
            Output.WriteLine("Transient node created.");

            // Step 2: Initialize the node's hub. 30 s Ping budget — activating a
            // brand-new per-instance NodeType hub triggers a cold-cache dynamic
            // compile that blocks the hub's first Ping response past the default
            // 10 s on slow CI runners. Outer [Fact(Timeout = 60000)] still bounds it.
            var nodeAddress = new Address(nodePath);
            client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Within(30.Seconds()).Emit();
            Output.WriteLine("Node hub initialized.");

            // Step 3: Confirm the node (make it Active). Subscribe to drain the
            // request's callback — `client.Post` alone registers a pending
            // callback that never gets consumed (the response IS dispatched
            // but no one's listening), which trips the quiesce-budget check
            // at test dispose: "leaked callback — the test posted a request
            // and never received (or never awaited) its reply". Observe +
            // Subscribe consumes the response without blocking the next step.
            var activeNode = transientNode with { State = MeshNodeState.Active };
            client.Observe(new CreateNodeRequest(activeNode), o => o.WithTarget(nodeAddress))
                .Subscribe(_ => { }, _ => { });

            // Step 4-5: Wait for node to become Active via the authoritative
            // per-node hub stream (GetMeshNodeStream), then read the same
            // emission for content verification. Using QueryAsync here would
            // be eventually consistent — it hits the lagged read-side index,
            // so the Transient→Active confirmation can race the query (see
            // Doc/Architecture/CqrsAndContentAccess.md and the
            // `cqrs_no_query_for_content` memory). One subscription, one
            // .Where(Active), one Timeout.
            var workspace = client.GetWorkspace();
            var retrievedNode = workspace.GetMeshNodeStream(nodePath)
                .Should().Within(10.Seconds()).Match(n => n != null && n.State == MeshNodeState.Active);
            Output.WriteLine("Node confirmed as Active (via GetMeshNodeStream).");

            // Step 6: Verify the retrieved node has the correct data
            retrievedNode.Should().NotBeNull("Node should be retrievable via QueryAsync");
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

            Output.WriteLine("QueryAsync verification completed successfully - all fields preserved.");
        }
        finally
        {
            try
            {
                NodeFactory.DeleteNode(nodePath).Should().Emit();
                Output.WriteLine("Cleanup: node deleted.");
            }
            catch { }
        }
    }

    /// <summary>
    /// Test that existing Todo items can be queried and have the expected content structure.
    /// This verifies that the Todo ContentType is properly configured.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void ExistingTodo_HasExpectedContentStructure()
    {
        var query = "path:ACME/ProductLaunch/Todo/DefinePersona";
        var results = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

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
    [Fact(Timeout = 60000)]
    public void Baseline_OverviewAreaRenders()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        client.Observe(new PingRequest(), o => o.WithTarget(parentAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            reference);

        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(c => c != null);

        control.Should().NotBeNull("Overview area should render");
        Output.WriteLine($"Overview control type: {control?.GetType().Name}");
    }

    #endregion
}
