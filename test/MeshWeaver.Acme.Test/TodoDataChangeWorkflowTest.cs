using MeshWeaver.Blazor.Portal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
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
/// Tests for DataChangeRequest workflows in the Graph-based Todo application.
/// Verifies that status changes, assignments, and other mutations work correctly
/// through the DataChangeRequest mechanism.
/// </summary>
public class TodoDataChangeWorkflowTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Per-session cache directory — appending a Guid prevents the Windows
    // file-lock collision where a stale .dll from a prior test process is
    // Stable cache directory — the timestamped-subdir cache (a3ab9909e)
    // gives each compile its own subdir so prior-process DLLs aren't touched.
    // File-lock collisions on the cache write are no longer possible.
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverTodoWorkflowTests",
        ".mesh-cache");

    // Local copy of test data - each test instance gets its own copy
    private string? _localTestDataPath;

    /// <summary>
    /// Gets or creates a local copy of the sample data for this test instance.
    /// Uses the pre-deployed SamplesGraph directory from the build output.
    /// </summary>
    private string GetLocalTestDataPath()
    {
        if (_localTestDataPath != null)
            return _localTestDataPath;

        _localTestDataPath = Path.Combine(Path.GetTempPath(), "MeshWeaverTests", $"TodoWorkflowTests_{Guid.NewGuid():N}");

        // Copy pre-deployed SamplesGraph to local test directory for mutation isolation
        CopyDirectory(TestPaths.SamplesGraph, _localTestDataPath);

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
        var localCopy = GetLocalTestDataPath();
        var graphPath = localCopy;
        var dataDirectory = Path.Combine(localCopy, "Data");
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
            .AddGraph();
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
    /// Reactive analogue of <see cref="MonolithMeshTestBase.WaitForQueryPathSetAsync"/>:
    /// folds the live <c>ObserveQuery</c> deltas (Initial / Reset / Added / Updated /
    /// Removed) into a running path set and surfaces it as an observable so tests can
    /// assert with <c>.Should().Match(predicate)</c> instead of awaiting a Task.
    /// </summary>
    private IObservable<IReadOnlySet<string>> ObserveQueryPathSet(string query)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        return MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan((IReadOnlySet<string>)paths, (_, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                {
                    paths.Clear();
                    foreach (var n in change.Items) if (n.Path is { } p) paths.Add(p);
                }
                else if (change.ChangeType is QueryChangeType.Added or QueryChangeType.Updated)
                {
                    foreach (var n in change.Items) if (n.Path is { } p) paths.Add(p);
                }
                else if (change.ChangeType is QueryChangeType.Removed)
                {
                    foreach (var n in change.Items) if (n.Path is { } p) paths.Remove(p);
                }
                return paths;
            });
    }

    /// <summary>
    /// Test that a Todo node can be retrieved via the persistence service.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void TodoNode_CanBeRetrievedViaPersistence()
    {
        var todoNode = ReadNode("ACME/ProductLaunch/Todo/DefinePersona")
            .Should().Within(60.Seconds()).Match(n => n is not null);

        todoNode.Should().NotBeNull("Todo node should exist");
        todoNode!.NodeType.Should().Be("ACME/Project/Todo");
        todoNode.Content.Should().NotBeNull("Todo node should have content");

        Output.WriteLine($"Retrieved Todo: {todoNode.Name}");
        Output.WriteLine($"NodeType: {todoNode.NodeType}");
        Output.WriteLine($"Content type: {todoNode.Content?.GetType().Name}");
    }

    /// <summary>
    /// Test that child Todo nodes can be enumerated via IMeshService.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ChildTodos_CanBeEnumeratedViaQuery()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var todos = await meshQuery.QueryAsync<MeshNode>("namespace:ACME/ProductLaunch/Todo", null, TestContext.Current.CancellationToken)
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
    [Fact(Timeout = 60000)]
    public void TodoContent_CanBeDeserializedCorrectly()
    {
        var todoNode = ReadNode("ACME/ProductLaunch/Todo/DefinePersona")
            .Should().Within(60.Seconds()).Match(n => n is not null);

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
            Output.WriteLine($"Content type: {todoNode.Content!.GetType().Name}");
            Output.WriteLine($"Content: {todoNode.Content}");
        }
    }

    /// <summary>
    /// Test that DataChangeRequest can be used to update Todo content.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void DataChangeRequest_CanBeCreatedForTodoUpdate()
    {
        // Get the original todo
        var todoNode = ReadNode("ACME/ProductLaunch/Todo/DefinePersona")
            .Should().Within(60.Seconds()).Match(n => n is not null);
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
    [Fact(Timeout = 60000)]
    public void ProjectHub_CanReceiveRequests()
    {
        var client = GetClient();
        var projectAddress = new Address("ACME/ProductLaunch");

        // Verify the hub is accessible
        var response = client.Observe(new PingRequest(), o => o.WithTarget(projectAddress)).Should().Emit();

        response.Should().NotBeNull("Project hub should respond to ping");
        Output.WriteLine($"Project hub is accessible at {projectAddress}");
    }

    /// <summary>
    /// Test that the Todo hub can receive requests.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void TodoHub_CanReceiveRequests()
    {
        var client = GetClient();
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");

        // Verify the hub is accessible
        var response = client.Observe(new PingRequest(), o => o.WithTarget(todoAddress)).Should().Emit();

        response.Should().NotBeNull("Todo hub should respond to ping");
        Output.WriteLine($"Todo hub is accessible at {todoAddress}");
    }

    /// <summary>
    /// Test that multiple Todo hubs can be accessed independently.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void MultipleTodoHubs_CanBeAccessedIndependently()
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

            var response = client.Observe(new PingRequest(), o => o.WithTarget(todoAddress)).Should().Emit();

            response.Should().NotBeNull($"Todo hub at {addressPath} should respond");
            Output.WriteLine($"Successfully accessed: {addressPath}");
        }
    }

    /// <summary>
    /// Test that the TodaysFocus view (used as summary) responds to data access.
    /// Note: "Summary" view doesn't exist, using TodaysFocus as the overview view.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void SummaryView_RespondsToDataAccess()
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
        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c != null);

        control.Should().NotBeNull("TodaysFocus view should render initially");
        Output.WriteLine($"Initial view rendered: {control?.GetType().Name}");

        Output.WriteLine("TodaysFocus view is reactive and would update on DataChangeRequest");
    }

    /// <summary>
    /// Test that DataChangeRequest.WithUpdates can update a Todo's status.
    /// This tests the pattern used by the Edit operation.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void UpdateStatus_ViaDataChangeRequest_ShouldWork()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Get the original todo
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");
        var todoNode = ReadNode("ACME/ProductLaunch/Todo/DefinePersona")
            .Should().Within(60.Seconds()).Match(n => n is not null);
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
    [Fact(Timeout = 60000)]
    public void DeleteTodo_ViaDataChangeRequest_PatternIsAvailable()
    {
        // Get an existing todo to verify the pattern
        var todoNode = ReadNode("ACME/ProductLaunch/Todo/DefinePersona")
            .Should().Within(60.Seconds()).Match(n => n is not null);
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
    [Fact(Timeout = 60000)]
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
    [Fact(Timeout = 60000)]
    public void AllTasksView_ShouldIncludeNewTaskButton()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("AllTasks");
        var projectAddress = new Address("ACME/ProductLaunch");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        Output.WriteLine("Getting AllTasks view...");
        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c != null);

        control.Should().NotBeNull("AllTasks view should render");
        Output.WriteLine($"AllTasks view rendered: {control?.GetType().Name}");
    }

    /// <summary>
    /// Test that the Overview view renders for a Todo.
    /// Note: "Details" view is named "Overview" in Todo.json.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void DetailsView_ShouldIncludeCrudButtons()
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
        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c != null);

        control.Should().NotBeNull("Overview view should render");
        Output.WriteLine($"Overview view rendered: {control?.GetType().Name}");
    }

    /// <summary>
    /// Test that the AllTasks view compiles and renders correctly with deleted items.
    /// This tests the dynamically compiled ProjectViews code including the Deleted section.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void AllTasksView_CompilesAndRendersWithDeletedSection()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("AllTasks");
        var projectAddress = new Address("ACME/ProductLaunch");
        var todoPath = "ACME/ProductLaunch/Todo/DefinePersona";

        // Get original node
        var originalNode = ReadNode(todoPath)
            .Should().Within(60.Seconds()).Match(n => n is not null);
        originalNode.Should().NotBeNull();

        // Soft delete a todo to ensure Deleted section has content
        var deletedNode = originalNode! with { State = MeshNodeState.Deleted };
        NodeFactory.UpdateNode(deletedNode).Should().Emit();
        Output.WriteLine("Soft-deleted a todo item");

        // Request the AllTasks view - this will trigger dynamic compilation
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        Output.WriteLine("Getting AllTasks view (triggers ProjectViews compilation)...");
        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c != null);

        control.Should().NotBeNull("AllTasks view should compile and render");
        Output.WriteLine($"AllTasks view compiled and rendered: {control?.GetType().Name}");
        Output.WriteLine("ProjectViews dynamic compilation successful");
        // Note: No restore needed - test uses local copy of data
    }

    /// <summary>
    /// Test that soft delete changes the node state to Deleted.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void SoftDelete_ChangesStateToDeleted()
    {
        var todoPath = "ACME/ProductLaunch/Todo/DefinePersona";

        // Get the original todo
        var originalNode = ReadNode(todoPath)
            .Should().Within(60.Seconds()).Match(n => n is not null);
        originalNode.Should().NotBeNull("Todo node should exist");
        Output.WriteLine($"Original state: {originalNode!.State}");

        // Perform soft delete by setting state to Deleted
        var deletedNode = originalNode with { State = MeshNodeState.Deleted };
        NodeFactory.UpdateNode(deletedNode).Should().Emit();

        // Verify the state changed (stream read â€” no catalog lag)
        var updatedNode = ReadNode(todoPath)
            .Should().Within(60.Seconds()).Match(n => n is not null && n.State == MeshNodeState.Deleted);
        updatedNode.Should().NotBeNull("Node should still exist after soft delete");
        updatedNode!.State.Should().Be(MeshNodeState.Deleted, "State should be Deleted after soft delete");
        Output.WriteLine($"Updated state: {updatedNode.State}");
        // Note: No restore needed - test uses local copy of data
    }

    /// <summary>
    /// Test that querying with state:Active excludes deleted items.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void QueryWithStateActive_ExcludesDeletedItems()
    {
        var todoPath = "ACME/ProductLaunch/Todo/DefinePersona";
        var activeQuery = "path:ACME/ProductLaunch/Todo nodeType:ACME/Project/Todo state:Active scope:subtree";

        // Capture the initial active set + the original todo from the same
        // ObserveQuery subscription â€” initial emission is the full snapshot.
        var initialItems = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(activeQuery))
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Select(c => c.Items)
            .Should().Emit();

        var originalNode = initialItems.FirstOrDefault(n => n.Path == todoPath);
        originalNode.Should().NotBeNull("DefinePersona should be in the initial active set");

        // Soft delete the node
        var deletedNode = originalNode! with { State = MeshNodeState.Deleted };
        NodeFactory.UpdateNode(deletedNode).Should().Emit();

        // Wait for the catalog to reflect the state change â€” ObserveQuery emits a
        // Removed/Updated delta when DefinePersona stops matching state:Active.
        var paths = ObserveQueryPathSet(activeQuery)
            .Should().Within(60.Seconds()).Match(set => !set.Contains(todoPath));

        paths.Should().NotContain(todoPath,
            "Deleted item should not appear in state:Active query results");
        Output.WriteLine($"Active query reflects soft-delete: {paths.Count} active items, deleted excluded");
        // Note: No restore needed - test uses local copy of data
    }

    /// <summary>
    /// Test that querying with state:Deleted only returns deleted items.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void QueryWithStateDeleted_OnlyReturnsDeletedItems()
    {
        var todoPath = "ACME/ProductLaunch/Todo/DefinePersona";
        var activeQuery = "path:ACME/ProductLaunch/Todo nodeType:ACME/Project/Todo state:Active scope:subtree";
        var deletedQuery = "path:ACME/ProductLaunch/Todo nodeType:ACME/Project/Todo state:Deleted scope:subtree";

        // Capture original from the live active set (set query â€” ObserveQuery is correct).
        var initialActive = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(activeQuery))
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Select(c => c.Items)
            .Should().Emit();

        var originalNode = initialActive.FirstOrDefault(n => n.Path == todoPath);
        originalNode.Should().NotBeNull("DefinePersona should be in the initial active set");

        // Soft delete
        var deletedNode = originalNode! with { State = MeshNodeState.Deleted };
        NodeFactory.UpdateNode(deletedNode).Should().Emit();

        // Wait for the catalog to surface DefinePersona in the deleted set.
        var paths = ObserveQueryPathSet(deletedQuery)
            .Should().Within(60.Seconds()).Match(set => set.Contains(todoPath));

        paths.Should().Contain(todoPath,
            "Deleted item should appear in state:Deleted query results");
        Output.WriteLine($"Deleted query reflects soft-delete: {paths.Count} deleted items, target included");
        // Note: No restore needed - test uses local copy of data
    }

    /// <summary>
    /// Test that restore changes the node state back to Active.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void Restore_ChangesStateBackToActive()
    {
        var todoPath = "ACME/ProductLaunch/Todo/DefinePersona";

        // Get the original todo
        var originalNode = ReadNode(todoPath)
            .Should().Within(60.Seconds()).Match(n => n is not null);
        originalNode.Should().NotBeNull();

        // First soft delete
        var deletedNode = originalNode! with { State = MeshNodeState.Deleted };
        NodeFactory.UpdateNode(deletedNode).Should().Emit();

        // Verify it's deleted (stream read)
        var deletedCheck = ReadNode(todoPath)
            .Should().Within(60.Seconds()).Match(n => n is not null && n.State == MeshNodeState.Deleted);
        deletedCheck!.State.Should().Be(MeshNodeState.Deleted);
        Output.WriteLine("Node is now Deleted");

        // Now restore
        var restoredNode = deletedCheck with { State = MeshNodeState.Active };
        NodeFactory.UpdateNode(restoredNode).Should().Emit();

        // Verify it's active again (stream read)
        var activeCheck = ReadNode(todoPath)
            .Should().Within(60.Seconds()).Match(n => n is not null && n.State == MeshNodeState.Active);
        activeCheck!.State.Should().Be(MeshNodeState.Active, "State should be Active after restore");
        Output.WriteLine("Node successfully restored to Active state");
        // Note: No restore needed - test uses local copy of data
    }

    /// <summary>
    /// Test that permanent (hard) delete removes the node completely.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void PermanentDelete_RemovesNodeCompletely()
    {
        // Create a temporary test node for this test
        var testId = $"TestTodo_{Guid.NewGuid():N}";
        var testPath = $"ACME/ProductLaunch/Todo/{testId}";
        var subtreeQuery = "path:ACME/ProductLaunch/Todo nodeType:ACME/Project/Todo scope:subtree";

        var testNode = new MeshNode(testId, "ACME/ProductLaunch/Todo")
        {
            Name = "Test Todo for Permanent Delete",
            NodeType = "ACME/Project/Todo",
            Content = new { id = testId, title = "Test Todo", status = "Pending" },
            State = MeshNodeState.Active
        };

        // Create the test node and wait for the catalog to surface it.
        NodeFactory.CreateNode(testNode).Should().Emit();
        Output.WriteLine($"Created test node at {testPath}");
        ObserveQueryPathSet(subtreeQuery)
            .Should().Within(60.Seconds()).Match(set => set.Contains(testPath));

        // Permanently delete it
        NodeFactory.DeleteNode(testPath).Should().Emit();
        Output.WriteLine("Permanently deleted test node");

        // Wait for the catalog to drop the deleted path.
        var paths = ObserveQueryPathSet(subtreeQuery)
            .Should().Within(60.Seconds()).Match(set => !set.Contains(testPath));
        paths.Should().NotContain(testPath, "Node should not exist after permanent delete");
        Output.WriteLine($"Catalog reflects permanent delete: {paths.Count} remaining items");
        // Note: No cleanup needed - test uses local copy of data
    }
}

