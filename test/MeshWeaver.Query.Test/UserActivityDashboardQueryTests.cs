using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Tests the exact MeshSearch queries used by UserActivityLayoutAreas.cs
/// to verify they return results when appropriate data exists.
/// These tests use InMemory persistence (MonolithMeshTestBase).
/// </summary>
public class UserActivityDashboardQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    // ── Query 1: Activity Feed ──────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task ActivityFeed_ReturnsMainNodesWithActivitySatellites()
    {
        // Arrange: 3 main nodes; only 2 have _activity satellite children
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("af/project1") with
        {
            Name = "Project 1", NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("af/project1/_activity/log1") with
        {
            Name = "Log 1", NodeType = "Activity",
            MainNode = "af/project1",
            Content = new ActivityLog("DataUpdate") { HubPath = "af/project1" }
        });

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("af/project2") with
        {
            Name = "Project 2", NodeType = "Markdown"
        });
        // project2 has NO activity satellite

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("af/project3") with
        {
            Name = "Project 3", NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("af/project3/_activity/log3") with
        {
            Name = "Log 3", NodeType = "Activity",
            MainNode = "af/project3",
            Content = new ActivityLog("Approval") { HubPath = "af/project3" }
        });

        // Act: dashboard query scoped to namespace
        var results = await MeshQuery
            .QueryAsync<MeshNode>("source:activity namespace:af scope:descendants sort:LastModified-desc")
            .ToListAsync();

        // Assert: only the 2 nodes that have activity children
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain("Project 1").And.Contain("Project 3");
        results.Select(n => n.Name).Should().NotContain("Project 2");
        results.Should().AllSatisfy(n => n.NodeType.Should().NotBe("Activity",
            "only main content nodes, not Activity satellites"));
    }

    [Fact(Timeout = 30000)]
    public async Task ActivityFeed_NoActivitySatellites_ReturnsEmpty()
    {
        // Arrange: main nodes but no activity satellites
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("afEmpty/doc1") with
        {
            Name = "Doc 1", NodeType = "Markdown"
        });

        // Act: scoped to the test namespace
        var results = await MeshQuery
            .QueryAsync<MeshNode>("source:activity namespace:afEmpty scope:descendants sort:LastModified-desc")
            .ToListAsync();

        // Assert: no activity satellites => empty
        results.Should().BeEmpty();
    }

    // ── Query 2: Recently Viewed ────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task RecentlyViewed_InMemory_ReturnsMainNodes_ExcludesSatellites()
    {
        // Arrange: main nodes + satellite node
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("rv/doc1") with
        {
            Name = "Doc 1", NodeType = "Markdown"
        });
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("rv/doc2") with
        {
            Name = "Doc 2", NodeType = "Markdown"
        });
        // Activity satellite (should be excluded by is:main)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("rv/doc1/_activity/log1") with
        {
            Name = "Activity", NodeType = "Activity",
            MainNode = "rv/doc1",
            Content = new ActivityLog("DataUpdate") { HubPath = "rv/doc1" }
        });

        // Act: dashboard query scoped to namespace
        var results = await MeshQuery
            .QueryAsync<MeshNode>("source:accessed namespace:rv scope:descendants sort:LastModified-desc")
            .ToListAsync();

        // Assert: main nodes returned, satellites excluded
        results.Should().HaveCount(2, "both doc1 and doc2 are main Markdown nodes");
        results.Should().AllSatisfy(n =>
            n.MainNode.Should().Be(n.Path, "only main nodes (MainNode == Path)"));
        results.Select(n => n.Name).Should().Contain("Doc 1").And.Contain("Doc 2");

        // Note: In InMemory, source:accessed has no UserActivity JOIN,
        // so it returns ALL main nodes (equivalent to is:main).
        // The PostgreSQL provider correctly filters by UserActivity records.
    }

    // ── Query 3: Latest Threads ─────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task LatestThreads_ByNodeType_WithDescendantsScope()
    {
        var ct = new CancellationTokenSource(25.Seconds()).Token;

        // Arrange: create context node and thread (use non-User namespace to avoid ACL)
        var contextPath = "ThreadCtx";
        await NodeFactory.CreateNodeAsync(
            new MeshNode(contextPath) { Name = "Thread Context", NodeType = "Markdown" }, ct);

        var client = GetClient();
        var response = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(contextPath, "Help me with my project")),
            o => o.WithTarget(new Address(contextPath)), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        var threadPath = response.Message.Node!.Path!;
        Output.WriteLine($"Thread created at: {threadPath}");

        // Act 1: nodeType:Thread with scope:descendants — should find threads
        var byType = await MeshQuery
            .QueryAsync<MeshNode>("nodeType:Thread scope:descendants sort:LastModified-desc")
            .ToListAsync();
        Output.WriteLine($"nodeType:Thread scope:descendants => {byType.Count} results");

        // Act 2: namespace query — direct _Thread namespace query
        var byNamespace = await MeshQuery
            .QueryAsync<MeshNode>($"namespace:{contextPath}/_Thread nodeType:Thread")
            .ToListAsync();
        Output.WriteLine($"namespace:{contextPath}/_Thread nodeType:Thread => {byNamespace.Count} results");

        // Act 3: exact dashboard query (no path, no scope)
        var dashboardQuery = await MeshQuery
            .QueryAsync<MeshNode>("nodeType:Thread sort:LastModified-desc")
            .ToListAsync();
        Output.WriteLine($"nodeType:Thread sort:LastModified-desc (dashboard) => {dashboardQuery.Count} results");

        // Assert: at least scope:descendants must find it
        byType.Should().NotBeEmpty("nodeType:Thread scope:descendants should find threads");
        byType.Should().AllSatisfy(n => n.NodeType.Should().Be("Thread"));

        // namespace query should also find it
        byNamespace.Should().NotBeEmpty($"namespace:{contextPath}/_Thread should find threads");

        // Dashboard query (no path) — InMemory: 0 results (Children scope + empty basePath)
        // PostgreSQL via RoutingMeshQueryProvider: should work (adds scope:descendants per partition)
        Output.WriteLine($"Dashboard query found {dashboardQuery.Count} threads " +
            "(0 expected in InMemory without routing; >0 expected in PostgreSQL with routing)");
    }

    // ── Query 4: My Items ───────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task MyItems_ReturnsOnlyMainContentNodes()
    {
        var ct = new CancellationTokenSource(25.Seconds()).Token;
        var ns = "myItems";

        // Arrange: namespace node first (required for CreateNodeRequest target)
        await NodeFactory.CreateNodeAsync(
            new MeshNode(ns) { Name = "My Items NS", NodeType = "Markdown" }, ct);

        // Main content nodes under the namespace
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{ns}/doc1") with
        {
            Name = "Document 1", NodeType = "Markdown"
        }, ct);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{ns}/project1") with
        {
            Name = "Project 1", NodeType = "Markdown"
        }, ct);

        // Activity satellite (should be excluded by is:main)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath($"{ns}/doc1/_activity/log1") with
        {
            Name = "Activity", NodeType = "Activity",
            MainNode = $"{ns}/doc1",
            Content = new ActivityLog("DataUpdate") { HubPath = $"{ns}/doc1" }
        }, ct);

        // Thread satellite via CreateNodeRequest
        var client = GetClient();
        var threadResponse = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ns, "Thread in my items")),
            o => o.WithTarget(new Address(ns)), ct);
        threadResponse.Message.Success.Should().BeTrue(threadResponse.Message.Error);

        // Act: exact dashboard query
        var results = await MeshQuery
            .QueryAsync<MeshNode>($"namespace:{ns} is:main context:search scope:descendants sort:LastModified-desc")
            .ToListAsync();

        // Assert: only main content nodes, no satellites
        results.Should().HaveCountGreaterThanOrEqualTo(2,
            "at least doc1 and project1 should be returned");
        results.Should().AllSatisfy(n =>
        {
            n.MainNode.Should().Be(n.Path, "only main nodes (MainNode == Path)");
            n.NodeType.Should().NotBe("Activity");
            n.NodeType.Should().NotBe("Thread");
            n.NodeType.Should().NotBe("ThreadMessage");
        });
        results.Select(n => n.Name).Should().Contain("Document 1").And.Contain("Project 1");
    }
}
