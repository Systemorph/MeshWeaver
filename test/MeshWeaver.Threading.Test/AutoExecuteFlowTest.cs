using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests the portal flow 1:1:
/// 1. Create thread via BuildThreadNode + CreateNodeRequest
/// 2. GUI creates user + response cells via CreateNodeRequest
/// 3. GUI sends SubmitMessageRequest with cell IDs
/// 4. Server executes, writes response to the response cell
/// </summary>
public class AutoExecuteFlowTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/TestUser";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new EchoChatClientFactory());
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    [Fact]
    public async Task PortalFlow_CreateThread_CreateCells_Submit_ResponseWritten()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        // Step 1: Create thread (BuildThreadNode — no PendingUserMessage)
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "Hello portal flow!", "TestUser");
        var threadPath = threadNode.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address), ct);
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error);
        Output.WriteLine("Thread created");

        // Step 2: GUI creates cells (thread exists now)
        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];

        client.Post(new CreateNodeRequest(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage
            {
                Role = "user", Text = "Hello portal flow!", Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput, CreatedBy = "TestUser"
            }
        }), o => o.WithTarget(new Address(threadPath)));

        client.Post(new CreateNodeRequest(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse, AgentName = "Orchestrator"
            }
        }), o => o.WithTarget(new Address(threadPath)));

        // Step 3: Submit with cell IDs
        Output.WriteLine("Submitting message...");
        var submitResp = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Hello portal flow!",
                UserMessageId = userMsgId,
                ResponseMessageId = responseMsgId,
                AgentName = "Orchestrator",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResp.Message.Success.Should().BeTrue(submitResp.Message.Error);
        Output.WriteLine($"Submit response: Messages=[{string.Join(",", submitResp.Message.Messages ?? [])}]");

        // Step 4: Wait for execution to complete
        for (var i = 0; i < 60; i++)
        {
            var dataResp = await client.AwaitResponse(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(threadPath)), ct);
            var node = dataResp.Message.Data as MeshNode;
            if (node?.Content is JsonElement je)
                node = node with { Content = je.Deserialize<MeshThread>(Mesh.JsonSerializerOptions) };
            var thread = node?.Content as MeshThread;

            if (thread is { IsExecuting: false })
            {
                Output.WriteLine($"Execution complete after {i * 500}ms");

                // Verify response cell
                var responsePath = $"{threadPath}/{responseMsgId}";
                var responseResp = await client.AwaitResponse(
                    new GetDataRequest(new MeshNodeReference()),
                    o => o.WithTarget(new Address(responsePath)), ct);
                var rNode = responseResp.Message.Data as MeshNode;
                if (rNode?.Content is JsonElement rje)
                    rNode = rNode with { Content = rje.Deserialize<ThreadMessage>(Mesh.JsonSerializerOptions) };
                var responseMsg = rNode?.Content as ThreadMessage;
                responseMsg.Should().NotBeNull();
                responseMsg!.Text.Should().NotBeNullOrEmpty("agent should have written response");
                Output.WriteLine($"Response: {responseMsg.Text[..Math.Min(80, responseMsg.Text.Length)]}");
                return; // SUCCESS
            }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("Execution did not complete");
    }

    [Fact]
    public async Task PortalFlow_ResponseCell_GetsUpdatedByExecution()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "Test response update", "TestUser");
        var threadPath = threadNode.Path!;

        await client.AwaitResponse(
            new CreateNodeRequest(threadNode), o => o.WithTarget(Mesh.Address), ct);

        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];
        var responsePath = $"{threadPath}/{responseMsgId}";

        client.Post(new CreateNodeRequest(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage { Role = "user", Text = "Test response update", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.ExecutedInput }
        }), o => o.WithTarget(new Address(threadPath)));

        client.Post(new CreateNodeRequest(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = ContextPath,
            Content = new ThreadMessage { Role = "assistant", Text = "", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse }
        }), o => o.WithTarget(new Address(threadPath)));

        await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath, UserMessageText = "Test response update",
                UserMessageId = userMsgId, ResponseMessageId = responseMsgId, ContextPath = ContextPath
            }, o => o.WithTarget(new Address(threadPath)), ct);

        for (var i = 0; i < 60; i++)
        {
            var responseResp = await client.AwaitResponse(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(responsePath)), ct);
            var rNode = responseResp.Message.Data as MeshNode;
            if (rNode?.Content is JsonElement rje)
                rNode = rNode with { Content = rje.Deserialize<ThreadMessage>(Mesh.JsonSerializerOptions) };
            var msg = rNode?.Content as ThreadMessage;
            if (msg?.Text is { Length: > 0 } text
                && !text.StartsWith("Allocating") && !text.StartsWith("Loading") && !text.StartsWith("Generating"))
            {
                Output.WriteLine($"Response cell updated: {text[..Math.Min(80, text.Length)]}");
                return;
            }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("Response cell never got final text");
    }

    #region Echo LLM

    private class EchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("EchoProvider");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Echo: {messages.Count()} messages")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"Echo: {messages.Count()} messages received.");
            await Task.Delay(10, ct);
        }

        public object? GetService(Type serviceType, object? key = null) => serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class EchoChatClientFactory : IChatClientFactory
    {
        public string Name => "EchoFactory";
        public IReadOnlyList<string> Models => ["echo-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => new(chatClient: new EchoChatClient(), instructions: "Echo agent.",
                name: config.Id, description: config.Description ?? "",
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
