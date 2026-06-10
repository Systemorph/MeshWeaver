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
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// End-to-end execution + persistence: submission via <see cref="ThreadSubmission.Submit"/>,
/// state observed via <c>client.GetWorkspace().GetMeshNodeStream(path)</c>.
/// </summary>
public class ThreadExecutionPersistenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FakeResponseText = "This is a test response from the fake agent to verify persistence end-to-end.";
    private const string ContextPath = "TestOrg";

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

    private string CreateContextNode(string path)
    {
        // Top-level partition root → seed under System (only the partition provisioner may create there).
        SeedTopLevel(new MeshNode(path) { Name = path, NodeType = "Markdown" });
        return path;
    }

    private string CreateThread(IMessageHub client, string ns, string text)
    {
        var response = client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ns, text)),
            o => o.WithTarget(new Address(ns))).Should().Within(30.Seconds()).Emit();
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        return response.Message.Node!.Path!;
    }

    /// <summary>
    /// Verifies that the user + response cells are created in persistence by the
    /// server-side dispatcher after a <see cref="ThreadSubmission.Submit"/> call.
    /// </summary>
    [Fact]
    public void ExecuteThread_PersistsToCorrectPartition()
    {
        var client = GetClient();

        CreateContextNode(ContextPath);
        var threadPath = CreateThread(client, ContextPath, "Persistence test message");
        Output.WriteLine($"Thread created at: {threadPath}");

        threadPath.Should().StartWith($"{ContextPath}/");
        threadPath.Should().Contain($"/{ThreadNodeType.ThreadPartition}/");

        var responseMsgId = ThreadFlow.SubmitAndWait(client, threadPath,
            "Hello from persistence test", contextPath: ContextPath).Should().Within(20.Seconds()).Emit();

        var thread = ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Count >= 2).Should().Within(20.Seconds()).Emit();
        thread.Messages.Should().HaveCount(2);
        Output.WriteLine($"Messages: [{string.Join(", ", thread.Messages)}]");

        var userContent = ThreadFlow.ReadMessage(client, threadPath, thread.Messages[0],
            m => m.Role == "user").Should().Within(20.Seconds()).Emit();
        userContent.Text.Should().Be("Hello from persistence test");
        Output.WriteLine($"User message OK: '{userContent.Text}'");

        var responseContent = ThreadFlow.ReadMessage(client, threadPath, responseMsgId,
            m => m.Role == "assistant").Should().Within(20.Seconds()).Emit();
        responseContent.Type.Should().Be(ThreadMessageType.AgentResponse);
        Output.WriteLine($"Response message node exists at: {threadPath}/{responseMsgId}");

        threadPath.Split('/')[0].Should().Be(ContextPath);
    }

    [Fact]
    public void ExecuteThread_ChildMessagesQueryableByNamespace()
    {
        var client = GetClient();

        CreateContextNode(ContextPath);
        var threadPath = CreateThread(client, ContextPath, "Child query test");

        ThreadFlow.SubmitAndWait(client, threadPath, "Test query by namespace",
            contextPath: ContextPath).Should().Within(20.Seconds()).Emit();

        ThreadFlow.ReadThread(client, threadPath, t => t.Messages.Count >= 2).Should().Within(20.Seconds()).Emit();

        var children = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}"))
            .Should().Match(c => c.Items.Count >= 2).Items;

        children.Should().HaveCountGreaterThanOrEqualTo(2);
        Output.WriteLine($"Found {children.Count} ThreadMessage children:");
        foreach (var child in children)
        {
            var msg = child.Content as ThreadMessage;
            Output.WriteLine($"  {child.Path}: role={msg?.Role}");
        }

        children.Any(n => n.Content is ThreadMessage tm && tm.Role == "user")
            .Should().BeTrue("should have a user message");
        children.Any(n => n.Content is ThreadMessage tm && tm.Role == "assistant")
            .Should().BeTrue("should have an assistant response");
    }

    [Fact]
    public void ExecuteThread_SubmitMessageDoesNotTimeout()
    {
        var client = GetClient();

        CreateContextNode(ContextPath);
        var threadPath = CreateThread(client, ContextPath, "Timeout test");

        var responseMsgId = ThreadFlow.SubmitAndWait(client, threadPath,
            "Test no timeout", contextPath: ContextPath, timeout: 15.Seconds()).Should().Within(15.Seconds()).Emit();

        responseMsgId.Should().NotBeNullOrEmpty("SubmitMessageResponse should arrive without timeout");
        Output.WriteLine("SubmitMessageRequest completed without timeout");
    }

    [Fact]
    public void ExecuteThread_ThreadContentPersisted()
    {
        var client = GetClient();

        CreateContextNode(ContextPath);
        var threadPath = CreateThread(client, ContextPath, "Persistence test");

        ThreadFlow.SubmitAndWait(client, threadPath, "Check persistence",
            contextPath: ContextPath).Should().Within(20.Seconds()).Emit();

        var thread = ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Count >= 2).Should().Within(20.Seconds()).Emit();
        thread.Messages.Should().HaveCountGreaterThanOrEqualTo(2,
            "Messages must be persisted with message IDs");
        Output.WriteLine($"Thread persisted: [{string.Join(", ", thread.Messages)}]");
    }

    #region Fake Chat Client Infrastructure

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
            => new(chatClient: new FakeChatClient(FakeResponseText),
                instructions: config.Instructions ?? "You are a helpful test assistant.",
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
