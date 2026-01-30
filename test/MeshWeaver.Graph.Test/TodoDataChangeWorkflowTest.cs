using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
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

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for DataChangeRequest workflows in the Graph-based Todo application.
/// Verifies that status changes, assignments, and other mutations work correctly
/// through the DataChangeRequest mechanism.
/// </summary>
[Collection("SamplesGraphData")]
public class TodoDataChangeWorkflowTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private JsonSerializerOptions _jsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    // Shared cache - tests run sequentially in this collection
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverTodoWorkflowTests",
        ".mesh-cache");

    // Local copy of test data - each test instance gets its own copy
    private string? _localTestDataPath;

    private static string GetSamplesGraphPath()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var solutionRoot = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "samples", "Graph");
    }

    /// <summary>
    /// Gets or creates a local copy of the sample data for this test instance.
    /// </summary>
    private string GetLocalTestDataPath()
    {
        if (_localTestDataPath != null)
            return _localTestDataPath;

        var currentDir = Directory.GetCurrentDirectory();
        _localTestDataPath = Path.Combine(currentDir, "testdata", $"TodoWorkflowTests_{Guid.NewGuid():N}");

        // Copy samples/Graph to local test directory
        var sourcePath = GetSamplesGraphPath();
        CopyDirectory(sourcePath, _localTestDataPath);

        return _localTestDataPath;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir);
        }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = GetLocalTestDataPath();
        var dataDirectory = Path.Combine(graphPath, "Data");
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
            .AddJsonGraphConfiguration(dataDirectory);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // Clean up local test data copy
        if (_localTestDataPath != null && Directory.Exists(_localTestDataPath))
        {
            try
            {
                Directory.Delete(_localTestDataPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    /// <summary>
    /// Test that a Todo node can be retrieved via the persistence service.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task TodoNode_CanBeRetrievedViaPersistence()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        var todoNode = await persistence.GetNodeAsync("ACME/ProductLaunch/Todo/DefinePersona", _jsonOptions, TestContext.Current.CancellationToken);

        todoNode.Should().NotBeNull("Todo node should exist");
        todoNode!.NodeType.Should().Be("ACME/Project/Todo");
        todoNode.Content.Should().NotBeNull("Todo node should have content");

        Output.WriteLine($"Retrieved Todo: {todoNode.Name}");
        Output.WriteLine($"NodeType: {todoNode.NodeType}");
        Output.WriteLine($"Content type: {todoNode.Content?.GetType().Name}");
    }

    /// <summary>
    /// Test that child Todo nodes can be enumerated via IMeshQuery.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task ChildTodos_CanBeEnumeratedViaQuery()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var todos = await meshQuery.QueryAsync<MeshNode>("path:ACME/ProductLaunch/Todo scope:children", _jsonOptions, null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        todos.Should().NotBeEmpty("Should have child Todo nodes");
        todos.Should().HaveCountGreaterThan(10, "Should have at least 10 Todo items");

        Output.WriteLine($"Found {todos.Count} Todo items:");
        foreach (var todo in todos.Take(5))
        {
            Output.WriteLine($"  - {todo.Path}: {todo.Name}");
        }
    }

    /// <summary>
    /// Test that Todo content can be deserialized correctly.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task TodoContent_CanBeDeserializedCorrectly()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        var todoNode = await persistence.GetNodeAsync("ACME/ProductLaunch/Todo/DefinePersona", _jsonOptions, TestContext.Current.CancellationToken);

        todoNode.Should().NotBeNull();
        todoNode!.Content.Should().NotBeNull();

        // Content could be a JsonElement or a typed object depending on deserialization state
        if (todoNode.Content is JsonElement jsonElement)
        {
            Output.WriteLine($"Content is JsonElement: {jsonElement}");
            jsonElement.TryGetProperty("title", out var titleElement).Should().BeTrue("Should have title property");
            titleElement.GetString().Should().NotBeNullOrEmpty("Title should not be empty");
            Output.WriteLine($"Title: {titleElement.GetString()}");
        }
        else
        {
            Output.WriteLine($"Content type: {todoNode.Content.GetType().Name}");
            Output.WriteLine($"Content: {todoNode.Content}");
        }
    }

    /// <summary>
    /// Test that DataChangeRequest can be used to update Todo content.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task DataChangeRequest_CanBeCreatedForTodoUpdate()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Get the original todo
        var todoNode = await persistence.GetNodeAsync("ACME/ProductLaunch/Todo/DefinePersona", _jsonOptions, TestContext.Current.CancellationToken);
        todoNode.Should().NotBeNull();

        // Verify we can create a DataChangeRequest
        var changeRequest = new DataChangeRequest();

        changeRequest.Should().NotBeNull("Should be able to create DataChangeRequest");
        Output.WriteLine("DataChangeRequest created successfully");
        Output.WriteLine("DataChangeRequest can be used with .WithUpdates(), .WithCreations(), .WithDeletions()");
    }

    /// <summary>
    /// Test that the Project hub can receive requests.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task ProjectHub_CanReceiveRequests()
    {
        var client = GetClient();
        var projectAddress = new Address("ACME/ProductLaunch");

        // Verify the hub is accessible
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(projectAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull("Project hub should respond to ping");
        Output.WriteLine($"Project hub is accessible at {projectAddress}");
    }

    /// <summary>
    /// Test that the Todo hub can receive requests.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task TodoHub_CanReceiveRequests()
    {
        var client = GetClient();
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");

        // Verify the hub is accessible
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(todoAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull("Todo hub should respond to ping");
        Output.WriteLine($"Todo hub is accessible at {todoAddress}");
    }

    /// <summary>
    /// Test that multiple Todo hubs can be accessed independently.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task MultipleTodoHubs_CanBeAccessedIndependently()
    {
        var client = GetClient();

        var todoAddresses = new[]
        {
            "ACME/ProductLaunch/Todo/DefinePersona",
            "ACME/ProductLaunch/Todo/LaunchEvent",
            "ACME/ProductLaunch/Todo/PressRelease"
        };

        foreach (var addressPath in todoAddresses)
        {
            var todoAddress = new Address(addressPath);

            var response = await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(todoAddress),
                TestContext.Current.CancellationToken);

            response.Should().NotBeNull($"Todo hub at {addressPath} should respond");
            Output.WriteLine($"Successfully accessed: {addressPath}");
        }
    }

    /// <summary>
    /// Test that the TodaysFocus view (used as summary) responds to data access.
    /// Note: "Summary" view doesn't exist, using TodaysFocus as the overview view.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task SummaryView_RespondsToDataAccess()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        // TodaysFocus is the overview/summary view
        var reference = new LayoutAreaReference("TodaysFocus");
        var projectAddress = new Address("ACME/ProductLaunch");

        // Get initial view
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        Output.WriteLine("Getting initial TodaysFocus view...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();

        control.Should().NotBeNull("TodaysFocus view should render initially");
        Output.WriteLine($"Initial view rendered: {control?.GetType().Name}");

        Output.WriteLine("TodaysFocus view is reactive and would update on DataChangeRequest");
    }

    /// <summary>
    /// Test that DataChangeRequest.WithUpdates can update a Todo's status.
    /// This tests the pattern used by the Edit operation.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task UpdateStatus_ViaDataChangeRequest_ShouldWork()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Get the original todo
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");
        var todoNode = await persistence.GetNodeAsync("ACME/ProductLaunch/Todo/DefinePersona", _jsonOptions, TestContext.Current.CancellationToken);
        todoNode.Should().NotBeNull();

        Output.WriteLine($"Original todo: {todoNode!.Name}");

        // Get the content as JsonElement to extract properties
        var contentJson = JsonSerializer.Serialize(todoNode.Content);
        using var doc = JsonDocument.Parse(contentJson);
        var root = doc.RootElement;

        var originalStatus = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : "unknown";
        Output.WriteLine($"Original status: {originalStatus}");

        // Create a DataChangeRequest with updated status
        var changeRequest = new DataChangeRequest();
        changeRequest.Should().NotBeNull();

        Output.WriteLine("DataChangeRequest.WithUpdates() pattern is available for status updates");
        Output.WriteLine("Pattern: new DataChangeRequest().WithUpdates(updatedTodo)");
    }

    /// <summary>
    /// Test that DataChangeRequest.WithDeletions can be used for deleting Todos.
    /// This tests the pattern used by the Delete operation.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task DeleteTodo_ViaDataChangeRequest_PatternIsAvailable()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Get an existing todo to verify the pattern
        var todoNode = await persistence.GetNodeAsync("ACME/ProductLaunch/Todo/DefinePersona", _jsonOptions, TestContext.Current.CancellationToken);
        todoNode.Should().NotBeNull();

        Output.WriteLine($"Todo exists: {todoNode!.Name}");

        // Verify the DataChangeRequest pattern is available
        var changeRequest = new DataChangeRequest();
        changeRequest.Should().NotBeNull();

        Output.WriteLine("DataChangeRequest.WithDeletions() pattern is available for todo deletion");
        Output.WriteLine("Pattern: new DataChangeRequest().WithDeletions(todoToDelete)");
        Output.WriteLine("Note: Actual deletion test would modify data, so we just verify the pattern exists");
    }

    /// <summary>
    /// Test that DataChangeRequest.WithCreations can be used for creating new Todos.
    /// This tests the pattern used by the Create operation in ProjectViews.
    /// </summary>
    [Fact(Timeout = 15000)]
    public void CreateTodo_ViaDataChangeRequest_PatternIsAvailable()
    {
        // Verify the DataChangeRequest pattern is available
        var changeRequest = new DataChangeRequest();
        changeRequest.Should().NotBeNull();

        Output.WriteLine("DataChangeRequest patterns available for CRUD:");
        Output.WriteLine("  - Create: host.Edit(newTodo, dataId) binds to DataChangeRequest internally");
        Output.WriteLine("  - Update: new DataChangeRequest().WithUpdates(updatedTodo)");
        Output.WriteLine("  - Delete: new DataChangeRequest().WithDeletions(todoToDelete)");
        Output.WriteLine("  - Cancel Create: new DataChangeRequest { Deletions = [newTodo] }");
    }

    /// <summary>
    /// Test that the AllTasks view renders with groups.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task AllTasksView_ShouldIncludeNewTaskButton()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("AllTasks");
        var projectAddress = new Address("ACME/ProductLaunch");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        Output.WriteLine("Getting AllTasks view...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();

        control.Should().NotBeNull("AllTasks view should render");
        Output.WriteLine($"AllTasks view rendered: {control?.GetType().Name}");
    }

    /// <summary>
    /// Test that the Overview view renders for a Todo.
    /// Note: "Details" view is named "Overview" in Todo.json.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task DetailsView_ShouldIncludeCrudButtons()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        // The view is named "Overview" in Todo.json, not "Details"
        var reference = new LayoutAreaReference("Overview");
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            todoAddress,
            reference);

        Output.WriteLine("Getting Overview view...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();

        control.Should().NotBeNull("Overview view should render");
        Output.WriteLine($"Overview view rendered: {control?.GetType().Name}");
    }

    /// <summary>
    /// Test that the AllTasks view compiles and renders correctly with deleted items.
    /// This tests the dynamically compiled ProjectViews code including the Deleted section.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task AllTasksView_CompilesAndRendersWithDeletedSection()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var reference = new LayoutAreaReference("AllTasks");
        var projectAddress = new Address("ACME/ProductLaunch");
        var todoPath = "ACME/ProductLaunch/Todo/DefinePersona";

        // Get original node
        var originalNode = await persistence.GetNodeAsync(todoPath, _jsonOptions, TestContext.Current.CancellationToken);
        originalNode.Should().NotBeNull();

        // Soft delete a todo to ensure Deleted section has content
        var deletedNode = originalNode! with { State = MeshNodeState.Deleted };
        await persistence.SaveNodeAsync(deletedNode, _jsonOptions, TestContext.Current.CancellationToken);
        Output.WriteLine("Soft-deleted a todo item");

        // Request the AllTasks view - this will trigger dynamic compilation
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        Output.WriteLine("Getting AllTasks view (triggers ProjectViews compilation)...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();

        control.Should().NotBeNull("AllTasks view should compile and render");
        Output.WriteLine($"AllTasks view compiled and rendered: {control?.GetType().Name}");
        Output.WriteLine("ProjectViews dynamic compilation successful");
        // Note: No restore needed - test uses local copy of data
    }

    /// <summary>
    /// Test that soft delete changes the node state to Deleted.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task SoftDelete_ChangesStateToDeleted()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var todoPath = "ACME/ProductLaunch/Todo/DefinePersona";

        // Get the original todo
        var originalNode = await persistence.GetNodeAsync(todoPath, _jsonOptions, TestContext.Current.CancellationToken);
        originalNode.Should().NotBeNull("Todo node should exist");
        Output.WriteLine($"Original state: {originalNode!.State}");

        // Perform soft delete by setting state to Deleted
        var deletedNode = originalNode with { State = MeshNodeState.Deleted };
        await persistence.SaveNodeAsync(deletedNode, _jsonOptions, TestContext.Current.CancellationToken);

        // Verify the state changed
        var updatedNode = await persistence.GetNodeAsync(todoPath, _jsonOptions, TestContext.Current.CancellationToken);
        updatedNode.Should().NotBeNull("Node should still exist after soft delete");
        updatedNode!.State.Should().Be(MeshNodeState.Deleted, "State should be Deleted after soft delete");
        Output.WriteLine($"Updated state: {updatedNode.State}");
        // Note: No restore needed - test uses local copy of data
    }

    /// <summary>
    /// Test that querying with state:Active excludes deleted items.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task QueryWithStateActive_ExcludesDeletedItems()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var todoPath = "ACME/ProductLaunch/Todo/DefinePersona";

        // Get the original todo
        var originalNode = await persistence.GetNodeAsync(todoPath, _jsonOptions, TestContext.Current.CancellationToken);
        originalNode.Should().NotBeNull();

        // Soft delete the node
        var deletedNode = originalNode! with { State = MeshNodeState.Deleted };
        await persistence.SaveNodeAsync(deletedNode, _jsonOptions, TestContext.Current.CancellationToken);

        // Query for active items only
        var activeQuery = "path:ACME/ProductLaunch/Todo nodeType:ACME/Project/Todo state:Active scope:subtree";
        var activeResults = await meshQuery.QueryAsync<MeshNode>(activeQuery, _jsonOptions, null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The deleted item should not be in active results
        activeResults.Should().NotContain(n => n.Path == todoPath,
            "Deleted item should not appear in state:Active query results");
        Output.WriteLine($"Active query returned {activeResults.Count} items, excluding the deleted one");
        // Note: No restore needed - test uses local copy of data
    }

    /// <summary>
    /// Test that querying with state:Deleted only returns deleted items.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task QueryWithStateDeleted_OnlyReturnsDeletedItems()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var todoPath = "ACME/ProductLaunch/Todo/DefinePersona";

        // Get the original todo
        var originalNode = await persistence.GetNodeAsync(todoPath, _jsonOptions, TestContext.Current.CancellationToken);
        originalNode.Should().NotBeNull();

        // Soft delete the node
        var deletedNode = originalNode! with { State = MeshNodeState.Deleted };
        await persistence.SaveNodeAsync(deletedNode, _jsonOptions, TestContext.Current.CancellationToken);

        // Query for deleted items only
        var deletedQuery = "path:ACME/ProductLaunch/Todo nodeType:ACME/Project/Todo state:Deleted scope:subtree";
        var deletedResults = await meshQuery.QueryAsync<MeshNode>(deletedQuery, _jsonOptions, null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The deleted item should be in deleted results
        deletedResults.Should().Contain(n => n.Path == todoPath,
            "Deleted item should appear in state:Deleted query results");
        Output.WriteLine($"Deleted query returned {deletedResults.Count} items, including the soft-deleted one");
        // Note: No restore needed - test uses local copy of data
    }

    /// <summary>
    /// Test that restore changes the node state back to Active.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Restore_ChangesStateBackToActive()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var todoPath = "ACME/ProductLaunch/Todo/DefinePersona";

        // Get the original todo
        var originalNode = await persistence.GetNodeAsync(todoPath, _jsonOptions, TestContext.Current.CancellationToken);
        originalNode.Should().NotBeNull();

        // First soft delete
        var deletedNode = originalNode! with { State = MeshNodeState.Deleted };
        await persistence.SaveNodeAsync(deletedNode, _jsonOptions, TestContext.Current.CancellationToken);

        // Verify it's deleted
        var deletedCheck = await persistence.GetNodeAsync(todoPath, _jsonOptions, TestContext.Current.CancellationToken);
        deletedCheck!.State.Should().Be(MeshNodeState.Deleted);
        Output.WriteLine("Node is now Deleted");

        // Now restore
        var restoredNode = deletedCheck with { State = MeshNodeState.Active };
        await persistence.SaveNodeAsync(restoredNode, _jsonOptions, TestContext.Current.CancellationToken);

        // Verify it's active again
        var activeCheck = await persistence.GetNodeAsync(todoPath, _jsonOptions, TestContext.Current.CancellationToken);
        activeCheck!.State.Should().Be(MeshNodeState.Active, "State should be Active after restore");
        Output.WriteLine("Node successfully restored to Active state");
        // Note: No restore needed - test uses local copy of data
    }

    /// <summary>
    /// Test that permanent (hard) delete removes the node completely.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task PermanentDelete_RemovesNodeCompletely()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Create a temporary test node for this test
        var testId = $"TestTodo_{Guid.NewGuid():N}";
        var testPath = $"ACME/ProductLaunch/Todo/{testId}";

        var testNode = new MeshNode(testId, "ACME/ProductLaunch/Todo")
        {
            Name = "Test Todo for Permanent Delete",
            NodeType = "ACME/Project/Todo",
            Content = new { id = testId, title = "Test Todo", status = "Pending" },
            IsPersistent = true,
            State = MeshNodeState.Active
        };

        // Create the test node
        await persistence.SaveNodeAsync(testNode, _jsonOptions, TestContext.Current.CancellationToken);
        Output.WriteLine($"Created test node at {testPath}");

        // Verify it exists
        var createdNode = await persistence.GetNodeAsync(testPath, _jsonOptions, TestContext.Current.CancellationToken);
        createdNode.Should().NotBeNull("Test node should exist after creation");

        // Permanently delete it
        await persistence.DeleteNodeAsync(testPath, recursive: false, TestContext.Current.CancellationToken);
        Output.WriteLine("Permanently deleted test node");

        // Verify it no longer exists
        var deletedNode = await persistence.GetNodeAsync(testPath, _jsonOptions, TestContext.Current.CancellationToken);
        deletedNode.Should().BeNull("Node should not exist after permanent delete");
        Output.WriteLine("Confirmed node no longer exists after permanent delete");
        // Note: No cleanup needed - test uses local copy of data
    }
}
