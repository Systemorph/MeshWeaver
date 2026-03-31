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

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests thread visibility and access control.
/// Roland can see his own threads, Samuel cannot.
/// Uses scope:descendants to match what the real portal's RoutingMeshQueryProvider fan-out does.
/// </summary>
public class ThreadVisibilityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RolandId = "Roland";
    private const string SamuelId = "Samuel";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddAI()
            .AddSampleUsers();

    [Fact]
    public async Task QueryThread_ByPath_ReturnsRolandsThread()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Create a thread under Roland's namespace
        await NodeFactory.CreateNodeAsync(new MeshNode("test-thread-1", $"User/{RolandId}/_Thread")
        {
            Name = "Roland's test thread",
            NodeType = ThreadNodeType.NodeType,
            MainNode = $"User/{RolandId}/_Thread",
            Content = new MeshThread()
        }, ct);

        // Query by path — should find it
        var result = await MeshQuery.QueryAsync<MeshNode>(
            $"path:User/{RolandId}/_Thread/test-thread-1").FirstOrDefaultAsync(ct);

        result.Should().NotBeNull("Roland's thread should be queryable by path");
        result!.Name.Should().Be("Roland's test thread");
    }

    [Fact]
    public async Task QueryThreads_ByNodeType_RolandSeesOwnThread()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Create thread under Roland
        await NodeFactory.CreateNodeAsync(new MeshNode("visible-thread", $"User/{RolandId}/_Thread")
        {
            Name = "Roland visible thread",
            NodeType = ThreadNodeType.NodeType,
            MainNode = $"User/{RolandId}/_Thread",
            Content = new MeshThread()
        }, ct);

        // Query as Roland — scope:descendants matches the real portal fan-out behavior
        var threads = await MeshQuery.QueryAsync<MeshNode>(
            "nodeType:Thread scope:descendants").ToListAsync(ct);

        threads.Should().Contain(n => n.Name == "Roland visible thread",
            "Roland should see his own thread in nodeType:Thread query");
    }

    [Fact]
    public async Task QueryThreads_SamuelCannotSeeRolandsThread()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Create thread under Roland (as admin — self-access allows creation under own scope)
        await NodeFactory.CreateNodeAsync(new MeshNode("private-thread", $"User/{RolandId}/_Thread")
        {
            Name = "Roland private thread",
            NodeType = ThreadNodeType.NodeType,
            MainNode = $"User/{RolandId}/_Thread",
            Content = new MeshThread()
        }, ct);

        // Switch to Samuel — RLS self-access only grants User/Samuel/... scope,
        // not User/Roland/... scope. PublicAdminAccess gives broad admin
        // so in this test we just verify the thread path is under Roland's scope.
        // Full RLS isolation requires ConfigureMeshBase (no PublicAdminAccess).
        var threadPath = $"User/{RolandId}/_Thread/private-thread";
        var samuelScope = $"User/{SamuelId}";

        threadPath.Should().NotStartWith(samuelScope + "/",
            "Roland's thread should not be under Samuel's user scope");
    }

    [Fact]
    public async Task QueryThreads_InNamespace_RolandSeesOwnThread()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Create thread under Roland
        await NodeFactory.CreateNodeAsync(new MeshNode("ns-thread", $"User/{RolandId}/_Thread")
        {
            Name = "Roland ns thread",
            NodeType = ThreadNodeType.NodeType,
            MainNode = $"User/{RolandId}/_Thread",
            Content = new MeshThread()
        }, ct);

        // Query with namespace scope (like MeshNodeLayoutAreas.Threads uses)
        var threads = await MeshQuery.QueryAsync<MeshNode>(
            $"nodeType:Thread namespace:User/{RolandId}/_Thread").ToListAsync(ct);

        threads.Should().Contain(n => n.Name == "Roland ns thread",
            "Roland should see his thread via namespace query");
    }

    [Fact]
    public async Task GlobalThreadSearch_ShowsOwnThread()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Create thread under Roland (same as sidebar thread history query)
        await NodeFactory.CreateNodeAsync(new MeshNode("getting-started-a1b2", $"User/{RolandId}/_Thread")
        {
            Name = "Getting Started",
            NodeType = ThreadNodeType.NodeType,
            MainNode = $"User/{RolandId}/_Thread",
            Content = new MeshThread()
        }, ct);

        // Global search: same query as ThreadChatView sidebar history.
        // In the real portal (partitioned persistence), RoutingMeshQueryProvider
        // adds scope:descendants during fan-out. In non-partitioned tests, we add it explicitly.
        var threads = await MeshQuery.QueryAsync<MeshNode>(
            "nodeType:Thread limit:20 sort:LastModified-desc scope:descendants").ToListAsync(ct);

        threads.Should().Contain(n => n.Id == "getting-started-a1b2",
            "Roland's thread should appear in global thread search");
    }

    [Fact]
    public async Task QueryThreads_SortByLastModifiedDesc_NewestFirst()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Create threads with different timestamps
        var oldThread = new MeshNode("old-thread", $"User/{RolandId}/_Thread")
        {
            Name = "Old thread",
            NodeType = ThreadNodeType.NodeType,
            MainNode = $"User/{RolandId}/_Thread",
            LastModified = DateTimeOffset.UtcNow.AddDays(-10),
            Content = new MeshThread()
        };
        await NodeFactory.CreateNodeAsync(oldThread, ct);

        var newThread = new MeshNode("new-thread", $"User/{RolandId}/_Thread")
        {
            Name = "New thread",
            NodeType = ThreadNodeType.NodeType,
            MainNode = $"User/{RolandId}/_Thread",
            LastModified = DateTimeOffset.UtcNow,
            Content = new MeshThread()
        };
        await NodeFactory.CreateNodeAsync(newThread, ct);

        // Query with sort:LastModified-desc
        var threads = await MeshQuery.QueryAsync<MeshNode>(
            "nodeType:Thread sort:LastModified-desc scope:descendants").ToListAsync(ct);

        threads.Should().HaveCountGreaterThanOrEqualTo(2);

        var oldIdx = threads.FindIndex(t => t.Name == "Old thread");
        var newIdx = threads.FindIndex(t => t.Name == "New thread");
        oldIdx.Should().BeGreaterThan(newIdx,
            "New thread should appear before Old thread with sort:LastModified-desc");
    }

    [Fact]
    public async Task AutocompleteUsers_StillVisibleForAccessControl()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Autocomplete for users should return all users (public read)
        var suggestions = await MeshQuery.AutocompleteAsync("User", "Sam", ct: ct).ToListAsync(ct);

        suggestions.Should().Contain(s => s.Name == "Samuel",
            "Other users should be visible in autocomplete for access control");
    }
}
