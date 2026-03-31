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
using MeshWeaver.AI.Persistence;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
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
/// Tests that thread execution works end-to-end without routing errors.
/// Verifies nodes are created in persistence and no DeliveryFailure messages are raised.
/// </summary>
public class ThreadExecutionPersistenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FakeResponseText = "This is a test response from the fake agent to verify persistence end-to-end.";
    private const string ContextPath = "TestOrg";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
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

    private async Task<string> CreateContextNodeAsync(string path, CancellationToken ct)
    {
        await NodeFactory.CreateNodeAsync(
            new MeshNode(path) { Name = path, NodeType = "Markdown" }, ct);
        return path;
    }

    private async Task<string> CreateThreadAsync(IMessageHub client, string ns, string text, CancellationToken ct)
    {
        var response = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ns, text)),
            o => o.WithTarget(new Address(ns)), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.Node!.Path!;
    }

    private IObservable<IReadOnlyList<string>> ObserveMessages(IMessageHub client, string threadPath)
    {
        var workspace = client.GetWorkspace();
        return workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                var content = node?.Content as MeshThread;
                var ids = content?.Messages ?? [];
                Output.WriteLine($"Messages stream: {ids.Count} IDs = [{string.Join(", ", ids)}]");
                return (IReadOnlyList<string>)ids;
            });
    }

    /// <summary>
    /// Verifies the full execution flow:
    /// 1. Create context → create thread → submit message
    /// 2. Thread and message nodes are created in persistence
    /// 3. Messages IDs appear in the workspace stream
    /// </summary>
    [Fact]
    public async Task ExecuteThread_PersistsToCorrectPartition()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var client = GetClient();

        await CreateContextNodeAsync(ContextPath, ct);
        var threadPath = await CreateThreadAsync(client, ContextPath, "Persistence test message", ct);
        Output.WriteLine($"Thread created at: {threadPath}");

        threadPath.Should().StartWith($"{ContextPath}/");
        threadPath.Should().Contain($"/{ThreadNodeType.ThreadPartition}/");

        // Submit message and wait for IDs
        var twoMessages = ObserveMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        client.Post(new SubmitMessageRequest
        {
            ThreadPath = threadPath,
            UserMessageText = "Hello from persistence test",
            ContextPath = ContextPath
        }, o => o.WithTarget(new Address(threadPath)));

        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"Messages: [{string.Join(", ", msgIds)}]");

        // Verify nodes exist in persistence (created by meshService.CreateNodeAsync)
        var userMsgPath = $"{threadPath}/{msgIds[0]}";
        var userNode = await MeshQuery.QueryAsync<MeshNode>($"path:{userMsgPath}")
            .FirstOrDefaultAsync(ct);
        userNode.Should().NotBeNull("user message node must exist in persistence");
        var userContent = userNode!.Content.Should().BeOfType<ThreadMessage>().Subject;
        userContent.Role.Should().Be("user");
        userContent.Text.Should().Be("Hello from persistence test");
        Output.WriteLine($"User message OK: '{userContent.Text}'");

        var responseMsgPath = $"{threadPath}/{msgIds[1]}";
        var responseNode = await MeshQuery.QueryAsync<MeshNode>($"path:{responseMsgPath}")
            .FirstOrDefaultAsync(ct);
        responseNode.Should().NotBeNull("response message node must exist in persistence");
        responseNode!.NodeType.Should().Be(ThreadMessageNodeType.NodeType);
        Output.WriteLine($"Response message node exists in persistence: {responseNode.Path}");

        // Verify partition
        threadPath.Split('/')[0].Should().Be(ContextPath);
    }

    /// <summary>
    /// Verifies that ThreadMessage children are queryable by namespace.
    /// </summary>
    [Fact]
    public async Task ExecuteThread_ChildMessagesQueryableByNamespace()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var client = GetClient();

        await CreateContextNodeAsync(ContextPath, ct);
        var threadPath = await CreateThreadAsync(client, ContextPath, "Child query test", ct);

        var twoMessages = ObserveMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        client.Post(new SubmitMessageRequest
        {
            ThreadPath = threadPath,
            UserMessageText = "Test query by namespace",
            ContextPath = ContextPath
        }, o => o.WithTarget(new Address(threadPath)));

        await twoMessages;

        var children = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}")
            .ToListAsync(ct);

        children.Should().HaveCountGreaterThanOrEqualTo(2);

        Output.WriteLine($"Found {children.Count} ThreadMessage children:");
        foreach (var child in children)
        {
            var msg = child.Content as ThreadMessage;
            Output.WriteLine($"  {child.Path}: role={msg?.Role}");
        }

        var hasUser = children.Any(n => n.Content is ThreadMessage tm && tm.Role == "user");
        var hasAssistant = children.Any(n => n.Content is ThreadMessage tm && tm.Role == "assistant");
        hasUser.Should().BeTrue("should have a user message");
        hasAssistant.Should().BeTrue("should have an assistant response");
    }

    /// <summary>
    /// Verifies SubmitMessageRequest completes without timeout (no deadlock).
    /// Uses AwaitResponse to ensure the thread hub responds.
    /// </summary>
    [Fact]
    public async Task ExecuteThread_SubmitMessageDoesNotTimeout()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var client = GetClient();

        await CreateContextNodeAsync(ContextPath, ct);
        var threadPath = await CreateThreadAsync(client, ContextPath, "Timeout test", ct);

        // Use AwaitResponse — if routing is broken, this will timeout
        var response = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Test no timeout",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);

        response.Message.Success.Should().BeTrue("SubmitMessageResponse should arrive without timeout");
        Output.WriteLine("SubmitMessageRequest completed without timeout");
    }

    /// <summary>
    /// Verifies Thread.Messages is persisted (debounced save completes).
    /// </summary>
    [Fact]
    public async Task ExecuteThread_ThreadContentPersisted()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var client = GetClient();

        await CreateContextNodeAsync(ContextPath, ct);
        var threadPath = await CreateThreadAsync(client, ContextPath, "Persistence test", ct);

        var twoMessages = ObserveMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        client.Post(new SubmitMessageRequest
        {
            ThreadPath = threadPath,
            UserMessageText = "Check persistence",
            ContextPath = ContextPath
        }, o => o.WithTarget(new Address(threadPath)));

        var msgIds = await twoMessages;

        // Poll persistence until Messages is flushed (debounce = 200ms + save)
        MeshThread? content = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var threadNode = await MeshQuery.QueryAsync<MeshNode>($"path:{threadPath}")
                .FirstOrDefaultAsync(ct);
            content = threadNode?.Content as MeshThread;
            if (content?.Messages?.Count >= 2)
                break;
            await Task.Delay(300, ct);
        }

        content.Should().NotBeNull("thread content must be persisted");
        content!.Messages.Should().HaveCountGreaterThanOrEqualTo(2,
            "Messages must be persisted with message IDs");
        Output.WriteLine($"Thread persisted: [{string.Join(", ", content.Messages)}]");
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
        {
            var agent = new ChatClientAgent(
                chatClient: new FakeChatClient(FakeResponseText),
                instructions: config.Instructions ?? "You are a helpful test assistant.",
                name: config.Id, description: config.Description ?? config.Id,
                tools: [], loggerFactory: null, services: null);
            return agent;
        }

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
