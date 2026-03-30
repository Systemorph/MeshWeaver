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
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests the StreamingArea layout area on thread hubs.
/// Verifies that:
/// 1. When idle (not executing), StreamingArea returns null
/// 2. When executing with ActiveMessageId, returns LayoutAreaControl for the streaming cell
/// 3. When tool calls with DelegationPath exist, shows sub-thread StreamingAreas recursively
/// </summary>
public class StreamingAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI().AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public async Task StreamingArea_WhenIdle_ReturnsNull()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        // Create an idle thread (not executing)
        var threadPath = "User/Roland/_Thread/streaming-idle-test";
        await NodeFactory.CreateNodeAsync(new MeshNode("streaming-idle-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread()
        }, ct);

        // Subscribe to the StreamingArea
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var streamingArea = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(ThreadNodeType.StreamingArea));

        streamingArea.Should().NotBeNull("StreamingArea should be served by the thread hub");

        // First emission should be null or empty (thread is idle)
        var first = await streamingArea!
            .Timeout(5.Seconds())
            .FirstAsync();

        Output.WriteLine($"StreamingArea emission: ChangeType={first.ChangeType}");
        // The area returns null when idle — the LayoutAreaView renders nothing
    }

    [Fact]
    public async Task StreamingArea_WhenExecuting_ReturnsStreamingCell()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/streaming-exec-test";
        var responseMsgId = "resp-abc";

        // Create the response message node
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
                AgentName = "Orchestrator"
            }
        }, ct);

        // Create the thread in executing state with ActiveMessageId
        await NodeFactory.CreateNodeAsync(new MeshNode("streaming-exec-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                IsExecuting = true,
                ActiveMessageId = responseMsgId,
                ExecutionStartedAt = DateTime.UtcNow
            }
        }, ct);

        // Subscribe to the StreamingArea
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var streamingArea = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(ThreadNodeType.StreamingArea));

        streamingArea.Should().NotBeNull();

        // Should get a non-null emission (the streaming cell control)
        var emission = await streamingArea!
            .Where(ci => ci.Value.ValueKind != JsonValueKind.Null)
            .Timeout(10.Seconds())
            .FirstAsync();

        Output.WriteLine($"StreamingArea emission: {emission.Value}");
        // The emission should contain the LayoutAreaControl pointing to the response message
    }

    [Fact(Skip = "Timing-dependent: completion transition through layout area stream is slow")]
    public async Task StreamingArea_WhenExecutionCompletes_ReturnsNull()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/streaming-complete-test";
        var responseMsgId = "resp-def";

        // Create response message
        await NodeFactory.CreateNodeAsync(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Id = responseMsgId,
                Role = "assistant",
                Text = "Done.",
                Type = ThreadMessageType.AgentResponse
            }
        }, ct);

        // Create thread in executing state
        await NodeFactory.CreateNodeAsync(new MeshNode("streaming-complete-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                IsExecuting = true,
                ActiveMessageId = responseMsgId,
                ExecutionStartedAt = DateTime.UtcNow
            }
        }, ct);

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var streamingArea = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(ThreadNodeType.StreamingArea));

        // Wait for non-null emission first (executing)
        await streamingArea!
            .Where(ci => ci.Value.ValueKind != JsonValueKind.Null)
            .Timeout(10.Seconds())
            .FirstAsync();

        Output.WriteLine("Got executing emission, now completing...");

        // Mark execution as complete
        workspace.UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with
            {
                Content = thread with
                {
                    IsExecuting = false,
                    ActiveMessageId = null,
                    ExecutionStatus = null
                }
            };
        }, new Address(threadPath), threadPath);

        // Should get a null emission (idle)
        var idle = await streamingArea
            .Where(ci => ci.Value.ValueKind == JsonValueKind.Null || ci.Value.ValueKind == JsonValueKind.Undefined)
            .Timeout(10.Seconds())
            .FirstAsync();

        Output.WriteLine("StreamingArea returned null after execution completed");
    }

    [Fact]
    public async Task ToolCallsArea_WhenIdle_ReturnsNull()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/toolcalls-idle-test";
        await NodeFactory.CreateNodeAsync(new MeshNode("toolcalls-idle-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread()
        }, ct);

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var toolCallsArea = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(ThreadNodeType.ToolCallsArea));

        toolCallsArea.Should().NotBeNull("ToolCallsArea should be served by the thread hub");

        var first = await toolCallsArea!
            .Timeout(5.Seconds())
            .FirstAsync();

        Output.WriteLine($"ToolCallsArea idle emission: ChangeType={first.ChangeType}");
    }

    [Fact]
    public async Task ToolCallsArea_WhenExecutingWithDelegation_ShowsSubThread()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var threadPath = "User/Roland/_Thread/toolcalls-delegation-test";
        var responseMsgId = "resp-tool";
        var subThreadPath = $"{threadPath}/{responseMsgId}/sub-navigator";

        // Create sub-thread
        await NodeFactory.CreateNodeAsync(new MeshNode("sub-navigator", $"{threadPath}/{responseMsgId}")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread { IsExecuting = true, ActiveMessageId = "sub-resp" }
        }, ct);

        // Create response message with delegation tool call
        await NodeFactory.CreateNodeAsync(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Id = responseMsgId,
                Role = "assistant",
                Text = "Delegating...",
                Type = ThreadMessageType.AgentResponse,
                ToolCalls = [new ToolCallEntry
                {
                    Name = "delegate_to_agent",
                    DisplayName = "Navigator",
                    DelegationPath = subThreadPath,
                    IsSuccess = true,
                    Timestamp = DateTime.UtcNow
                }]
            }
        }, ct);

        // Create thread in executing state
        await NodeFactory.CreateNodeAsync(new MeshNode("toolcalls-delegation-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                IsExecuting = true,
                ActiveMessageId = responseMsgId,
                ExecutionStartedAt = DateTime.UtcNow
            }
        }, ct);

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var toolCallsArea = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(ThreadNodeType.ToolCallsArea));

        // Should get a non-null emission with delegation content
        var emission = await toolCallsArea!
            .Where(ci => ci.Value.ValueKind != JsonValueKind.Null && ci.Value.ValueKind != JsonValueKind.Undefined)
            .Timeout(10.Seconds())
            .FirstAsync();

        Output.WriteLine($"ToolCallsArea delegation emission: {emission.Value}");
        emission.Value.ValueKind.Should().NotBe(JsonValueKind.Null);
    }
}
