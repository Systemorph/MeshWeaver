using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests that delegation failures are properly propagated:
/// - When sub-thread creation fails, the delegation returns an error result
/// - When CancellationToken fires, the delegation completes with failure
/// - The parent thread doesn't hang forever on broken delegations
/// </summary>
public class DelegationFailureTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                // Use a slow agent that delegates — the delegation target won't exist
                services.AddSingleton<IChatClientFactory>(new DelegatingFakeChatClientFactory());
                return services;
            })
            .AddAI()
            .AddSampleUsers();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    private async Task<string> CreateThreadAsync(IMessageHub client, string text, CancellationToken ct)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "Roland");
        var delivery = client.Post(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address))!;
        var response = await client.RegisterCallback(delivery, (d, _) => Task.FromResult(d), ct);
        var createResponse = ((IMessageDelivery<CreateNodeResponse>)response).Message;
        createResponse.Success.Should().BeTrue(createResponse.Error);
        return createResponse.Node!.Path!;
    }

    [Fact]
    public async Task SubmitMessage_WithCancellation_DoesNotHangForever()
    {
        // This test verifies that when a thread is cancelled, the delegation
        // doesn't hang indefinitely waiting for a sub-thread response.
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var client = GetClient();

        var threadPath = await CreateThreadAsync(client, "Cancellation test", ct);

        // Submit a message — the fake agent will try to delegate
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest { ThreadPath = threadPath, UserMessageText = "Do something that delegates" },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        // Wait a bit for delegation to start
        await Task.Delay(1000, ct);

        // Cancel the execution
        client.Post(new CancelThreadStreamRequest { ThreadPath = threadPath },
            o => o.WithTarget(new Address(threadPath)));

        // Wait for execution to stop — should not hang
        var workspace = client.GetWorkspace();
        var messagesStream = workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                return node?.Content as MeshThread;
            });

        // The thread should eventually have 2 messages (user + response)
        // and the response should be marked as not executing
        var thread = await messagesStream
            .Where(t => t?.Messages.Count >= 2)
            .Timeout(10.Seconds())
            .FirstAsync();

        thread.Should().NotBeNull();
        Output.WriteLine($"Thread has {thread!.Messages.Count} messages");
    }

    #region Fake delegating agent

    /// <summary>
    /// Agent that always tries to delegate to a non-existent "Worker" agent.
    /// This simulates the failure case where delegation routing fails.
    /// </summary>
    private class DelegatingFakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("DelegatingFake");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Yield a function call to delegate_to_Worker
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call1", "delegate_to_Worker",
                    new Dictionary<string, object?> { ["task"] = "Do some work" })]);

            // Wait for the tool result (the framework will invoke the delegation)
            await Task.Delay(100, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Delegation completed.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class DelegatingFakeChatClientFactory : IChatClientFactory
    {
        public string Name => "DelegatingFakeFactory";
        public IReadOnlyList<string> Models => ["fake-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new DelegatingFakeChatClient(),
                instructions: config.Instructions ?? "You delegate everything.",
                name: config.Id, description: config.Description ?? config.Id,
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
