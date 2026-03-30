using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests that ActiveToolCalls on Thread node are populated during execution
/// and cleared when execution ends. Verifies the ToolCallsView reads from
/// the thread's own workspace stream (no remote subscription = no deadlock).
/// </summary>
public class ActiveToolCallsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI().AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public async Task ActiveToolCalls_PushedDuringExecution_VisibleOnThreadNode()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/active-toolcalls-test";
        var subThreadPath = $"{threadPath}/resp1/sub-navigator";

        // Create thread with active tool calls (simulating mid-execution state)
        await NodeFactory.CreateNodeAsync(new MeshNode("active-toolcalls-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                IsExecuting = true,
                ActiveMessageId = "resp1",
                ExecutionStatus = "delegate_to_agent",
                ActiveToolCalls = ImmutableList.Create(
                    new ToolCallEntry
                    {
                        Name = "search_nodes",
                        DisplayName = "search_nodes(query: pricing)",
                        IsSuccess = true,
                        Timestamp = DateTime.UtcNow
                    },
                    new ToolCallEntry
                    {
                        Name = "delegate_to_agent",
                        DisplayName = "Navigator: Research pricing models",
                        DelegationPath = subThreadPath,
                        IsSuccess = true,
                        Timestamp = DateTime.UtcNow
                    })
            }
        }, ct);

        // Read via remote stream — tool calls visible
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var threadStream = workspace.GetRemoteStream<MeshNode>(
            new Address(threadPath), new MeshNodeReference());

        var threadNode = await threadStream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t?.ActiveToolCalls.Count > 0)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        threadNode.Should().NotBeNull();
        threadNode!.ActiveToolCalls.Should().HaveCount(2);
        threadNode.ActiveToolCalls[0].Name.Should().Be("search_nodes");
        threadNode.ActiveToolCalls[1].Name.Should().Be("delegate_to_agent");
        threadNode.ActiveToolCalls[1].DelegationPath.Should().Be(subThreadPath);
        Output.WriteLine($"ActiveToolCalls visible: {threadNode.ActiveToolCalls.Count} calls");
    }

    [Fact]
    public async Task ActiveToolCalls_ClearedWhenExecutionEnds()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // Create thread with active tool calls
        await NodeFactory.CreateNodeAsync(new MeshNode("toolcalls-clear-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                IsExecuting = true,
                ActiveMessageId = "resp2",
                ActiveToolCalls = ImmutableList.Create(new ToolCallEntry
                {
                    Name = "get_node",
                    DisplayName = "get_node(path: Doc/Architecture)",
                    IsSuccess = true,
                    Timestamp = DateTime.UtcNow
                })
            }
        }, ct);

        var threadPath = "User/Roland/_Thread/toolcalls-clear-test";
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var threadStream = workspace.GetRemoteStream<MeshNode>(
            new Address(threadPath), new MeshNodeReference());

        // Verify tool calls present
        var executing = await threadStream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t?.ActiveToolCalls.Count > 0)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);
        executing!.ActiveToolCalls.Should().HaveCount(1);
        Output.WriteLine("Tool calls present during execution");

        // Mark execution complete — tool calls should clear
        var cleared = threadStream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t is { IsExecuting: false } && t.ActiveToolCalls.Count == 0)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        workspace.UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with
            {
                Content = thread with
                {
                    IsExecuting = false,
                    ActiveMessageId = null,
                    ExecutionStatus = null,
                    ActiveToolCalls = []
                }
            };
        }, new Address(threadPath), threadPath);

        var idle = await cleared;
        idle!.IsExecuting.Should().BeFalse();
        idle.ActiveToolCalls.Should().BeEmpty();
        Output.WriteLine("Tool calls cleared after execution ended");
    }

    [Fact]
    public async Task ActiveToolCalls_DelegationPath_IdentifiesSubThread()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/delegation-path-test";
        var subThread1 = $"{threadPath}/resp3/sub-navigator";
        var subThread2 = $"{threadPath}/resp3/sub-researcher";

        // Thread with two parallel delegations
        await NodeFactory.CreateNodeAsync(new MeshNode("delegation-path-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                IsExecuting = true,
                ActiveMessageId = "resp3",
                ActiveToolCalls = ImmutableList.Create(
                    new ToolCallEntry
                    {
                        Name = "delegate_to_agent",
                        DisplayName = "Navigator: Find docs",
                        DelegationPath = subThread1,
                        IsSuccess = true,
                        Timestamp = DateTime.UtcNow
                    },
                    new ToolCallEntry
                    {
                        Name = "delegate_to_agent",
                        DisplayName = "Researcher: Analyze data",
                        DelegationPath = subThread2,
                        IsSuccess = true,
                        Timestamp = DateTime.UtcNow
                    })
            }
        }, ct);

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var threadStream = workspace.GetRemoteStream<MeshNode>(
            new Address(threadPath), new MeshNodeReference());

        var thread = await threadStream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t?.ActiveToolCalls.Count >= 2)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        var delegations = thread!.ActiveToolCalls
            .Where(c => !string.IsNullOrEmpty(c.DelegationPath))
            .ToList();

        delegations.Should().HaveCount(2, "two parallel delegations");
        delegations[0].DelegationPath.Should().Be(subThread1);
        delegations[1].DelegationPath.Should().Be(subThread2);
        Output.WriteLine($"Delegations: {delegations[0].DisplayName}, {delegations[1].DisplayName}");
    }
}
