using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
/// End-to-end: create a parent thread, then a delegation sub-thread under a
/// response message, submit a message to the sub-thread via the canonical GUI
/// handler (<see cref="ThreadSubmission.Submit"/>), and verify the full
/// hierarchy is navigable. State is observed via
/// <c>client.GetWorkspace().GetMeshNodeStream(path)</c>.
/// </summary>
public class DelegationExecutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FakeResponse = "I found three relevant documents about the topic.";
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
        return base.ConfigureClient(configuration).AddLayoutClient();
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
    public async Task DelegationSubThread_SubmitMessage_ProducesNavigableHierarchy()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        var threadPath = await CreateThreadAsync(client, "Delegation execution test", ct);
        Output.WriteLine($"Parent thread: {threadPath}");

        var parentResponseMsgId = await ThreadFlow.SubmitAndWait(client, threadPath,
            "Research reinsurance pricing", contextPath: ContextPath).FirstAsync().ToTask(ct);
        Output.WriteLine($"Parent response: {parentResponseMsgId}");

        // Wait for parent response to have its final text before creating
        // the sub-thread underneath.
        await ThreadFlow.ReadMessage(client, threadPath, parentResponseMsgId,
            m => !string.IsNullOrEmpty(m.Text) && m.Status != ThreadMessageStatus.Streaming).FirstAsync().ToTask(ct);

        // Create the delegation sub-thread under the parent's response cell.
        var subThreadId = ThreadNodeType.GenerateSpeakingId("research reinsurance pricing");
        var parentMsgPath = $"{threadPath}/{parentResponseMsgId}";
        var subThreadPath = $"{parentMsgPath}/{subThreadId}";

        await NodeFactory.CreateNode(new MeshNode(subThreadId, parentMsgPath)
        {
            Name = "Research reinsurance pricing",
            NodeType = ThreadNodeType.NodeType,
            Content = new MeshThread()
        });
        Output.WriteLine($"Sub-thread created: {subThreadPath}");

        // Submit via GUI handler — server generates message ids on the sub-thread.
        var subResponseMsgId = await ThreadFlow.SubmitAndWait(client, subThreadPath,
            "Find documents about reinsurance pricing models", contextPath: ContextPath).FirstAsync().ToTask(ct);
        Output.WriteLine($"Sub-thread response: {subResponseMsgId}");

        // Verify full hierarchy is navigable via remote streams.
        var parentThread = await ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Count >= 2).FirstAsync().ToTask(ct);
        parentThread.Messages.Should().HaveCount(2);

        var subThreads = await MeshQuery
            .QueryAsync<MeshNode>($"namespace:{parentMsgPath} nodeType:{ThreadNodeType.NodeType}")
            .ToListAsync(ct);
        subThreads.Should().ContainSingle("should find exactly one sub-thread");
        subThreads[0].Path.Should().Be(subThreadPath);

        var subThread = await ThreadFlow.ReadThread(client, subThreadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 2).FirstAsync().ToTask(ct);
        subThread.Messages.Should().HaveCount(2);

        var subUserContent = await ThreadFlow.ReadMessage(client, subThreadPath,
            subThread.Messages[0], m => m.Role == "user").FirstAsync().ToTask(ct);
        subUserContent.Text.Should().Contain("reinsurance pricing");

        var subRespContent = await ThreadFlow.ReadMessage(client, subThreadPath,
            subResponseMsgId, m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Text)).FirstAsync().ToTask(ct);
        subRespContent.Text.Should().NotBeNullOrEmpty();

        Output.WriteLine($"Sub-thread response: '{subRespContent.Text}'");
        Output.WriteLine("Full hierarchy verified: parent → message → sub-thread → sub-messages");
    }

    #region Fake LLM

    private class FakeChatClient : IChatClient
    {
        private readonly string response;
        public FakeChatClient(string response) => this.response = response;
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
