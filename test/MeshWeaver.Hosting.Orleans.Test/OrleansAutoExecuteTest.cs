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
/// Orleans integration test: BuildThreadWithMessages + AutoExecutePendingMessage.
/// Creates a thread with pre-populated Messages + PendingUserMessage in one shot.
/// Verifies that:
/// 1. AutoExecutePendingMessage creates the child ThreadMessage nodes
/// 2. UpdateThreadMessageContent routes to the response grain
/// 3. Execution completes and response text is written
///
/// This reproduces the production bug where UpdateThreadMessageContent
/// went to the thread grain instead of the response message grain
/// because the child nodes weren't created in persistence.
/// </summary>
[Collection(nameof(OrleansClusterCollection))]
public class OrleansAutoExecuteTest(SharedOrleansFixture fixture, ITestOutputHelper output) : TestBase(output)
{
    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await fixture.GetClientAsync($"autoexec-{name}-{Guid.NewGuid():N}", "Roland");

    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        // Canonical CQRS-correct read: target the per-node hub's MeshNodeReference
        // reducer, not an EntityCollection lookup. The owning hub is the source of
        // truth for MeshNode content; this avoids any catalog / index lag.
        var response = await client.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path))).FirstAsync().ToTask(ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(fixture.ClientMesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(fixture.ClientMesh.JsonSerializerOptions);
        return null;
    }

    /// <summary>
    /// BuildThreadWithMessages creates thread + auto-executes.
    /// Response cell must be created, receive UpdateThreadMessageContent,
    /// and have final response text. Thread must end with IsExecuting=false.
    /// </summary>
    [Fact]
    public async Task AutoExecute_CreatesResponseCell_And_CompletesExecution()
    {
        SharedOrleansFixture.SwappableFactory.SetInner(new AutoExecEchoChatClientFactory());
        try
        {
            var ct = new CancellationTokenSource(30.Seconds()).Token;
            var client = await GetClientAsync();

            // Build thread with pre-populated messages (auto-execute on activation)
            var (threadNode, userMsgId, responseMsgId) = ThreadNodeType.BuildThreadWithMessages(
                "User/Roland", "Hello Orleans auto-execute!",
                createdBy: "Roland", agentName: "Orchestrator");
            var threadPath = threadNode.Path!;
            Output.WriteLine($"Thread: {threadPath}, user={userMsgId}, response={responseMsgId}");

            // Create the thread â€” AutoExecutePendingMessage should fire on grain activation
            var createResponse = await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("User/Roland"))).FirstAsync().ToTask(ct);
            createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error);
            Output.WriteLine("Thread created, waiting for execution...");

            // Poll for execution to complete
            ThreadMessage? response = null;
            var responsePath = $"{threadPath}/{responseMsgId}";
            for (var i = 0; i < 60; i++)
            {
                // Check thread state
                var thread = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
                if (thread is { IsExecuting: false, PendingUserMessage: null })
                {
                    Output.WriteLine($"Thread execution complete after {i * 500}ms");

                    // Verify response cell has content
                    response = await GetHubContentAsync<ThreadMessage>(client, responsePath, ct);
                    break;
                }
                await Task.Delay(500, ct);
            }

            response.Should().NotBeNull("response message must exist in persistence");
            response!.Text.Should().NotBeNullOrEmpty("agent should have written response text");
            Output.WriteLine($"Response: {response.Text[..Math.Min(100, response.Text.Length)]}");

            // Verify user cell exists
            var userMsg = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{userMsgId}", ct);
            userMsg.Should().NotBeNull("user message must exist in persistence");
            userMsg!.Text.Should().Be("Hello Orleans auto-execute!");
            userMsg.Role.Should().Be("user");

            Output.WriteLine("PASSED");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }

    /// <summary>
    /// Verifies that UpdateThreadMessageContent reaches the response grain (not the thread grain).
    /// The response cell should have text != "" and != "Allocating agent...".
    /// </summary>
    [Fact]
    public async Task AutoExecute_UpdateThreadMessageContent_RoutesToResponseGrain()
    {
        SharedOrleansFixture.SwappableFactory.SetInner(new AutoExecEchoChatClientFactory());
        try
        {
            var ct = new CancellationTokenSource(30.Seconds()).Token;
            var client = await GetClientAsync();

            var (threadNode, _, responseMsgId) = ThreadNodeType.BuildThreadWithMessages(
                "User/Roland", "Test routing to response grain",
                createdBy: "Roland", agentName: "Orchestrator");
            var threadPath = threadNode.Path!;
            var responsePath = $"{threadPath}/{responseMsgId}";

            await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("User/Roland"))).FirstAsync().ToTask(ct);

            // Poll for response cell to have final text (not empty, not "Allocating agent...")
            for (var i = 0; i < 60; i++)
            {
                var msg = await GetHubContentAsync<ThreadMessage>(client, responsePath, ct);
                if (msg?.Text is { Length: > 0 } text && !text.StartsWith("Allocating") && !text.StartsWith("Loading") && !text.StartsWith("Generating"))
                {
                    Output.WriteLine($"Response cell has final text after {i * 500}ms: {text[..Math.Min(80, text.Length)]}");
                    return; // SUCCESS
                }
                await Task.Delay(500, ct);
            }
            throw new TimeoutException("UpdateThreadMessageContent never reached response cell with final text");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }

    #region Echo LLM

    private class AutoExecEchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("AutoExecEcho");
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

    private class AutoExecEchoChatClientFactory : IChatClientFactory
    {
        public string Name => "AutoExecEchoFactory";
        public IReadOnlyList<string> Models => ["echo-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => new(chatClient: new AutoExecEchoChatClient(), instructions: "Echo agent.",
                name: config.Id, description: config.Description ?? "",
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
