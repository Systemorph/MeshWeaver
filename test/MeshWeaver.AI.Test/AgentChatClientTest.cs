#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for AgentChatClient using real mesh infrastructure.
/// Uses the samples/Graph/Data directory deployed to TestData.
/// </summary>
public class AgentChatClientTest : MonolithMeshTestBase
{
    // Path to the deployed test data (samples/Graph/Data copied to bin/TestData)
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public AgentChatClientTest(ITestOutputHelper output) : base(output)
    {
    }

    // Share Mesh/SP across [Fact]s.
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            // AddAI registers Agent NodeType + AgentConfiguration content type so
            // .md files with `nodeType: Agent` deserialise into AgentConfiguration â€”
            // without this AgentPickerProjection.ProjectAgents filters them all out.
            .AddAI()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Per-partition agent registry (refactor 4f44ec3c6): agents are surfaced by EXACT
    /// membership of the well-known /Agent sub-namespaces (platform "Agent", the space's
    /// "{space}/Agent", and the user's "{user}/Agent") - NOT by a NodeType/ancestor graph
    /// walk. So an agent OUTSIDE those namespaces (TodoAgent at ACME/Project/TodoAgent) is
    /// no longer surfaced for an ACME context, while the platform registry agents are. The
    /// query shape itself is unit-tested in AgentPickerQueriesTest; this pins the
    /// AgentChatClient wiring to the new model.
    /// </summary>
    [Fact]
    public async Task AgentChatClient_Initialize_SurfacesPartitionAgents_NotNodeTypeNamespaceAgents()
    {
        // Arrange - ACME/ProductLaunch has NodeType="ACME/Project", TodoAgent is at ACME/Project/TodoAgent
        var contextPath = "ACME/ProductLaunch";
        var expectedNodeType = "ACME/Project";
        var expectedTodoAgentPath = "ACME/Project/TodoAgent";

        // Static node read â€” no write before, catalog read is correct (no CQRS lag).
        var productLaunchNode = await MeshQuery.QueryAsync<MeshNode>($"path:{contextPath}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        productLaunchNode.Should().NotBeNull("ProductLaunch node should exist in test data");

        // ProductLaunch is an ACME/Project instance — test-data sanity check. Under the new
        // registry its NodeType no longer drives agent discovery (that was the removed model).
        productLaunchNode!.NodeType.Should().Be(expectedNodeType,
            "ProductLaunch should be an ACME/Project instance in the test data");

        // Act - Create AgentChatClient using the mesh's service provider
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);

        // 1. Initialize then SetContext â€” SetContext re-inits the subscription with
        //    the context node's space partition, which is what brings in that space's
        //    own agents at namespace:{space}/Agent (exact membership, no scope walk).
        chatClient.Initialize(contextPath);
        chatClient.SetContext(new AgentContext
        {
            Address = new Address("ACME", "ProductLaunch"),
            Node = productLaunchNode
        });

        // Wait until the registry surfaces agents (the platform /Agent defaults). FromAsync keeps
        // the read non-blocking and yields the loaded set straight off the pipeline (the cold
        // synced query can take a few seconds to populate).
        var orderedAgents = await chatClient.WhenInitialized
            .SelectMany(_ => Observable.FromAsync(chatClient.GetOrderedAgentsAsync))
            .Where(a => a.Count > 0)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Assert: the platform /Agent registry agents are surfaced.
        orderedAgents.Should().NotBeEmpty("platform /Agent registry agents are always surfaced");

        // TodoAgent lives at ACME/Project/TodoAgent, OUTSIDE the /Agent registry namespaces,
        // so exact-membership discovery (namespace:ACME/Agent|Agent) does NOT surface it. The
        // pre-refactor NodeType-scoped scope:selfAndAncestors lookup that found it was removed
        // in 4f44ec3c6.
        orderedAgents.Any(a => a.Path == expectedTodoAgentPath).Should().BeFalse(
            "agents are surfaced by exact /Agent-namespace membership, not NodeType-ancestor discovery");
    }

    /// <summary>
    /// Per-partition registry: for ANY context the platform "Agent" defaults are surfaced
    /// (plus the context's space/user /Agent agents when present). There is no path-hierarchy
    /// / ancestor walk - discovery is exact /Agent-namespace membership.
    /// </summary>
    [Fact]
    public async Task AgentChatClient_Initialize_SurfacesPlatformAgentsForAnyContext()
    {
        // Arrange - Use a path that should have agents in hierarchy
        var contextPath = "ACME";

        // Static node read â€” no write before, catalog read is correct (no CQRS lag).
        var acmeNode = await MeshQuery.QueryAsync<MeshNode>($"path:{contextPath}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        acmeNode.Should().NotBeNull("ACME node should exist in test data");

        // Act
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        chatClient.Initialize(contextPath);
        chatClient.SetContext(new AgentContext
        {
            Address = new Address("ACME"),
            Node = acmeNode
        });

        // Wait for the synced query to emit a non-empty agent set (non-blocking read).
        var orderedAgents = await chatClient.WhenInitialized
            .SelectMany(_ => Observable.FromAsync(chatClient.GetOrderedAgentsAsync))
            .Where(a => a.Count > 0)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Assert: the platform /Agent registry agents are surfaced for any context.
        orderedAgents.Should().NotBeEmpty("platform /Agent registry agents should be surfaced for any context");
    }
}
