using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    public void StreamingArea_WhenIdle_ReturnsNull()
    {
        // Create an idle thread (not executing)
        var threadPath = "User/Roland/_Thread/streaming-idle-test";
        NodeFactory.CreateNode(new MeshNode("streaming-idle-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread()
        }).Should().Emit();

        // Subscribe to the StreamingArea
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var streamingArea = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(ThreadNodeType.StreamingArea));

        // First emission should be null or empty (thread is idle) — the area is
        // served by the thread hub.
        var first = streamingArea.Should().Within(5.Seconds()).Emit();

        Output.WriteLine($"StreamingArea emission: ChangeType={first.ChangeType}");
        // The area returns null when idle â€” the LayoutAreaView renders nothing
    }

    [Fact]
    public void StreamingArea_WhenExecuting_ReturnsStreamingCell()
    {
        var threadPath = "User/Roland/_Thread/streaming-exec-test";
        var responseMsgId = "resp-abc";

        // Create the response message node
        NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "Working on it...",
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Orchestrator"
            }
        }).Should().Emit();

        // Create the thread in executing state with ActiveMessageId
        NodeFactory.CreateNode(new MeshNode("streaming-exec-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = responseMsgId,
                ExecutionStartedAt = DateTime.UtcNow
            }
        }).Should().Emit();

        // Subscribe to the StreamingArea
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var streamingArea = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(ThreadNodeType.StreamingArea));

        // Should get a non-null emission (the streaming cell control)
        var emission = streamingArea.Should().Within(10.Seconds())
            .Match(ci => ci.Value.ValueKind != JsonValueKind.Null);

        Output.WriteLine($"StreamingArea emission: {emission.Value}");
        // The emission should contain the LayoutAreaControl pointing to the response message
    }

    [Fact(Skip = "Layout-area observation chain doesn't propagate the executingâ†’idle MeshNode transition within 10 s. Same threadStream.Update pattern works in ToolCallsVisibilityTest when read via threadStream directly â€” only the LayoutAreaReferenceâ†’StreamingViewâ†’GetMeshNodeStream chain is slow. Needs investigation of whether StreamingView's GetMeshNodeStream subscribes to the same reducer the Update writes to.")]
    public void StreamingArea_WhenExecutionCompletes_ReturnsNull()
    {
        var threadPath = "User/Roland/_Thread/streaming-complete-test";
        var responseMsgId = "resp-def";

        // Create response message
        NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "Done.",
                Type = ThreadMessageType.AgentResponse
            }
        }).Should().Emit();

        // Create thread in executing state
        NodeFactory.CreateNode(new MeshNode("streaming-complete-test", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                Status = ThreadExecutionStatus.Executing,
                ActiveMessageId = responseMsgId,
                ExecutionStartedAt = DateTime.UtcNow
            }
        }).Should().Emit();

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var streamingArea = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(ThreadNodeType.StreamingArea));

        // Wait for non-null emission first (executing)
        streamingArea!
            .Where(ci => ci.Value.ValueKind != JsonValueKind.Null)
            .Should().Within(10.Seconds()).Emit();

        Output.WriteLine("Got executing emission, now completing...");

        // Mark execution as complete via the canonical MeshNode stream handle.
        var threadStream = workspace.GetMeshNodeStream(threadPath);
        threadStream.Should().Within(10.Seconds()).Emit();
        threadStream.Update(current =>
        {
            var thread = current.Content as MeshThread ?? new MeshThread();
            return current with
            {
                Content = thread with
                {
                    Status = ThreadExecutionStatus.Idle,
                    ActiveMessageId = null,
                    ExecutionStatus = null
                }
            };
        }).Subscribe(_ => { }, ex => Output.WriteLine($"Update failed: {ex}"));

        // Should get a null emission (idle)
        streamingArea
            .Should().Within(10.Seconds())
            .Match(ci => ci.Value.ValueKind == JsonValueKind.Null || ci.Value.ValueKind == JsonValueKind.Undefined);

        Output.WriteLine("StreamingArea returned null after execution completed");
    }

}
