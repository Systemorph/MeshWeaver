using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
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
/// Orleans integration test: cold-start scenario.
/// Pre-seeds a thread with 2 user + 2 assistant messages in persistence.
/// Submits a 3rd message and verifies the agent sees ALL 5 previous messages
/// via GetDataRequest + CombineLatest (not from cache or local workspace).
/// </summary>
[Collection(nameof(OrleansClusterCollection))]
public class OrleansChatHistoryTest(SharedOrleansFixture fixture, ITestOutputHelper output) : TestBase(output)
{
    private const string ThreadPath = "User/Roland/_Thread/history-cold-start";

    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await fixture.GetClientAsync($"hist-{name}-{Guid.NewGuid():N}", "Roland");

    [Fact]
    public async Task ColdStart_AgentSeesAllPreviousMessages()
    {
        // Swap to echo factory
        SharedOrleansFixture.SwappableFactory.SetInner(new EchoChatClientFactory());
        try
        {
            var ct = new CancellationTokenSource(30.Seconds()).Token;
            var client = await GetClientAsync();
            // Thread + 4 messages are pre-seeded in SharedOrleansFixture via AddMeshNodes.
            // This simulates a cold start: grains not yet activated, data in persistence.
            Output.WriteLine("Thread pre-seeded with 4 messages via AddMeshNodes");

            // Submit and wait for response
            // Post + collect TWO responses: CellsCreated then ExecutionCompleted
            Output.WriteLine("Posting SubmitMessageRequest...");
            var completionTcs = new TaskCompletionSource<SubmitMessageResponse>();
            var delivery = client.Post(new SubmitMessageRequest
            {
                ThreadPath = ThreadPath,
                UserMessageText = "Third question — can you see history?",
                ContextPath = "User/Roland"
            }, o => o.WithTarget(new Address(ThreadPath)));
            delivery.Should().NotBeNull("Post should return a delivery");

            var originalDelivery = (IMessageDelivery)delivery!;
            void RegisterForCompletion()
            {
                _ = client.RegisterCallback(originalDelivery, resp =>
                {
                    if (resp is IMessageDelivery<SubmitMessageResponse> smr)
                    {
                        var msg = smr.Message;
                        Output.WriteLine($"Response: status={msg.Status}, success={msg.Success}, text={msg.ResponseText?[..Math.Min(60, msg.ResponseText?.Length ?? 0)]}");
                        if (msg.Status == SubmitMessageStatus.ExecutionCompleted ||
                            msg.Status == SubmitMessageStatus.ExecutionFailed)
                            completionTcs.TrySetResult(msg);
                        else
                            RegisterForCompletion(); // Re-register for completion response
                    }
                    return resp;
                });
            }
            RegisterForCompletion();

            var completion = await completionTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            Output.WriteLine($"Execution completed: success={completion.Success}, text={completion.ResponseText}");

            completion.Success.Should().BeTrue("execution should succeed");
            // The agent MUST see 6 separate messages (4 pre-seeded history + 1 new input cell + 1 new user).
            // If this fails, the ChatClientAgent is flattening conversation turns into a single prompt.
            completion.ResponseText.Should().Contain("6 messages",
                "agent must receive 6 separate ChatMessage objects, not a flattened prompt");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }

    #region Echo LLM

    private class EchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("EchoProvider");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"I received {messages.Count()} messages.")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var msgList = messages.ToList();
            var summary = string.Join(" | ", msgList.Select((m, i) =>
                $"[{i}:{m.Role}:{(m.Text?.Length > 30 ? m.Text[..30] + "..." : m.Text)}]"));
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                $"I received {msgList.Count} messages. {summary}");
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
