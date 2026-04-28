#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Deadlock-coverage tests for <see cref="AgentChatClient.GetOrderedAgentsAsync"/>.
///
/// The bug being guarded: <c>LoadOrderedAgentsAsync</c> at AgentChatClient.cs:~1010
/// performs a single-node lookup for the context node's <c>NodeType</c>. The previous
/// implementation bridged that hub round-trip via <c>await hub.GetMeshNode(contextPath).ToTask()</c>,
/// which deadlocks the hub ActionBlock under any concurrent load. The fix uses a
/// <c>TaskCompletionSource</c> filled from <c>Subscribe</c> instead.
///
/// These tests fire several <see cref="IAgentChat.GetOrderedAgentsAsync"/> calls
/// concurrently against the same mesh; under deadlock they hang past the wall-clock
/// timeout, under the fix they all complete in &lt; 5 s.
/// </summary>
public class AgentChatClientDeadlockTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());

    /// <summary>
    /// SHOULD-FAIL-IF: <c>LoadOrderedAgentsAsync</c> bridges <c>hub.GetMeshNode(contextPath)</c>
    /// via <c>.ToTask()</c> + <c>await</c> — that pattern deadlocks the hub ActionBlock when
    /// multiple concurrent callers are in flight.
    /// </summary>
    [Fact]
    public async Task GetOrderedAgentsAsync_WithContextPath_ConcurrentCallers_DoNotDeadlock()
    {
        // ProductLaunch carries NodeType="ACME/Project", which exercises the
        // single-node-lookup branch (not "Agent" / not "Markdown") — the exact branch
        // that contained the await-on-hub.GetMeshNode bug.
        const string ContextPath = "ACME/ProductLaunch";

        // Pre-flight: confirm the test data is loaded — if this fails the test setup
        // is wrong, not the production code under test.
        var contextNode = await MeshQuery.QueryAsync<MeshNode>($"path:{ContextPath}",
            ct: TestContext.Current.CancellationToken)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        contextNode.Should().NotBeNull("ProductLaunch fixture node missing — test data setup broke");
        contextNode!.NodeType.Should().Be("ACME/Project");

        // Fire 8 concurrent GetOrderedAgentsAsync chains against fresh AgentChatClient
        // instances. Each chain re-runs the contextPath single-node lookup. A deadlock
        // in that lookup makes one of the hub pumps stall; under load several stall
        // in a row. WaitAsync(15s) bounds the wait — under deadlock it throws TimeoutException.
        async Task RunOne(int idx)
        {
            var client = new AgentChatClient(Mesh.ServiceProvider);
            await client.InitializeAsync(ContextPath);
            client.SetContext(new AgentContext
            {
                Address = new Address("ACME", "ProductLaunch"),
                Node = contextNode,
            });
            var agents = await client.GetOrderedAgentsAsync();
            agents.Should().NotBeEmpty($"call {idx} should have found agents from ACME/Project hierarchy");
        }

        var all = Task.WhenAll(Enumerable.Range(0, 8).Select(RunOne));
        await all.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the contextPath single-node lookup blocks the hub even for a
    /// single sequential caller (the trivial repro of the deadlock — every fresh
    /// <c>AgentChatClient</c> kicks off the same hub round-trip on activation).
    /// </summary>
    [Fact]
    public async Task GetOrderedAgentsAsync_WithContextPath_SingleCaller_ResolvesQuickly()
    {
        const string ContextPath = "ACME/ProductLaunch";
        var contextNode = await MeshQuery.QueryAsync<MeshNode>($"path:{ContextPath}",
            ct: TestContext.Current.CancellationToken)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        contextNode.Should().NotBeNull();

        var client = new AgentChatClient(Mesh.ServiceProvider);
        await client.InitializeAsync(ContextPath).WaitAsync(TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);
        client.SetContext(new AgentContext
        {
            Address = new Address("ACME", "ProductLaunch"),
            Node = contextNode,
        });

        var agents = await client.GetOrderedAgentsAsync()
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        agents.Should().NotBeEmpty();
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the single-node lookup path is invoked and hangs even when the
    /// context node has a NodeType that is filtered out (Markdown / Agent). The fix
    /// is in the lookup itself, not the post-lookup filter — the code still runs the
    /// hub round-trip before deciding to discard the result.
    /// </summary>
    [Fact]
    public async Task GetOrderedAgentsAsync_WithMarkdownContext_DoesNotDeadlock()
    {
        // Use a known Markdown node from the test data — TestDoc/ParentDoc.md.
        const string ContextPath = "TestDoc/ParentDoc";
        var contextNode = await MeshQuery.QueryAsync<MeshNode>($"path:{ContextPath}",
            ct: TestContext.Current.CancellationToken)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        // Even if the fixture file isn't there, the production code still runs the lookup
        // and applies the filter; that's the path we want to guard. So we skip the
        // assertion on contextNode existing and focus on the deadlock guard.
        var client = new AgentChatClient(Mesh.ServiceProvider);
        await client.InitializeAsync(ContextPath).WaitAsync(TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);
        client.SetContext(new AgentContext
        {
            Address = new Address("TestDoc", "ParentDoc"),
            Node = contextNode,
        });

        // No assertion on agents content — the existence check is "did the call return".
        await client.GetOrderedAgentsAsync()
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }
}
