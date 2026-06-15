using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Verifies ALL previous messages are passed to the agent on each execution.
/// Submission goes through the GUI handler (<see cref="ThreadSubmission.Submit"/>);
/// every read goes through <c>client.GetWorkspace().GetMeshNodeStream(path)</c>.
/// </summary>
public class ChatHistoryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
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

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddData();
    }

    private async Task<string> CreateThread(IMessageHub client, string text)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "TestUser");
        var response = await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(Mesh.Address)).Should().Within(60.Seconds()).Emit();
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        return response.Message.Node!.Path!;
    }

    private async Task<string> SubmitAndWait(IMessageHub client, string threadPath, string text)
    {
        var responseMsgId = await ThreadFlow.SubmitAndWait(client, threadPath, text,
            contextPath: ContextPath, timeout: 60.Seconds()).Should().Within(60.Seconds()).Emit();

        // CompletedAt is the deterministic "streaming finished" signal â€” only set
        // by the terminal PushToResponseMessage call in ExecuteMessageAsync. Beats
        // text-pattern matching against in-flight placeholders.
        var finalMessage = await ThreadFlow.ReadMessage(client, threadPath, responseMsgId,
            m => m.CompletedAt != null && !string.IsNullOrEmpty(m.Text),
            timeout: 60.Seconds()).Should().Within(60.Seconds()).Emit();

        return finalMessage.Text!;
    }

    [Fact]
    public async Task ThreeMessages_AgentSeesFullHistory()
    {
        var client = GetClient();
        var threadPath = await CreateThread(client, "History test");

        var response1 = await SubmitAndWait(client, threadPath, "First message");
        Output.WriteLine($"Response 1: {response1}");
        response1.Should().Contain("2 messages", "first message: system + user");

        var response2 = await SubmitAndWait(client, threadPath, "Second message");
        Output.WriteLine($"Response 2: {response2}");
        response2.Should().Contain("4 messages", "second message: system + 2 history + 1 new");

        var response3 = await SubmitAndWait(client, threadPath, "Third message");
        Output.WriteLine($"Response 3: {response3}");
        response3.Should().Contain("6 messages", "third message: system + 4 history + 1 new");
    }

    [Fact]
    public async Task TwoMessages_NoDuplicates_CorrectRoles()
    {
        var client = GetClient();
        var threadPath = await CreateThread(client, "Duplicate check");

        var response1 = await SubmitAndWait(client, threadPath, "Hello");
        Output.WriteLine($"Response 1: {response1}");
        response1.Should().Contain("2 messages", "first call: system prompt + user message");

        var response2 = await SubmitAndWait(client, threadPath, "World");
        Output.WriteLine($"Response 2: {response2}");
        response2.Should().Contain("4 messages",
            "second call: system + 2 history (user+assistant) + 1 new user = 4 total");
    }

    #region Echo LLM â€” responds with message count to verify history is passed

    private class EchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("EchoProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken ct = default)
        {
            var count = messages.Count();
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, $"I received {count} messages in this conversation.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var msgList = messages.ToList();
            var summary = string.Join(" | ", msgList.Select((m, i) =>
                $"[{i}:{m.Role}:{(m.Text?.Length > 30 ? m.Text[..30] + "..." : m.Text)}]"));
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                $"I received {msgList.Count} messages in this conversation. Messages: {summary}");
            await Task.Delay(10, ct);
        }

        public object? GetService(Type serviceType, object? key = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class EchoChatClientFactory : IChatClientFactory
    {
        public string Name => "EchoFactory";
        public IReadOnlyList<string> Models => ["echo-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new EchoChatClient(), instructions: "Echo agent.",
                name: config.Id, description: config.Description ?? "",
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
