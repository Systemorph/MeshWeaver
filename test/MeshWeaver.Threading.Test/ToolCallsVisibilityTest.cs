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

    [Fact(Timeout = 30_000)]
    public async Task ResponseMessage_ToolCalls_VisibleViaRemoteStream()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

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

        await NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
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
        });

        await NodeFactory.CreateNode(new MeshNode("toolcalls-visible-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = responseMsgId,
                Messages = [responseMsgId]
            }
        });

        Output.WriteLine("Created thread with response message containing tool calls");

        var client = GetClient();
        var msg = (await ThreadFlow.ReadMessage(client, threadPath, responseMsgId,
            m => m.ToolCalls.Count >= 2, timeout: 10.Seconds()).FirstAsync().ToTask(ct));

        msg.ToolCalls.Should().HaveCount(2);
        msg.ToolCalls[0].Name.Should().Be("search_nodes");
        msg.ToolCalls[1].Name.Should().Be("delegate_to_agent");
        msg.ToolCalls[1].DelegationPath.Should().NotBeNullOrEmpty();
        Output.WriteLine($"Tool calls visible: {msg.ToolCalls.Count} calls, delegation={msg.ToolCalls[1].DelegationPath}");
    }

    [Fact(Timeout = 30_000)]
    public async Task ResponseMessage_ToolCallsUpdate_PropagatesViaStream()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/toolcalls-update-test";
        var responseMsgId = "resp-upd";
        var responsePath = $"{threadPath}/{responseMsgId}";

        await NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
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
        });

        await NodeFactory.CreateNode(new MeshNode("toolcalls-update-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = responseMsgId,
                Messages = [responseMsgId]
            }
        });

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var responseStream = workspace.GetMeshNodeStream(responsePath);

        var initial = await responseStream
            .Select(n => n.Content as ThreadMessage)
            .Where(m => m != null)
            .Take(1)
            .Timeout(10.Seconds())
            .ToTask(ct);
        initial!.ToolCalls.Should().BeEmpty("initially no tool calls");
        Output.WriteLine("Initial: 0 tool calls");

        var waitForToolCalls = responseStream
            .Select(n => n.Content as ThreadMessage)
            .Where(m => m?.ToolCalls.Count > 0)
            .Take(1)
            .Timeout(10.Seconds())
            .ToTask(ct);

        await responseStream.Update(current =>
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
        }).FirstAsync().ToTask(ct);

        var afterUpdate = await waitForToolCalls;
        afterUpdate!.ToolCalls.Should().HaveCount(1);
        afterUpdate.ToolCalls[0].Name.Should().Be("get_node");
        Output.WriteLine($"After update: {afterUpdate.ToolCalls.Count} tool calls - {afterUpdate.ToolCalls[0].DisplayName}");
    }

    [Fact(Timeout = 30_000)]
    public async Task Delegation_AppearsOnResponseMessage_ThenThreadGoesIdle()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/toolcalls-lifecycle-test";
        var responseMsgId = "resp-life";
        var responsePath = $"{threadPath}/{responseMsgId}";
        var subThreadPath = $"{responsePath}/sub-worker";

        await NodeFactory.CreateNode(new MeshNode("sub-worker", responsePath)
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread { Status = ThreadExecutionStatus.Executing, ActiveMessageId = "sub-resp" }
        });

        await NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "",
                Type = ThreadMessageType.AgentResponse, AgentName = "Orchestrator"
            }
        });

        await NodeFactory.CreateNode(new MeshNode("toolcalls-lifecycle-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                Status = ThreadExecutionStatus.Executing, ActiveMessageId = responseMsgId, Messages = [responseMsgId]
            }
        });

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var responseStream = workspace.GetMeshNodeStream(responsePath);

        var initial = await responseStream
            .Select(n => n.Content as ThreadMessage)
            .Where(m => m != null).Take(1).Timeout(10.Seconds()).ToTask(ct);
        initial!.ToolCalls.Where(c => !string.IsNullOrEmpty(c.DelegationPath)).Should().BeEmpty();
        Output.WriteLine("Phase 1: 0 delegations");

        var delegationAppeared = responseStream
            .Select(n => (n.Content as ThreadMessage)?.ToolCalls
                .Where(c => !string.IsNullOrEmpty(c.DelegationPath)).ToList())
            .Where(d => d?.Count > 0).Take(1).Timeout(10.Seconds()).ToTask(ct);

        await responseStream.Update(current =>
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
        }).FirstAsync().ToTask(ct);

        var delegations = await delegationAppeared;
        delegations.Should().HaveCount(1);
        delegations![0].DelegationPath.Should().Be(subThreadPath);
        Output.WriteLine($"Phase 2: delegation appeared â€” {delegations[0].DisplayName}");

        var threadStream = workspace.GetMeshNodeStream(threadPath);
        await threadStream.Update(current =>
        {
            var t = current.Content as MeshThread ?? new MeshThread();
            return current with { Content = t with { Status = ThreadExecutionStatus.Idle, ActiveMessageId = null } };
        }).FirstAsync().ToTask(ct);

        var idle = await threadStream
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Take(1)
            .Timeout(10.Seconds()).ToTask(ct);

        idle!.IsExecuting.Should().BeFalse();
        idle.ActiveMessageId.Should().BeNull("ActiveMessageId should be cleared when execution ends");
        Output.WriteLine("Phase 3: thread idle");
    }

    /// <summary>
    /// Opens the layout area for a response message, then updates tool calls
    /// via the stream. The layout-area subscriptions must NOT cause runaway
    /// version increments (host.UpdateData re-triggering the stream).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ToolCallsUpdate_WithLayoutArea_NoFeedbackLoop()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/feedback-loop-test";
        var responseMsgId = "resp-loop";
        var responsePath = $"{threadPath}/{responseMsgId}";

        await NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
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
        });

        await NodeFactory.CreateNode(new MeshNode("feedback-loop-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = responseMsgId,
                Messages = [responseMsgId]
            }
        });

        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Open the layout area â€” activates ThreadMessageLayoutAreas.Overview
        // subscriptions (text + toolCalls + isExecuting).
        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(responsePath),
            new LayoutAreaReference(ThreadMessageNodeType.OverviewArea));

        await layoutStream!
            .Where(ci => ci.Value.ValueKind != JsonValueKind.Null)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);
        Output.WriteLine("Layout area rendered");

        var responseStream = workspace.GetMeshNodeStream(responsePath);

        // Record version BEFORE the update â€” we read it off the underlying
        // sync stream, the same one the Update writes through.
        var streamForVersion = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(responsePath), new MeshNodeReference());
        var versionBefore = streamForVersion.Hub.Version;
        Output.WriteLine($"Version before update: {versionBefore}");

        await responseStream.Update(current =>
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
        }).FirstAsync().ToTask(ct);

        await Task.Delay(500, ct);

        var versionAfter = streamForVersion.Hub.Version;
        Output.WriteLine($"Version after update + 500ms: {versionAfter}");

        var versionDelta = versionAfter - versionBefore;
        versionDelta.Should().BeLessThan(20,
            "a single tool call update should not cause runaway version increments. " +
            $"Delta was {versionDelta} â€” indicates a feedback loop in layout area subscriptions");
        Output.WriteLine($"Version delta: {versionDelta} (no feedback loop)");

        await responseStream.Update(current =>
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
        }).FirstAsync().ToTask(ct);

        await Task.Delay(500, ct);

        var versionFinal = streamForVersion.Hub.Version;
        var totalDelta = versionFinal - versionBefore;
        totalDelta.Should().BeLessThan(40,
            "two updates should not cause runaway versions. " +
            $"Total delta was {totalDelta}");
        Output.WriteLine($"Total version delta after 2 updates: {totalDelta}");
    }
}
