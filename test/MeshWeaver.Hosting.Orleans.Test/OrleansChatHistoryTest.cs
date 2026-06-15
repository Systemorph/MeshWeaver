using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
/// Submits a 3rd message and verifies the agent sees ALL previous messages
/// via the live response cell (not from cache or local workspace).
///
/// 🚨 Test <c>await</c>s the reactive assertions: each terminal
/// <c>ObservableAssertions</c> method bridges the stream to a Task at the test
/// edge (the sanctioned <c>.FirstAsync()/.ToTask()</c> bridge) — no blocking
/// wait inside the test body. See ObservableAssertions remarks.
/// </summary>
public class OrleansChatHistoryTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private const string ThreadPath = "TestUser/_Thread/history-cold-start";

    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"hist-{name}-{Guid.NewGuid():N}", "TestUser");

    [Fact]
    public async Task ColdStart_AgentSeesAllPreviousMessages()
    {
        // Swap to echo factory
        SharedOrleansFixture.SwappableFactory.SetInner(new EchoChatClientFactory());
        try
        {
            var client = GetClient();
            var workspace = client.GetWorkspace();
            // Thread + 4 messages are pre-seeded in SharedOrleansFixture via the seed provider.
            // This simulates a cold start: grains not yet activated, data in persistence.
            Output.WriteLine("Thread pre-seeded with 4 messages");

            // Submit via the SAME entry point production uses (workspace.SubmitMessage
            // -> ThreadInput.AppendUserInput -> stream.Update on the thread node).
            Output.WriteLine("SubmitMessage (production entry point)...");

            // The thread is pre-seeded with 4 messages. Wait for Messages.Count >= 6
            // (4 seed + 1 user input cell + 1 agent response cell created by this
            // submission). The agent response is the LAST id appended.
            var messagesStream = workspace.GetMeshNodeStream(ThreadPath)
                .Select(node =>
                {
                    
                    return (node?.Content as MeshThread)?.Messages
                           ?? (IReadOnlyList<string>)ImmutableList<string>.Empty;
                });

            client.SubmitMessage(
                ThreadPath,
                "Third question - can you see history?",
                contextPath: "TestUser");
            Output.WriteLine("Append dispatched");

            // Resolve message ids — last id is the new agent response cell.
            var msgIds = await messagesStream.Should().Within(30.Seconds()).Match(ids => ids.Count >= 6);
            var responseMsgId = msgIds[^1];
            var responsePath = $"{ThreadPath}/{responseMsgId}";
            Output.WriteLine($"Response cell: {responseMsgId} (full list: [{string.Join(",", msgIds)}])");

            // Wait for the response cell text to fill in. The EchoChatClient's final
            // streaming text always contains "received" once execution is done.
            var responseMsg = await workspace.GetMeshNodeStream(responsePath)
                .Select(node =>
                {
                    if (node?.Content is ThreadMessage tm) return tm;
                    if (node?.Content is JsonElement cje) return cje.Deserialize<ThreadMessage>(client.JsonSerializerOptions);
                    return null;
                })
                .Should().Within(30.Seconds())
                .Match(m => m?.Text is { } text && text.Contains("received", StringComparison.OrdinalIgnoreCase));

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
