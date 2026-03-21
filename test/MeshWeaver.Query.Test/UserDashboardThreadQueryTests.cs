using System;
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
/// Tests that the dashboard queries in UserActivityLayoutAreas.cs
/// return results correctly:
/// - Latest Threads: finds user's threads across all namespaces
/// - Activity Feed: finds nodes with activity satellites
/// - My Items: returns main content nodes only
/// </summary>
public class UserDashboardThreadQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The ObjectId from TestUsers.Admin (set by MonolithMeshTestBase.InitializeAsync).
    /// </summary>
    private const string AdminUserId = "Roland";

    private CancellationToken TestTimeout => new CancellationTokenSource(25.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    // ── Latest Threads ─────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task LatestThreads_FindsThreadsAcrossNamespaces_ByCreator()
    {
        // Arrange: create context nodes in different namespaces
        await NodeFactory.CreateNodeAsync(
            new MeshNode("PartnerRe") { Name = "Partner Re", NodeType = "Markdown" }, TestTimeout);
        await NodeFactory.CreateNodeAsync(
            new MeshNode("ACME") { Name = "ACME Corp", NodeType = "Markdown" }, TestTimeout);

        // Create threads in two different namespaces via CreateNodeRequest
        var client = GetClient();

        var resp1 = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode("PartnerRe", "Discussion about Partner Re portfolio")),
            o => o.WithTarget(new Address("PartnerRe")), TestTimeout);
        resp1.Message.Success.Should().BeTrue(resp1.Message.Error);
        Output.WriteLine($"Thread 1 at: {resp1.Message.Node?.Path}");

        var resp2 = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode("ACME", "ACME project review")),
            o => o.WithTarget(new Address("ACME")), TestTimeout);
        resp2.Message.Success.Should().BeTrue(resp2.Message.Error);
        Output.WriteLine($"Thread 2 at: {resp2.Message.Node?.Path}");

        // Act: query threads by creator across all partitions (the fixed dashboard query)
        var myThreads = await MeshQuery
            .QueryAsync<MeshNode>($"nodeType:Thread content.CreatedBy:{AdminUserId} scope:descendants sort:LastModified-desc")
            .ToListAsync();
        Output.WriteLine($"content.CreatedBy:{AdminUserId} scope:descendants => {myThreads.Count} threads");

        // Assert: both threads should be found across namespaces
        myThreads.Should().HaveCount(2, "both threads should be found regardless of namespace");
        myThreads.Should().AllSatisfy(n => n.NodeType.Should().Be("Thread"));

        // Verify CreatedBy is stored in content
        foreach (var thread in myThreads)
        {
            var content = thread.Content.Should().BeOfType<MeshThread>().Subject;
            content.CreatedBy.Should().Be(AdminUserId, "thread should store the creator's user ID");
        }
    }

    [Fact(Timeout = 30000)]
    public async Task LatestThreads_OldQuery_MissesThreadsInOtherNamespaces()
    {
        // This test documents the bug: the old namespace-scoped query
        // only finds threads under the user's own namespace.

        // Arrange: create context node in a different namespace
        await NodeFactory.CreateNodeAsync(
            new MeshNode("External") { Name = "External Org", NodeType = "Markdown" }, TestTimeout);

        var client = GetClient();
        var resp = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode("External", "Thread in external namespace")),
            o => o.WithTarget(new Address("External")), TestTimeout);
        resp.Message.Success.Should().BeTrue(resp.Message.Error);

        // Act: old query (namespace:User/userId scope:descendants) — misses external threads
        var userNs = $"User/{AdminUserId}";
        var oldQueryResults = await MeshQuery
            .QueryAsync<MeshNode>($"nodeType:Thread namespace:{userNs} scope:descendants sort:LastModified-desc")
            .ToListAsync();

        // Act: new query (content.CreatedBy filter, scope:descendants for global search)
        var newQueryResults = await MeshQuery
            .QueryAsync<MeshNode>($"nodeType:Thread content.CreatedBy:{AdminUserId} scope:descendants sort:LastModified-desc")
            .ToListAsync();

        Output.WriteLine($"Old query (namespace:{userNs}): {oldQueryResults.Count} threads");
        Output.WriteLine($"New query (content.CreatedBy): {newQueryResults.Count} threads");

        // Assert: old query misses the thread, new query finds it
        oldQueryResults.Should().BeEmpty("old namespace query can't find threads in External namespace");
        newQueryResults.Should().NotBeEmpty("new CreatedBy query finds threads across all namespaces");
    }

    [Fact(Timeout = 30000)]
    public async Task LatestThreads_DoesNotShowOtherUsersThreads()
    {
        // Arrange: create a thread with a different creator
        var otherUserId = "other-user";
        var threadPath = $"Shared/_Thread/other-thread-{Guid.NewGuid():N}";
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath(threadPath) with
        {
            Name = "Other user's thread",
            NodeType = "Thread",
            MainNode = "Shared/_Thread",
            Content = new MeshThread
            {
                ParentPath = "Shared",
                CreatedBy = otherUserId
            }
        }, TestTimeout);

        // Act: query for current user's threads
        var myThreads = await MeshQuery
            .QueryAsync<MeshNode>($"nodeType:Thread content.CreatedBy:{AdminUserId} scope:descendants sort:LastModified-desc")
            .ToListAsync();

        // Assert: other user's thread should NOT appear
        myThreads.Should().NotContain(n => n.Path == threadPath,
            "should not show threads created by other users");
    }

    // ── Activity Feed ──────────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task ActivityFeed_FindsNodesWithActivityAcrossNamespaces()
    {
        // Arrange: create nodes with activity in different namespaces
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("OrgA/doc1") with
        {
            Name = "Org A Document", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("OrgA/doc1/_activity/log1") with
        {
            Name = "Edit activity", NodeType = "Activity",
            MainNode = "OrgA/doc1",
            Content = new ActivityLog("DataUpdate") { HubPath = "OrgA/doc1" }
        }, TestTimeout);

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("OrgB/doc2") with
        {
            Name = "Org B Document", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("OrgB/doc2/_activity/log2") with
        {
            Name = "Edit activity", NodeType = "Activity",
            MainNode = "OrgB/doc2",
            Content = new ActivityLog("Approval") { HubPath = "OrgB/doc2" }
        }, TestTimeout);

        // Node without activity (should NOT appear)
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("OrgC/doc3") with
        {
            Name = "No Activity Doc", NodeType = "Markdown"
        }, TestTimeout);

        // Act: the dashboard activity feed query
        var results = await MeshQuery
            .QueryAsync<MeshNode>("source:activity scope:subtree is:main sort:LastModified-desc")
            .ToListAsync();

        // Assert: only nodes WITH activity should appear
        results.Should().HaveCountGreaterThanOrEqualTo(2,
            "at least the two nodes with activity satellites");
        results.Select(n => n.Name).Should().Contain("Org A Document");
        results.Select(n => n.Name).Should().Contain("Org B Document");
        results.Select(n => n.Name).Should().NotContain("No Activity Doc");
        results.Should().AllSatisfy(n =>
        {
            n.MainNode.Should().Be(n.Path, "only main nodes, not satellites");
            n.NodeType.Should().NotBe("Activity");
        });
    }

    [Fact(Timeout = 30000)]
    public async Task ActivityFeed_ScopedToNamespace_FindsOnlyThatNamespace()
    {
        // Arrange: activity in two namespaces
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("nsA/item1") with
        {
            Name = "Item A", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("nsA/item1/_activity/log1") with
        {
            Name = "Log", NodeType = "Activity",
            MainNode = "nsA/item1",
            Content = new ActivityLog("DataUpdate") { HubPath = "nsA/item1" }
        }, TestTimeout);

        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("nsB/item2") with
        {
            Name = "Item B", NodeType = "Markdown"
        }, TestTimeout);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("nsB/item2/_activity/log2") with
        {
            Name = "Log", NodeType = "Activity",
            MainNode = "nsB/item2",
            Content = new ActivityLog("Approval") { HubPath = "nsB/item2" }
        }, TestTimeout);

        // Act: scoped to nsA only
        var results = await MeshQuery
            .QueryAsync<MeshNode>("source:activity namespace:nsA scope:descendants sort:LastModified-desc")
            .ToListAsync();

        // Assert: only nsA items
        results.Should().ContainSingle();
        results[0].Name.Should().Be("Item A");
    }

    // ── Thread CreatedBy storage ────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task CreateNodeRequest_Thread_StoresCreatedByInContent()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(
            new MeshNode("TestCtx") { Name = "Test Context", NodeType = "Markdown" }, TestTimeout);

        // Act: create thread via the production CreateNodeRequest path
        var client = GetClient();
        var response = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode("TestCtx", "Verify created-by storage")),
            o => o.WithTarget(new Address("TestCtx")), TestTimeout);

        response.Message.Success.Should().BeTrue(response.Message.Error);
        var threadPath = response.Message.Node!.Path!;

        // Assert: retrieve and verify CreatedBy is set
        var node = await MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}").FirstOrDefaultAsync(TestTimeout);
        node.Should().NotBeNull();
        var content = node!.Content.Should().BeOfType<MeshThread>().Subject;
        content.CreatedBy.Should().NotBeNullOrEmpty("CreatedBy should be set by HandleCreateThread");
        content.CreatedBy.Should().Be(AdminUserId, "CreatedBy should match the logged-in user's ObjectId");
    }
}
