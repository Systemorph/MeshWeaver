using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Verifies tool calls on response messages are visible via the canonical
/// <c>client.GetWorkspace().GetMeshNodeStream(path)</c> reactive handle â€”
/// the same primitive the Blazor view consumes. Updates also go through
/// the same handle (no ad-hoc <c>GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>).
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
    public void ResponseMessage_ToolCalls_VisibleViaRemoteStream()
    {
        var threadPath = "User/Roland/_Thread/toolcalls-visible-test";
        var responseMsgId = "resp-vis";
        var responsePath = $"{threadPath}/{responseMsgId}";

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

        NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "Working on it...",
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Orchestrator",
                ToolCalls = toolCalls
            }
        }).Should().Emit();

        NodeFactory.CreateNode(new MeshNode("toolcalls-visible-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = responseMsgId,
                Messages = [responseMsgId]
            }
        }).Should().Emit();

        Output.WriteLine("Created thread with response message containing tool calls");

        var client = GetClient();
        var msg = ThreadFlow.ReadMessage(client, threadPath, responseMsgId,
            m => m.ToolCalls.Count >= 2, timeout: 10.Seconds()).Should().Within(10.Seconds()).Emit();

        msg.ToolCalls.Should().HaveCount(2);
        msg.ToolCalls[0].Name.Should().Be("search_nodes");
        msg.ToolCalls[1].Name.Should().Be("delegate_to_agent");
        msg.ToolCalls[1].DelegationPath.Should().NotBeNullOrEmpty();
        Output.WriteLine($"Tool calls visible: {msg.ToolCalls.Count} calls, delegation={msg.ToolCalls[1].DelegationPath}");
    }

    [Fact]
    public void ResponseMessage_ToolCallsUpdate_PropagatesViaStream()
    {
        var threadPath = "User/Roland/_Thread/toolcalls-update-test";
        var responseMsgId = "resp-upd";
        var responsePath = $"{threadPath}/{responseMsgId}";

        NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "",
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Orchestrator"
            }
        }).Should().Emit();

        NodeFactory.CreateNode(new MeshNode("toolcalls-update-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = responseMsgId,
                Messages = [responseMsgId]
            }
        }).Should().Emit();

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var responseStream = workspace.GetMeshNodeStream(responsePath);

        var initial = responseStream
            .Select(n => n.Content as ThreadMessage)
            .Should().Within(10.Seconds()).Match(m => m != null);
        initial!.ToolCalls.Should().BeEmpty("initially no tool calls");
        Output.WriteLine("Initial: 0 tool calls");

        responseStream.Update(current =>
        {
            var msg = current.Content as ThreadMessage ?? new ThreadMessage { Role = "assistant", Text = "" };
            return current with
            {
                Content = msg with
                {
                    ToolCalls = ImmutableList.Create(new ToolCallEntry
                    {
                        Name = "get_node",
                        DisplayName = "get_node(path: Doc/Architecture)",
                        IsSuccess = true,
                        Timestamp = DateTime.UtcNow
                    })
                }
            };
        }).Should().Emit();

        var afterUpdate = responseStream
            .Select(n => n.Content as ThreadMessage)
            .Should().Within(10.Seconds()).Match(m => m?.ToolCalls.Count > 0);
        afterUpdate!.ToolCalls.Should().HaveCount(1);
        afterUpdate.ToolCalls[0].Name.Should().Be("get_node");
        Output.WriteLine($"After update: {afterUpdate.ToolCalls.Count} tool calls - {afterUpdate.ToolCalls[0].DisplayName}");
    }

    [Fact]
    public void Delegation_AppearsOnResponseMessage_ThenThreadGoesIdle()
    {
        var threadPath = "User/Roland/_Thread/toolcalls-lifecycle-test";
        var responseMsgId = "resp-life";
        var responsePath = $"{threadPath}/{responseMsgId}";
        var subThreadPath = $"{responsePath}/sub-worker";

        NodeFactory.CreateNode(new MeshNode("sub-worker", responsePath)
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread { Status = ThreadExecutionStatus.Executing, ActiveMessageId = "sub-resp" }
        }).Should().Emit();

        NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "",
                Type = ThreadMessageType.AgentResponse, AgentName = "Orchestrator"
            }
        }).Should().Emit();

        NodeFactory.CreateNode(new MeshNode("toolcalls-lifecycle-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                Status = ThreadExecutionStatus.Executing, ActiveMessageId = responseMsgId, Messages = [responseMsgId]
            }
        }).Should().Emit();

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var responseStream = workspace.GetMeshNodeStream(responsePath);

        var initial = responseStream
            .Select(n => n.Content as ThreadMessage)
            .Should().Within(10.Seconds()).Match(m => m != null);
        initial!.ToolCalls.Where(c => !string.IsNullOrEmpty(c.DelegationPath)).Should().BeEmpty();
        Output.WriteLine("Phase 1: 0 delegations");

        responseStream.Update(current =>
        {
            var msg = current.Content as ThreadMessage ?? new ThreadMessage { Role = "assistant", Text = "" };
            return current with
            {
                Content = msg with
                {
                    ToolCalls = ImmutableList.Create(new ToolCallEntry
                    {
                        Name = "delegate_to_agent", DisplayName = "Worker: Analyze data",
                        DelegationPath = subThreadPath, IsSuccess = true, Timestamp = DateTime.UtcNow
                    })
                }
            };
        }).Should().Emit();

        var delegations = responseStream
            .Select(n => (n.Content as ThreadMessage)?.ToolCalls
                .Where(c => !string.IsNullOrEmpty(c.DelegationPath)).ToList())
            .Should().Within(10.Seconds()).Match(d => d?.Count > 0);
        delegations.Should().HaveCount(1);
        delegations![0].DelegationPath.Should().Be(subThreadPath);
        Output.WriteLine($"Phase 2: delegation appeared â€” {delegations[0].DisplayName}");

        var threadStream = workspace.GetMeshNodeStream(threadPath);
        threadStream.Update(current =>
        {
            var t = current.Content as MeshThread ?? new MeshThread();
            return current with { Content = t with { Status = ThreadExecutionStatus.Idle, ActiveMessageId = null } };
        }).Should().Emit();

        var idle = threadStream
            .Select(n => n.Content as MeshThread)
            .Should().Within(10.Seconds()).Match(t => t is { IsExecuting: false });

        idle!.IsExecuting.Should().BeFalse();
        idle.ActiveMessageId.Should().BeNull("ActiveMessageId should be cleared when execution ends");
        Output.WriteLine("Phase 3: thread idle");
    }

    /// <summary>
    /// Opens the layout area for a response message, then updates tool calls
    /// via the stream. The layout-area subscriptions must NOT cause runaway
    /// version increments (host.UpdateData re-triggering the stream).
    /// </summary>
    [Fact]
    public void ToolCallsUpdate_WithLayoutArea_NoFeedbackLoop()
    {
        var threadPath = "User/Roland/_Thread/feedback-loop-test";
        var responseMsgId = "resp-loop";
        var responsePath = $"{threadPath}/{responseMsgId}";

        NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "",
                Type = ThreadMessageType.AgentResponse,
                AgentName = "TestAgent"
            }
        }).Should().Emit();

        NodeFactory.CreateNode(new MeshNode("feedback-loop-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = responseMsgId,
                Messages = [responseMsgId]
            }
        }).Should().Emit();

        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Open the layout area â€” activates ThreadMessageLayoutAreas.Overview
        // subscriptions (text + toolCalls + isExecuting).
        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(responsePath),
            new LayoutAreaReference(ThreadMessageNodeType.OverviewArea));

        layoutStream!
            .Where(ci => ci.Value.ValueKind != JsonValueKind.Null)
            .Should().Within(10.Seconds()).Emit();
        Output.WriteLine("Layout area rendered");

        var responseStream = workspace.GetMeshNodeStream(responsePath);

        // Record version BEFORE the update â€” we read it off the underlying
        // sync stream, the same one the Update writes through.
        var streamForVersion = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(responsePath), new MeshNodeReference());
        var versionBefore = streamForVersion.Hub.Version;
        Output.WriteLine($"Version before update: {versionBefore}");

        responseStream.Update(current =>
        {
            var msg = current.Content as ThreadMessage ?? new ThreadMessage { Role = "assistant", Text = "" };
            return current with
            {
                Content = msg with
                {
                    ToolCalls = ImmutableList.Create(new ToolCallEntry
                    {
                        Name = "Search",
                        DisplayName = "Searching nodes...",
                        Arguments = "query: test",
                        Timestamp = DateTime.UtcNow
                    })
                }
            };
        }).Should().Emit();

        // Settle window: let any feedback-loop emissions accumulate, then
        // measure the version delta. A guaranteed-empty stream waited on for a
        // fixed span is the void-safe equivalent of the old Task.Delay(500).
        Observable.Empty<Unit>().Should().NotEmit(500.Milliseconds());

        var versionAfter = streamForVersion.Hub.Version;
        Output.WriteLine($"Version after update + 500ms: {versionAfter}");

        var versionDelta = versionAfter - versionBefore;
        versionDelta.Should().BeLessThan(20,
            "a single tool call update should not cause runaway version increments. " +
            $"Delta was {versionDelta} â€” indicates a feedback loop in layout area subscriptions");
        Output.WriteLine($"Version delta: {versionDelta} (no feedback loop)");

        responseStream.Update(current =>
        {
            var msg = current.Content as ThreadMessage ?? new ThreadMessage { Role = "assistant", Text = "" };
            return current with
            {
                Content = msg with
                {
                    Text = "Done.",
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
        }).Should().Emit();

        Observable.Empty<Unit>().Should().NotEmit(500.Milliseconds());

        var versionFinal = streamForVersion.Hub.Version;
        var totalDelta = versionFinal - versionBefore;
        totalDelta.Should().BeLessThan(40,
            "two updates should not cause runaway versions. " +
            $"Total delta was {totalDelta}");
        Output.WriteLine($"Total version delta after 2 updates: {totalDelta}");
    }
}
