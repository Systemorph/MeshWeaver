using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans integration: exact portal flow.
/// 1. Create thread (BuildThreadNode)
/// 2. Create user cell → verify
/// 3. Create response cell → verify
/// 4. SubmitMessageRequest (state update only) → verify
/// 5. WatchForExecution triggers execution
/// 6. Response cell gets agent text
/// </summary>
[Collection(nameof(OrleansClusterCollection))]
public class OrleansPortalFlowTest(SharedOrleansFixture fixture, ITestOutputHelper output) : TestBase(output)
{
    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await fixture.GetClientAsync($"portal-{name}-{Guid.NewGuid():N}", "Roland");

    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        var nodeId = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        var response = await client.AwaitResponse(
            new GetDataRequest(new EntityReference(nameof(MeshNode), nodeId)),
            o => o.WithTarget(new Address(path)), ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(fixture.ClientMesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(fixture.ClientMesh.JsonSerializerOptions);
        return null;
    }

    /// <summary>
    /// Exact portal flow: create thread → create cells (verified) → submit → execution → response.
    /// </summary>
    [Fact]
    public async Task PortalFlow_CreateThread_CreateCells_Submit_ExecutionCompletes()
    {
        SharedOrleansFixture.SwappableFactory.SetInner(new PortalFlowEchoChatClientFactory());
        try
        {
            var ct = new CancellationTokenSource(30.Seconds()).Token;
            var client = await GetClientAsync();

            // Step 1: Create thread
            var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "Portal flow Orleans test", "Roland");
            var createResp = await client.AwaitResponse(
                new CreateNodeRequest(threadNode),
                o => o.WithTarget(new Address("User/Roland")), ct);
            createResp.Message.Success.Should().BeTrue(createResp.Message.Error);
            var threadPath = createResp.Message.Node!.Path!;
            Output.WriteLine($"Thread: {threadPath}");

            // Step 2: Create user cell → verify
            var userMsgId = Guid.NewGuid().ToString("N")[..8];
            var responseMsgId = Guid.NewGuid().ToString("N")[..8];

            var userCellResp = await client.AwaitResponse(
                new CreateNodeRequest(new MeshNode(userMsgId, threadPath)
                {
                    NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                    Content = new ThreadMessage
                    {
                        Role = "user", Text = "Portal flow Orleans test", Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.ExecutedInput, CreatedBy = "Roland"
                    }
                }), o => o.WithTarget(new Address(threadPath)), ct);
            userCellResp.Message.Success.Should().BeTrue("user cell creation must succeed");
            Output.WriteLine($"User cell created: {userMsgId}");

            // Step 3: Create response cell → verify
            var responseCellResp = await client.AwaitResponse(
                new CreateNodeRequest(new MeshNode(responseMsgId, threadPath)
                {
                    NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                    Content = new ThreadMessage
                    {
                        Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.AgentResponse, AgentName = "Orchestrator"
                    }
                }), o => o.WithTarget(new Address(threadPath)), ct);
            responseCellResp.Message.Success.Should().BeTrue("response cell creation must succeed");
            Output.WriteLine($"Response cell created: {responseMsgId}");

            // Step 4: Submit — updates state, WatchForExecution triggers execution
            var submitResp = await client.AwaitResponse(
                new SubmitMessageRequest
                {
                    ThreadPath = threadPath,
                    UserMessageText = "Portal flow Orleans test",
                    UserMessageId = userMsgId,
                    ResponseMessageId = responseMsgId,
                    AgentName = "Orchestrator",
                    ContextPath = "User/Roland"
                }, o => o.WithTarget(new Address(threadPath)), ct);
            submitResp.Message.Success.Should().BeTrue("submit must succeed");
            Output.WriteLine("Submitted — WatchForExecution should trigger");

            // Step 5: Poll for execution to complete
            var responsePath = $"{threadPath}/{responseMsgId}";
            for (var i = 0; i < 60; i++)
            {
                var thread = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
                if (thread is { IsExecuting: false })
                {
                    Output.WriteLine($"Execution complete after {i * 500}ms");

                    var responseMsg = await GetHubContentAsync<ThreadMessage>(client, responsePath, ct);
                    responseMsg.Should().NotBeNull("response cell must exist");
                    responseMsg!.Text.Should().NotBeNullOrEmpty("agent must have written response");
                    Output.WriteLine($"Response: {responseMsg.Text[..Math.Min(100, responseMsg.Text.Length)]}");

                    // Verify user cell
                    var userMsg = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{userMsgId}", ct);
                    userMsg.Should().NotBeNull();
                    userMsg!.Text.Should().Be("Portal flow Orleans test");

                    Output.WriteLine("PASSED");
                    return;
                }
                await Task.Delay(500, ct);
            }
            throw new TimeoutException("Execution did not complete");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }

    /// <summary>
    /// Existing thread: second message on a thread that already has messages.
    /// Verifies WatchForExecution triggers for new ActiveMessageId.
    /// </summary>
    [Fact]
    public async Task ExistingThread_SecondMessage_ExecutionCompletes()
    {
        SharedOrleansFixture.SwappableFactory.SetInner(new PortalFlowEchoChatClientFactory());
        try
        {
            var ct = new CancellationTokenSource(30.Seconds()).Token;
            var client = await GetClientAsync();

            // Create thread + first message pair
            var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "Multi-message test", "Roland");
            var createResp = await client.AwaitResponse(
                new CreateNodeRequest(threadNode),
                o => o.WithTarget(new Address("User/Roland")), ct);
            var threadPath = createResp.Message.Node!.Path!;

            var u1 = Guid.NewGuid().ToString("N")[..8];
            var r1 = Guid.NewGuid().ToString("N")[..8];
            await client.AwaitResponse(new CreateNodeRequest(new MeshNode(u1, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "user", Text = "First question", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.ExecutedInput }
            }), o => o.WithTarget(new Address(threadPath)), ct);
            await client.AwaitResponse(new CreateNodeRequest(new MeshNode(r1, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "assistant", Text = "", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse }
            }), o => o.WithTarget(new Address(threadPath)), ct);
            await client.AwaitResponse(new SubmitMessageRequest
            {
                ThreadPath = threadPath, UserMessageText = "First question",
                UserMessageId = u1, ResponseMessageId = r1, ContextPath = "User/Roland"
            }, o => o.WithTarget(new Address(threadPath)), ct);

            // Wait for first execution to complete
            for (var i = 0; i < 60; i++)
            {
                var t = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
                if (t is { IsExecuting: false }) break;
                await Task.Delay(500, ct);
            }
            Output.WriteLine("First message complete");

            // Second message — same thread, new cells
            var u2 = Guid.NewGuid().ToString("N")[..8];
            var r2 = Guid.NewGuid().ToString("N")[..8];
            await client.AwaitResponse(new CreateNodeRequest(new MeshNode(u2, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "user", Text = "Second question", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.ExecutedInput }
            }), o => o.WithTarget(new Address(threadPath)), ct);
            await client.AwaitResponse(new CreateNodeRequest(new MeshNode(r2, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "assistant", Text = "", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse }
            }), o => o.WithTarget(new Address(threadPath)), ct);
            await client.AwaitResponse(new SubmitMessageRequest
            {
                ThreadPath = threadPath, UserMessageText = "Second question",
                UserMessageId = u2, ResponseMessageId = r2, ContextPath = "User/Roland"
            }, o => o.WithTarget(new Address(threadPath)), ct);

            // Wait for second execution
            for (var i = 0; i < 60; i++)
            {
                var t = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
                if (t is { IsExecuting: false } && t.Messages.Count >= 4)
                {
                    Output.WriteLine($"Second message complete after {i * 500}ms, Messages={t.Messages.Count}");
                    var responseMsg = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{r2}", ct);
                    responseMsg.Should().NotBeNull();
                    responseMsg!.Text.Should().NotBeNullOrEmpty();
                    Output.WriteLine($"Response: {responseMsg.Text[..Math.Min(80, responseMsg.Text.Length)]}");
                    Output.WriteLine("PASSED");
                    return;
                }
                await Task.Delay(500, ct);
            }
            throw new TimeoutException("Second execution did not complete");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }

    #region Echo LLM

    private class PortalFlowEchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("PortalFlowEcho");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Echo: {messages.Count()} messages")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"Portal echo: received {messages.Count()} messages.");
            await Task.Delay(10, ct);
        }

        public object? GetService(Type serviceType, object? key = null) => serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class PortalFlowEchoChatClientFactory : IChatClientFactory
    {
        public string Name => "PortalFlowEchoFactory";
        public IReadOnlyList<string> Models => ["echo-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => new(chatClient: new PortalFlowEchoChatClient(), instructions: "Echo agent.",
                name: config.Id, description: config.Description ?? "",
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
