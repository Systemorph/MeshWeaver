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
            var meshService = client.ServiceProvider.GetRequiredService<IMeshService>();

            // Thread + 4 messages are pre-seeded in SharedOrleansFixture via AddMeshNodes.
            // This simulates a cold start: grains not yet activated, data in persistence.
            Output.WriteLine("Thread pre-seeded with 4 messages via AddMeshNodes");

            // Submit and wait for response
            Output.WriteLine("Posting SubmitMessageRequest...");
            var submitResponse = await client.AwaitResponse(new SubmitMessageRequest
            {
                ThreadPath = ThreadPath,
                UserMessageText = "Third question — can you see history?",
                ContextPath = "User/Roland"
            }, o => o.WithTarget(new Address(ThreadPath)), ct);
            Output.WriteLine($"Submit response: success={submitResponse.Message.Success}, error={submitResponse.Message.Error}");

            // Wait for execution to complete
            for (var i = 0; i < 60; i++)
            {
                var node = await meshService.QueryAsync<MeshNode>($"path:{ThreadPath}").FirstOrDefaultAsync(ct);
                var thread = node?.Content as MeshThread;
                if (thread is { IsExecuting: false } && thread.Messages.Count >= 6)
                {
                    var lastMsgId = thread.Messages[^1];
                    var msgNode = await meshService.QueryAsync<MeshNode>($"path:{ThreadPath}/{lastMsgId}").FirstOrDefaultAsync(ct);
                    var responseText = (msgNode?.Content as ThreadMessage)?.Text;
                    if (!string.IsNullOrEmpty(responseText))
                    {
                        Output.WriteLine($"Agent response: {responseText}");
                        // The echo agent reports how many messages it received
                        responseText.Should().Contain("5 messages",
                            "agent MUST see 4 history messages (2 user + 2 assistant) + 1 new user = 5 total. " +
                            "If this fails, GetDataRequest didn't return the history messages in Orleans.");
                        return; // SUCCESS
                    }
                }
                await Task.Delay(200, ct);
            }
            throw new TimeoutException("Execution did not complete");
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
