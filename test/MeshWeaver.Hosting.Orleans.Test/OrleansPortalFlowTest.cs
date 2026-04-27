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

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans integration: exact portal flow.
/// 1. Create thread (BuildThreadNode)
/// 2. Create user cell â†’ verify
/// 3. Create response cell â†’ verify
/// 4. SubmitMessageRequest (state update only) â†’ verify
/// 5. WatchForExecution triggers execution
/// 6. Response cell gets agent text
///
/// TODO(append-migration): SubmitMessageRequest still used because this test
/// specifically exercises the legacy "client creates user + response cells then
/// posts SubmitMessageRequest with both UserMessageId + ResponseMessageId" flow.
/// The new AppendUserMessageRequest path makes the server own cell creation
/// (via PendingUserMessages + the watcher), so the explicit pre-created cell ids
/// have no equivalent. Production code (thread hub â†’ _Exec) still routes through
/// SubmitMessageRequest with explicit ResponseMessageId, so this Orleans-level
/// flow check remains meaningful until the legacy code is removed.
/// </summary>
[Collection(nameof(OrleansClusterCollection))]
public class OrleansPortalFlowTest(SharedOrleansFixture fixture, ITestOutputHelper output) : OrleansSharedTestBase(fixture, output)
{
    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await base.GetClientAsync($"portal-{name}-{Guid.NewGuid():N}", "Roland");

    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        // Canonical CQRS-correct read via per-node MeshNodeReference reducer.
        var response = await client.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path))).FirstAsync().ToTask(ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(Fixture.ClientMesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(Fixture.ClientMesh.JsonSerializerOptions);
        return null;
    }

    /// <summary>
    /// Exact portal flow: create thread â†’ create cells (verified) â†’ submit â†’ execution â†’ response.
    /// </summary>
    // TODO(append-migration): kept on SubmitMessageRequest â€” see class-level comment.
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
            var createResp = await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("User/Roland"))).FirstAsync().ToTask(ct);
            createResp.Message.Success.Should().BeTrue(createResp.Message.Error);
            var threadPath = createResp.Message.Node!.Path!;
            Output.WriteLine($"Thread: {threadPath}");

            // Step 2: Create user cell â†’ verify
            var userMsgId = Guid.NewGuid().ToString("N")[..8];
            var responseMsgId = Guid.NewGuid().ToString("N")[..8];

            var userCellResp = await client.Observe(new CreateNodeRequest(new MeshNode(userMsgId, threadPath)
                {
                    NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                    Content = new ThreadMessage
                    {
                        Role = "user", Text = "Portal flow Orleans test", Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.ExecutedInput, CreatedBy = "Roland"
                    }
                }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);
            userCellResp.Message.Success.Should().BeTrue("user cell creation must succeed");
            Output.WriteLine($"User cell created: {userMsgId}");

            // Step 3: Create response cell â†’ verify
            var responseCellResp = await client.Observe(new CreateNodeRequest(new MeshNode(responseMsgId, threadPath)
                {
                    NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                    Content = new ThreadMessage
                    {
                        Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.AgentResponse, AgentName = "Orchestrator"
                    }
                }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);
            responseCellResp.Message.Success.Should().BeTrue("response cell creation must succeed");
            Output.WriteLine($"Response cell created: {responseMsgId}");

            // Step 4: Submit â€” updates state, WatchForExecution triggers execution
            var submitResp = await client.Observe(new SubmitMessageRequest
                {
                    ThreadPath = threadPath,
                    UserMessageText = "Portal flow Orleans test",
                    UserMessageId = userMsgId,
                    ResponseMessageId = responseMsgId,
                    AgentName = "Orchestrator",
                    ContextPath = "User/Roland"
                }, o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);
            submitResp.Message.Success.Should().BeTrue("submit must succeed");
            Output.WriteLine("Submitted â€” WatchForExecution should trigger");

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
    // TODO(append-migration): kept on SubmitMessageRequest â€” see class-level comment.
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
            var createResp = await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("User/Roland"))).FirstAsync().ToTask(ct);
            var threadPath = createResp.Message.Node!.Path!;

            var u1 = Guid.NewGuid().ToString("N")[..8];
            var r1 = Guid.NewGuid().ToString("N")[..8];
            await client.Observe(new CreateNodeRequest(new MeshNode(u1, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "user", Text = "First question", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.ExecutedInput }
            }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);
            await client.Observe(new CreateNodeRequest(new MeshNode(r1, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "assistant", Text = "", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse }
            }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);
            await client.Observe(new SubmitMessageRequest
            {
                ThreadPath = threadPath, UserMessageText = "First question",
                UserMessageId = u1, ResponseMessageId = r1, ContextPath = "User/Roland"
            }, o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);

            // Wait for first execution to complete
            for (var i = 0; i < 60; i++)
            {
                var t = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
                if (t is { IsExecuting: false }) break;
                await Task.Delay(500, ct);
            }
            Output.WriteLine("First message complete");

            // Second message â€” same thread, new cells
            var u2 = Guid.NewGuid().ToString("N")[..8];
            var r2 = Guid.NewGuid().ToString("N")[..8];
            await client.Observe(new CreateNodeRequest(new MeshNode(u2, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "user", Text = "Second question", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.ExecutedInput }
            }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);
            await client.Observe(new CreateNodeRequest(new MeshNode(r2, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "assistant", Text = "", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse }
            }), o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);
            await client.Observe(new SubmitMessageRequest
            {
                ThreadPath = threadPath, UserMessageText = "Second question",
                UserMessageId = u2, ResponseMessageId = r2, ContextPath = "User/Roland"
            }, o => o.WithTarget(new Address(threadPath))).FirstAsync().ToTask(ct);

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
