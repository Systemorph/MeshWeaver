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
/// Tests for Project-level aggregate views (Summary, AllItems, TodosByCategory, Planning, MyTasks, Backlog, TodaysFocus).
/// These views aggregate data from child Todo items in the ProductLaunch project.
/// </summary>
[Collection("ProjectTodoViewsTests")]
public class ProjectTodoViewsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverProjectViewTests",
        ".mesh-cache");

    private static string GetSamplesGraphPath()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var solutionRoot = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "samples", "Graph");
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = GetSamplesGraphPath();
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

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    #region Query Tests

    /// <summary>
    /// Test that IMeshQuery can find Todo nodes by nodeType.
    /// This is a prerequisite for the views to work correctly.
    /// Uses the same query pattern as ProjectViews: path:{project}/Todo scope:children
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task MeshQuery_ShouldFindTodosByNodeType()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        // Query for all Todo items under ACME/ProductLaunch/Todo (same pattern used by ProjectViews)
        var query = "path:ACME/ProductLaunch/Todo nodeType:ACME/Project/Todo scope:children";
        Output.WriteLine($"Querying: {query}");

        var results = await meshQuery.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery(query), ct: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Count} Todo items:");
        foreach (var node in results.Take(10))
        {
            Output.WriteLine($"  - {node.Path}: {node.Name} (NodeType: {node.NodeType})");
        }

        results.Should().NotBeEmpty("Should find Todo items under ACME/ProductLaunch/Todo");
        results.Should().OnlyContain(n => n.NodeType == "ACME/Project/Todo", "All results should be Todo nodes");
    }

    /// <summary>
    /// Test that IMeshQuery can find all 21 Todo nodes.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task MeshQuery_ShouldFindAll21TodoNodes()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        // Query for all Todo items - we have 21 sample Todo items
        var query = "path:ACME/ProductLaunch/Todo nodeType:ACME/Project/Todo scope:children";
        Output.WriteLine($"Querying: {query}");

        var results = await meshQuery.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery(query), ct: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Count} Todo items:");
        foreach (var node in results)
        {
            Output.WriteLine($"  - {node.Path}: {node.Name} (NodeType: {node.NodeType})");
        }

        results.Should().HaveCount(21, "Should find all 21 Todo sample items");
        results.Should().OnlyContain(n => n.NodeType == "ACME/Project/Todo", "All results should be Todo nodes");
    }

    /// <summary>
    /// Test that IMeshQuery filters by nodeType correctly.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task MeshQuery_ShouldFilterByNodeType()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        // Query without nodeType filter
        var queryWithoutFilter = "path:ACME/ProductLaunch/Todo scope:children";
        var allResults = await meshQuery.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery(queryWithoutFilter), ct: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Query with nodeType filter
        var queryWithFilter = "path:ACME/ProductLaunch/Todo nodeType:ACME/Project/Todo scope:children";
        var filteredResults = await meshQuery.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery(queryWithFilter), ct: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Without filter: {allResults.Count}, With filter: {filteredResults.Count}");

        filteredResults.Should().NotBeEmpty("Should find Todo nodes with filter");
        filteredResults.Should().OnlyContain(n => n.NodeType == "ACME/Project/Todo", "All filtered results should be Todo nodes");
    }

    #endregion

    #region View Tests

    /// <summary>
    /// Test that the Summary view renders with task statistics.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Summary_ShouldRenderWithData()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference("Summary");
        var projectAddress = new Address("ACME/ProductLaunch");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is LayoutGridControl { Areas.Count: > 2 })
            .Timeout(30.Seconds())
            .FirstAsync();

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;
        grid.Areas.Should().HaveCountGreaterThan(2, "should have multiple areas with statistics");
        Output.WriteLine($"Grid has {grid.Areas.Count} areas");
    }

    /// <summary>
    /// Test that the AllItems view renders with tasks grouped by status.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AllItems_ShouldRenderWithData()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference("AllItems");
        var projectAddress = new Address("ACME/ProductLaunch");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is LayoutGridControl { Areas.Count: > 2 })
            .Timeout(30.Seconds())
            .FirstAsync();

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;
        grid.Areas.Should().HaveCountGreaterThan(2, "should have multiple areas with todo items");
        Output.WriteLine($"Grid has {grid.Areas.Count} areas");
    }

    /// <summary>
    /// Test that the TodosByCategory view renders with actual task categories.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TodosByCategory_ShouldRenderWithData()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference("TodosByCategory");
        var projectAddress = new Address("ACME/ProductLaunch");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        // Wait for LayoutGridControl with areas (the actual data view after loading completes)
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is LayoutGridControl { Areas.Count: > 2 })
            .Timeout(30.Seconds())
            .FirstAsync();

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;
        grid.Areas.Should().HaveCountGreaterThan(2, "should have multiple areas with categories");
        Output.WriteLine($"Grid has {grid.Areas.Count} areas - Todo items loaded successfully");
    }

    /// <summary>
    /// Test that the Planning view renders with data.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Planning_ShouldRenderWithData()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference("Planning");
        var projectAddress = new Address("ACME/ProductLaunch");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is LayoutGridControl { Areas.Count: > 2 })
            .Timeout(30.Seconds())
            .FirstAsync();

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;
        grid.Areas.Should().HaveCountGreaterThan(2);
        Output.WriteLine($"Grid has {grid.Areas.Count} areas");
    }

    /// <summary>
    /// Test that the MyTasks view renders with data.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task MyTasks_ShouldRenderWithData()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference("MyTasks");
        var projectAddress = new Address("ACME/ProductLaunch");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is LayoutGridControl { Areas.Count: > 2 })
            .Timeout(30.Seconds())
            .FirstAsync();

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;
        grid.Areas.Should().HaveCountGreaterThan(2);
        Output.WriteLine($"Grid has {grid.Areas.Count} areas");
    }

    /// <summary>
    /// Test that the Backlog view renders with data.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Backlog_ShouldRenderWithData()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference("Backlog");
        var projectAddress = new Address("ACME/ProductLaunch");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        // Backlog might have 2 areas if all tasks are assigned (header + empty message)
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is LayoutGridControl { Areas.Count: >= 2 })
            .Timeout(30.Seconds())
            .FirstAsync();

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;
        grid.Areas.Should().HaveCountGreaterThanOrEqualTo(2);
        Output.WriteLine($"Grid has {grid.Areas.Count} areas (may be empty backlog if all tasks assigned)");
    }

    /// <summary>
    /// Test that the TodaysFocus view renders with data.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TodaysFocus_ShouldRenderWithData()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference("TodaysFocus");
        var projectAddress = new Address("ACME/ProductLaunch");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is LayoutGridControl { Areas.Count: > 2 })
            .Timeout(30.Seconds())
            .FirstAsync();

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;
        grid.Areas.Should().HaveCountGreaterThan(2);
        Output.WriteLine($"Grid has {grid.Areas.Count} areas");
    }

    /// <summary>
    /// Test that exactly 2 items are overdue based on their DueDateOffsetDays.
    /// The test data has been set up with relative dates using DueDateOffsetDays:
    /// - PositioningDoc: -3 days (overdue, InProgress)
    /// - PricingStrategy: -5 days (overdue, Pending)
    /// All other non-completed items have positive offsets (future dates).
    /// Note: The IContentInitializable.Initialize() method calculates DueDate from DueDateOffsetDays.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TodaysFocus_ShouldHaveExactlyTwoOverdueItems()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        // Query for all Todo items
        var query = "path:ACME/ProductLaunch/Todo nodeType:ACME/Project/Todo scope:children";
        Output.WriteLine($"Querying: {query}");

        var results = await meshQuery.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery(query), ct: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {results.Count} Todo items");

        // Get content as dynamic objects to access properties
        var todosWithDates = new List<(string Id, int? OffsetDays, string Status)>();
        foreach (var node in results)
        {
            var contentJson = JsonSerializer.Serialize(node.Content);
            using var doc = JsonDocument.Parse(contentJson);
            var root = doc.RootElement;

            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : "unknown";
            var status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : "unknown";
            int? offsetDays = root.TryGetProperty("dueDateOffsetDays", out var offsetProp) && offsetProp.ValueKind == JsonValueKind.Number
                ? offsetProp.GetInt32()
                : null;

            todosWithDates.Add((id ?? "unknown", offsetDays, status ?? "unknown"));
            Output.WriteLine($"  - {id}: offset={offsetDays}, status={status}");
        }

        // Filter for overdue items: negative offset (past due date) and not completed
        var overdueItems = todosWithDates
            .Where(t => t.OffsetDays.HasValue && t.OffsetDays.Value < 0 && t.Status != "Completed")
            .ToList();

        Output.WriteLine($"\nOverdue items (offset < 0, not completed): {overdueItems.Count}");
        foreach (var item in overdueItems)
        {
            Output.WriteLine($"  - {item.Id}: offset={item.OffsetDays}, status={item.Status}");
        }

        // Verify exactly 2 items are overdue
        overdueItems.Should().HaveCount(2, "Should have exactly 2 overdue items");

        // Verify the specific items
        var overdueIds = overdueItems.Select(t => t.Id).ToList();
        overdueIds.Should().Contain("PositioningDoc", "PositioningDoc should be overdue (dueDateOffsetDays: -3)");
        overdueIds.Should().Contain("PricingStrategy", "PricingStrategy should be overdue (dueDateOffsetDays: -5)");

        Output.WriteLine("\nTest passed: Exactly 2 overdue items (PositioningDoc and PricingStrategy)");
    }

    #endregion
}
