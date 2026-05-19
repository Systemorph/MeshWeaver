using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
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
/// Verifies the persisted state of a thread is reachable from a fresh client
/// (the "navigate back to the URL" case). Submission uses
/// <see cref="ThreadSubmission.Submit"/>; reads use
/// <c>client.GetWorkspace().GetMeshNodeStream(path)</c>.
/// </summary>
public class ThreadResumeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FakeResponse = "This is the agent's response to verify resume.";
    private const string ContextPath = "User/TestUser";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddData();
    }

    private async Task<string> CreateThreadAsync(IMessageHub client, string text, CancellationToken ct)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "TestUser");
        var response = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.Node!.Path!;
    }

    [Fact]
    public async Task Resume_ThreadWithMessages_LoadsAllMessages()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        var threadPath = await CreateThreadAsync(client, "Resume test thread", ct);
        Output.WriteLine($"Thread: {threadPath}");

        var responseMsgId = await ChatFlow.SubmitAndWaitAsync(client, threadPath,
            "First message for resume test", contextPath: ContextPath, ct: ct);

        var thread = await ChatFlow.ReadThreadAsync(client, threadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 2, ct: ct);
        thread.Messages.Should().HaveCount(2);
        Output.WriteLine($"Persisted messages: [{string.Join(", ", thread.Messages)}]");

        // Wait for response text to be present.
        await ChatFlow.ReadMessageAsync(client, threadPath, responseMsgId,
            m => !string.IsNullOrEmpty(m.Text), ct: ct);

        // Simulate resume: a fresh client subscribes to the same thread.
        var client2 = GetClient();
        var resumed = await ChatFlow.ReadThreadAsync(client2, threadPath,
            t => t.Messages.Count >= 2, ct: ct);
        resumed.Messages.Should().HaveCount(2, "resumed thread should have all messages");
        resumed.Messages[0].Should().Be(thread.Messages[0]);
        resumed.Messages[1].Should().Be(thread.Messages[1]);
        Output.WriteLine($"Resumed messages: [{string.Join(", ", resumed.Messages)}]");

        foreach (var msgId in resumed.Messages)
        {
            var msg = await ChatFlow.ReadMessageAsync(client2, threadPath, msgId,
                m => !string.IsNullOrEmpty(m.Text), ct: ct);
            Output.WriteLine($"  {msgId}: role={msg.Role}, text='{msg.Text[..Math.Min(50, msg.Text.Length)]}'");
        }

        Output.WriteLine("Resume verified: all messages loaded correctly");
    }

    [Fact]
    public async Task Resume_ThreadWithMultipleExchanges_LoadsAll()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        var threadPath = await CreateThreadAsync(client, "Multi-exchange resume test", ct);

        await ChatFlow.SubmitAndWaitAsync(client, threadPath, "First question",
            contextPath: ContextPath, ct: ct);
        var threadAfterFirst = await ChatFlow.ReadThreadAsync(client, threadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 2, ct: ct);
        Output.WriteLine($"After first exchange: [{string.Join(", ", threadAfterFirst.Messages)}]");

        await ChatFlow.SubmitAndWaitAsync(client, threadPath, "Second question",
            contextPath: ContextPath, ct: ct);
        var allThread = await ChatFlow.ReadThreadAsync(client, threadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 4, ct: ct);
        allThread.Messages.Should().HaveCount(4, "should have 2 exchanges = 4 messages");

        // Wait for the last response to have text.
        await ChatFlow.ReadMessageAsync(client, threadPath, allThread.Messages[3],
            m => !string.IsNullOrEmpty(m.Text), ct: ct);

        var client2 = GetClient();
        var resumed = await ChatFlow.ReadThreadAsync(client2, threadPath,
            t => t.Messages.Count >= 4, ct: ct);
        resumed.Messages.Should().HaveCount(4);
        resumed.Messages.Should().BeEquivalentTo(allThread.Messages, o => o.WithStrictOrdering());

        Output.WriteLine($"Resumed {resumed.Messages.Count} messages across 2 exchanges");
    }

    #region Fake LLM

    private class FakeChatClient(string response) : IChatClient
    {
        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in response.Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Delay(10, cancellationToken);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class FakeChatClientFactory : IChatClientFactory
    {
        public string Name => "FakeFactory";
        public IReadOnlyList<string> Models => ["fake-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new FakeChatClient(FakeResponse),
                instructions: config.Instructions ?? "You are a test assistant.",
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
