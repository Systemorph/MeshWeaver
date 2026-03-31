using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
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
/// Tests that tool calls on response messages are visible via remote streams.
/// </summary>
public class ToolCallsVisibilityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI().AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public async Task ResponseMessage_ToolCalls_VisibleViaRemoteStream()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/toolcalls-visible-test";
        var responseMsgId = "resp-vis";
        var responsePath = $"{threadPath}/{responseMsgId}";

        // Create response message with tool calls including a delegation
        var toolCalls = ImmutableList.Create(
            new ToolCallEntry
            {
                Name = "search_nodes",
                DisplayName = "search_nodes",
                Arguments = "query: reinsurance",
                Result = "Found 3 nodes",
                IsSuccess = true,
                Timestamp = DateTime.UtcNow
            },
            new ToolCallEntry
            {
                Name = "delegate_to_agent",
                DisplayName = "Navigator: Research pricing models",
                DelegationPath = $"{responsePath}/sub-navigator",
                IsSuccess = true,
                Timestamp = DateTime.UtcNow
            });

        await NodeFactory.CreateNodeAsync(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Id = responseMsgId,
                Role = "assistant",
                Text = "Working on it...",
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Orchestrator",
                ToolCalls = toolCalls
            }
        }, ct);

        // Create thread
        await NodeFactory.CreateNodeAsync(new MeshNode("toolcalls-visible-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                IsExecuting = true,
                ActiveMessageId = responseMsgId,
                Messages = [responseMsgId]
            }
        }, ct);

        Output.WriteLine("Created thread with response message containing tool calls");

        // 1. Read tool calls via remote stream on the response message
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var responseStream = workspace.GetRemoteStream<MeshNode>(
            new Address(responsePath), new MeshNodeReference());

        var responseNode = await responseStream
            .Select(ci => ci.Value)
            .Where(n => n?.Content is ThreadMessage)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        var msg = responseNode!.Content as ThreadMessage;
        msg.Should().NotBeNull();
        msg!.ToolCalls.Should().HaveCount(2, "response message should have 2 tool calls");
        msg.ToolCalls[0].Name.Should().Be("search_nodes");
        msg.ToolCalls[1].Name.Should().Be("delegate_to_agent");
        msg.ToolCalls[1].DelegationPath.Should().NotBeNullOrEmpty("delegation should have a path");
        Output.WriteLine($"Tool calls visible: {msg.ToolCalls.Count} calls, delegation={msg.ToolCalls[1].DelegationPath}");
    }

    [Fact]
    public async Task ResponseMessage_ToolCallsUpdate_PropagatesViaStream()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/toolcalls-update-test";
        var responseMsgId = "resp-upd";
        var responsePath = $"{threadPath}/{responseMsgId}";

        // Create response message with NO tool calls initially
        await NodeFactory.CreateNodeAsync(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Id = responseMsgId,
                Role = "assistant",
                Text = "",
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Orchestrator"
            }
        }, ct);

        // Create thread
        await NodeFactory.CreateNodeAsync(new MeshNode("toolcalls-update-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                IsExecuting = true,
                ActiveMessageId = responseMsgId,
                Messages = [responseMsgId]
            }
        }, ct);

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var responseStream = workspace.GetRemoteStream<MeshNode>(
            new Address(responsePath), new MeshNodeReference());

        // Wait for initial (empty tool calls)
        var initial = await responseStream
            .Select(ci => ci.Value?.Content as ThreadMessage)
            .Where(m => m != null)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);
        initial!.ToolCalls.Should().BeEmpty("initially no tool calls");
        Output.WriteLine("Initial: 0 tool calls");

        // Now update the message with tool calls (simulating what responseStream.Update does)
        var waitForToolCalls = responseStream
            .Select(ci => ci.Value?.Content as ThreadMessage)
            .Where(m => m?.ToolCalls.Count > 0)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        // Update via remote stream (same pattern as ThreadExecution)
        responseStream.Update(current =>
        {
            if (current == null) return null;
            var updated = current with
            {
                Content = new ThreadMessage
                {
                    Id = responseMsgId,
                    Role = "assistant",
                    Text = "",
                    Type = ThreadMessageType.AgentResponse,
                    AgentName = "Orchestrator",
                    ToolCalls = ImmutableList.Create(new ToolCallEntry
                    {
                        Name = "get_node",
                        DisplayName = "get_node(path: Doc/Architecture)",
                        IsSuccess = true,
                        Timestamp = DateTime.UtcNow
                    })
                }
            };
            return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
        });

        var afterUpdate = await waitForToolCalls;
        afterUpdate.Should().NotBeNull();
        afterUpdate!.ToolCalls.Should().HaveCount(1);
        afterUpdate.ToolCalls[0].Name.Should().Be("get_node");
        Output.WriteLine($"After update: {afterUpdate.ToolCalls.Count} tool calls - {afterUpdate.ToolCalls[0].DisplayName}");
    }

    [Fact]
    public async Task Delegation_AppearsOnResponseMessage_ThenThreadGoesIdle()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/toolcalls-lifecycle-test";
        var responseMsgId = "resp-life";
        var responsePath = $"{threadPath}/{responseMsgId}";
        var subThreadPath = $"{responsePath}/sub-worker";

        // Create sub-thread (executing)
        await NodeFactory.CreateNodeAsync(new MeshNode("sub-worker", responsePath)
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread { IsExecuting = true, ActiveMessageId = "sub-resp" }
        }, ct);

        // Create response message — initially NO tool calls
        await NodeFactory.CreateNodeAsync(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Id = responseMsgId, Role = "assistant", Text = "",
                Type = ThreadMessageType.AgentResponse, AgentName = "Orchestrator"
            }
        }, ct);

        // Create thread in executing state
        await NodeFactory.CreateNodeAsync(new MeshNode("toolcalls-lifecycle-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                IsExecuting = true, ActiveMessageId = responseMsgId, Messages = [responseMsgId]
            }
        }, ct);

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var responseStream = workspace.GetRemoteStream<MeshNode>(
            new Address(responsePath), new MeshNodeReference());

        // 1. Initially no delegations
        var initial = await responseStream
            .Select(ci => ci.Value?.Content as ThreadMessage)
            .Where(m => m != null).Timeout(10.Seconds()).FirstAsync().ToTask(ct);
        initial!.ToolCalls.Where(c => !string.IsNullOrEmpty(c.DelegationPath)).Should().BeEmpty();
        Output.WriteLine("Phase 1: 0 delegations");

        // 2. Add delegation tool call
        var delegationAppeared = responseStream
            .Select(ci => (ci.Value?.Content as ThreadMessage)?.ToolCalls
                .Where(c => !string.IsNullOrEmpty(c.DelegationPath)).ToList())
            .Where(d => d?.Count > 0).Timeout(10.Seconds()).FirstAsync().ToTask(ct);

        responseStream.Update(current =>
        {
            if (current == null) return null;
            var updated = current with
            {
                Content = new ThreadMessage
                {
                    Id = responseMsgId, Role = "assistant", Text = "",
                    Type = ThreadMessageType.AgentResponse, AgentName = "Orchestrator",
                    ToolCalls = ImmutableList.Create(new ToolCallEntry
                    {
                        Name = "delegate_to_agent", DisplayName = "Worker: Analyze data",
                        DelegationPath = subThreadPath, IsSuccess = true, Timestamp = DateTime.UtcNow
                    })
                }
            };
            return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
        });

        var delegations = await delegationAppeared;
        delegations.Should().HaveCount(1);
        delegations![0].DelegationPath.Should().Be(subThreadPath);
        Output.WriteLine($"Phase 2: delegation appeared — {delegations[0].DisplayName}");

        // 3. Mark thread as not executing
        workspace.UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with
            {
                Content = thread with { IsExecuting = false, ActiveMessageId = null }
            };
        }, new Address(threadPath), threadPath);

        var threadStream = workspace.GetRemoteStream<MeshNode>(
            new Address(threadPath), new MeshNodeReference());
        var idle = await threadStream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Timeout(10.Seconds()).FirstAsync().ToTask(ct);

        idle!.IsExecuting.Should().BeFalse();
        idle.ActiveMessageId.Should().BeNull("ActiveMessageId should be cleared when execution ends");
        Output.WriteLine("Phase 3: thread idle");
    }

    /// <summary>
    /// Reproduces feedback loop: opening the layout area for a response message,
    /// then updating tool calls via stream. The layout area subscriptions must NOT
    /// cause runaway version increments (host.UpdateData re-triggering the stream).
    /// </summary>
    [Fact]
    public async Task ToolCallsUpdate_WithLayoutArea_NoFeedbackLoop()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/feedback-loop-test";
        var responseMsgId = "resp-loop";
        var responsePath = $"{threadPath}/{responseMsgId}";

        // Create response message — empty, simulating start of execution
        await NodeFactory.CreateNodeAsync(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Id = responseMsgId,
                Role = "assistant",
                Text = "",
                Type = ThreadMessageType.AgentResponse,
                AgentName = "TestAgent"
            }
        }, ct);

        // Create thread in executing state
        await NodeFactory.CreateNodeAsync(new MeshNode("feedback-loop-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                IsExecuting = true,
                ActiveMessageId = responseMsgId,
                Messages = [responseMsgId]
            }
        }, ct);

        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Open the layout area for the response message — this activates
        // ThreadMessageLayoutAreas.Overview subscriptions (text + toolCalls + isExecuting)
        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(responsePath),
            new LayoutAreaReference(ThreadMessageNodeType.OverviewArea));

        // Wait for layout to render
        var layoutRendered = await layoutStream!
            .Where(ci => ci.Value.ValueKind != JsonValueKind.Null)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);
        Output.WriteLine("Layout area rendered");

        // Get the response node stream (same as ThreadExecution uses)
        var responseStream = workspace.GetRemoteStream<MeshNode>(
            new Address(responsePath), new MeshNodeReference());

        // Record version BEFORE the update
        var versionBefore = responseStream.Hub.Version;
        Output.WriteLine($"Version before update: {versionBefore}");

        // Push tool calls — simulating what ThreadExecution does during streaming
        responseStream.Update(current =>
        {
            if (current == null) return null;
            var updated = current with
            {
                Content = new ThreadMessage
                {
                    Id = responseMsgId,
                    Role = "assistant",
                    Text = "",
                    Type = ThreadMessageType.AgentResponse,
                    AgentName = "TestAgent",
                    ToolCalls = ImmutableList.Create(new ToolCallEntry
                    {
                        Name = "Search",
                        DisplayName = "Searching nodes...",
                        Arguments = "query: test",
                        Timestamp = DateTime.UtcNow
                    })
                }
            };
            return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
        });

        // Wait a bit for any feedback loop to manifest
        await Task.Delay(500, ct);

        var versionAfter = responseStream.Hub.Version;
        Output.WriteLine($"Version after update + 500ms: {versionAfter}");

        // The version should NOT have exploded. A single update should cause
        // at most a handful of version increments (update + layout reaction).
        // If feedback loop exists, version jumps by hundreds or thousands.
        var versionDelta = versionAfter - versionBefore;
        versionDelta.Should().BeLessThan(20,
            "a single tool call update should not cause runaway version increments. " +
            $"Delta was {versionDelta} — indicates a feedback loop in layout area subscriptions");
        Output.WriteLine($"Version delta: {versionDelta} (no feedback loop)");

        // Also push a second update with completed tool call
        responseStream.Update(current =>
        {
            if (current == null) return null;
            var updated = current with
            {
                Content = new ThreadMessage
                {
                    Id = responseMsgId,
                    Role = "assistant",
                    Text = "Done.",
                    Type = ThreadMessageType.AgentResponse,
                    AgentName = "TestAgent",
                    ToolCalls = ImmutableList.Create(new ToolCallEntry
                    {
                        Name = "Search",
                        DisplayName = "Searching nodes...",
                        Arguments = "query: test",
                        Result = "Found 5 nodes",
                        IsSuccess = true,
                        Timestamp = DateTime.UtcNow
                    })
                }
            };
            return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
        });

        await Task.Delay(500, ct);

        var versionFinal = responseStream.Hub.Version;
        var totalDelta = versionFinal - versionBefore;
        totalDelta.Should().BeLessThan(40,
            "two updates should not cause runaway versions. " +
            $"Total delta was {totalDelta}");
        Output.WriteLine($"Total version delta after 2 updates: {totalDelta}");
    }
}
