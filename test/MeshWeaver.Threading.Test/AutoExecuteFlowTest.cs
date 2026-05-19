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
/// Portal flow, end-to-end, via the canonical GUI handlers:
/// <list type="number">
///   <item><see cref="ThreadSubmission.CreateThreadAndSubmit"/> creates the thread
///     with the first user message pre-seeded; the server watcher dispatches.</item>
///   <item>State is observed via <c>client.GetWorkspace().GetMeshNodeStream(path)</c>
///     — same reactive handle the Blazor view holds.</item>
/// </list>
/// </summary>
public class AutoExecuteFlowTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
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

    [Fact]
    public async Task PortalFlow_CreateThread_CreateCells_Submit_ResponseWritten()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "Hello portal flow!", "TestUser");
        var threadPath = threadNode.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        var createResponse = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(ct);
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error);
        Output.WriteLine("Thread created");

        var responseMsgId = await ChatFlow.SubmitAndWaitAsync(client, threadPath,
            "Hello portal flow!", contextPath: ContextPath, agentName: "Orchestrator",
            timeout: 30.Seconds(), ct: ct);
        Output.WriteLine($"Response msg id: {responseMsgId}");

        var response = await ChatFlow.ReadMessageAsync(client, threadPath, responseMsgId,
            m => !string.IsNullOrEmpty(m.Text) && m.Status != ThreadMessageStatus.Streaming,
            ct: ct);
        response.Text.Should().NotBeNullOrEmpty("agent should have written response");
        Output.WriteLine($"Response: {response.Text[..Math.Min(80, response.Text.Length)]}");
    }

    [Fact]
    public async Task PortalFlow_ResponseCell_GetsUpdatedByExecution()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "Test response update", "TestUser");
        var threadPath = threadNode.Path!;
        await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(ct);

        var responseMsgId = await ChatFlow.SubmitAndWaitAsync(client, threadPath,
            "Test response update", contextPath: ContextPath, timeout: 30.Seconds(), ct: ct);

        var response = await ChatFlow.ReadMessageAsync(client, threadPath, responseMsgId,
            m => !string.IsNullOrEmpty(m.Text)
                 && !m.Text.StartsWith("Allocating")
                 && !m.Text.StartsWith("Loading")
                 && !m.Text.StartsWith("Generating"),
            ct: ct);
        Output.WriteLine($"Response cell updated: {response.Text[..Math.Min(80, response.Text.Length)]}");
    }

    #region Echo LLM

    private class EchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("EchoProvider");
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
