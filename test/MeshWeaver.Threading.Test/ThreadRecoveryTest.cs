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
/// Tests that stale executing threads are properly recovered on hub restart.
/// Creates MeshNodes mimicking running threads with active response messages,
/// then verifies that recovery marks the response as "*Cancelled*" and clears execution state.
/// </summary>
public class ThreadRecoveryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI().AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>
    /// A stale thread with an active response message should:
    /// 1. Mark the response message text as "*Cancelled*"
    /// 2. Clear IsExecuting, ExecutionStatus, ActiveMessageId
    /// 3. Mark ActiveProgress as completed
    /// </summary>
    [Fact]
    public async Task Recovery_StaleThread_ResponseMessageMarkedCancelled()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var threadPath = "User/Roland/_Thread/stale-with-response";
        var responseMsgId = "resp1234";
        var responsePath = $"{threadPath}/{responseMsgId}";

        // Create the stale response message node (partially streamed text)
        await meshService.CreateNodeAsync(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new ThreadMessage
            {
                Id = responseMsgId,
                Role = "assistant",
                Text = "Here is the beginning of my response",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Executor"
            }
        }, ct);

        // Create the stale thread node
        await meshService.CreateNodeAsync(new MeshNode("stale-with-response", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                ParentPath = "User/Roland",
                IsExecuting = true,
                ExecutionStatus = "search_nodes",
                ActiveMessageId = responseMsgId,
                ExecutionStartedAt = DateTime.UtcNow.AddMinutes(-5),
                Messages = [responseMsgId],
                ActiveProgress = new ThreadProgressEntry
                {
                    ThreadPath = threadPath,
                    ThreadName = "Executor",
                    Status = "search_nodes"
                }
            }
        }, ct);

        Output.WriteLine("Created stale thread with response message");

        // Subscribe to the thread's stream — hub activation triggers recovery
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var threadStream = workspace.GetRemoteStream<MeshNode>(
            new Address(threadPath), new MeshNodeReference());

        // Wait for thread to reach IsExecuting=false
        var recovered = await threadStream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        // Verify thread state
        recovered.Should().NotBeNull();
        recovered!.IsExecuting.Should().BeFalse();
        recovered.ExecutionStatus.Should().StartWith("Cancelled at ");
        recovered.ActiveMessageId.Should().BeNull();
        recovered.ExecutionStartedAt.Should().BeNull();
        Output.WriteLine($"Thread recovered: IsExecuting={recovered.IsExecuting}, status={recovered.ExecutionStatus}");

        // Verify ActiveProgress marked completed
        recovered.ActiveProgress.Should().NotBeNull();
        recovered.ActiveProgress!.IsCompleted.Should().BeTrue();
        Output.WriteLine($"ActiveProgress completed: {recovered.ActiveProgress.IsCompleted}");

        // Verify response message has "*Cancelled*" appended
        var responseStream = workspace.GetRemoteStream<MeshNode>(
            new Address(responsePath), new MeshNodeReference());

        var responseMsg = await responseStream
            .Select(ci => ci.Value?.Content as ThreadMessage)
            .Where(m => m != null && m.Text.Contains("Cancelled"))
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        responseMsg.Should().NotBeNull();
        responseMsg!.Text.Should().Contain("*Cancelled");
        responseMsg.Text.Should().Contain("last updated");
        responseMsg.Text.Should().StartWith("Here is the beginning of my response");
        Output.WriteLine($"Response message: '{responseMsg.Text}'");
    }

    /// <summary>
    /// A stale thread with NO active message (e.g., crashed before creating cells)
    /// should just clear execution state without errors.
    /// </summary>
    [Fact]
    public async Task Recovery_StaleThread_NoActiveMessage_ClearsState()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var threadPath = "User/Roland/_Thread/stale-no-msg";

        await meshService.CreateNodeAsync(new MeshNode("stale-no-msg", "User/Roland/_Thread")
        {
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                ParentPath = "User/Roland",
                IsExecuting = true,
                ExecutionStatus = "Generating response...",
                ExecutionStartedAt = DateTime.UtcNow.AddMinutes(-10)
            }
        }, ct);

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<MeshNode>(
            new Address(threadPath), new MeshNodeReference());

        var recovered = await stream
            .Select(ci => ci.Value?.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        recovered.Should().NotBeNull();
        recovered!.IsExecuting.Should().BeFalse();
        recovered.ExecutionStatus.Should().StartWith("Cancelled at ");
        Output.WriteLine($"Thread recovered without active message: IsExecuting={recovered.IsExecuting}, status={recovered.ExecutionStatus}");
    }
}
