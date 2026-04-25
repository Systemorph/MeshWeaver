using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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

            // Submit via AppendUserMessageRequest, then wait for the response cell to settle.
            // The new API returns Success/Error only — the agent's response text lives on the
            // response satellite cell. Read it via the workspace stream once execution completes.
            Output.WriteLine("Posting AppendUserMessageRequest...");
            var workspace = client.GetWorkspace();

            // Subscribe BEFORE submitting to capture the moment Messages.Count >= 2.
            var twoMessages = workspace.GetRemoteStream<MeshNode>(new Address(ThreadPath))!
                .Select(nodes =>
                {
                    var node = nodes?.FirstOrDefault(n => n.Path == ThreadPath);
                    return (node?.Content as MeshThread)?.Messages ?? [];
                })
                .Where(ids => ids.Count >= 2)
                .Timeout(20.Seconds())
                .FirstAsync()
                .ToTask(ct);

            var submitResp = await client.AwaitResponse(new AppendUserMessageRequest
            {
                ThreadPath = ThreadPath,
                UserMessageId = Guid.NewGuid().ToString("N")[..8],
                UserText = "Third question — can you see history?",
                ContextPath = "User/Roland"
            }, o => o.WithTarget(new Address(ThreadPath)), ct);
            submitResp.Message.Success.Should().BeTrue(submitResp.Message.Error);
            Output.WriteLine($"Append accepted: success={submitResp.Message.Success}");

            // Resolve message ids (user + response).
            var msgIds = await twoMessages;
            var responseMsgId = msgIds[1];
            var responsePath = $"{ThreadPath}/{responseMsgId}";
            Output.WriteLine($"Response cell: {responseMsgId}");

            // Wait for the response cell text to fill in (= execution finished streaming).
            ThreadMessage? responseMsg = null;
            for (var i = 0; i < 100; i++)
            {
                var resp = await client.AwaitResponse(
                    new GetDataRequest(new MeshNodeReference()),
                    o => o.WithTarget(new Address(responsePath)), ct);
                var node = resp.Message.Data as MeshNode;
                if (node == null && resp.Message.Data is JsonElement je)
                    node = je.Deserialize<MeshNode>(client.JsonSerializerOptions);
                if (node?.Content is ThreadMessage tm) responseMsg = tm;
                else if (node?.Content is JsonElement cje)
                    responseMsg = cje.Deserialize<ThreadMessage>(client.JsonSerializerOptions);

                if (!string.IsNullOrEmpty(responseMsg?.Text)) break;
                await Task.Delay(200, ct);
            }

            Output.WriteLine($"Execution completed: text={responseMsg?.Text}");
            responseMsg.Should().NotBeNull("response cell must be populated");
            // The agent MUST see 6 separate messages (4 pre-seeded history + 1 new input cell + 1 new user).
            // If this fails, the ChatClientAgent is flattening conversation turns into a single prompt.
            responseMsg!.Text.Should().Contain("6 messages",
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
