using System;
using System.Collections.Generic;
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
using MeshWeaver.AI.Persistence;
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
/// Tests the full SubmitMessageRequest handler flow using workspace remote streams.
/// No MeshQuery, no Task.Delay — reactive streams with .Where().FirstAsync().
/// </summary>
public class ExecuteThreadMessageTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FakeResponseText = "This is a test response from the fake agent with enough words to verify streaming works correctly.";
    private const string ContextPath = "User/Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddMemoryChatPersistence();
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .AddAI()
            .AddSampleUsers();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(SubmitMessageRequest), typeof(SubmitMessageResponse))
            .WithTypes(typeof(CreateThreadRequest), typeof(CreateThreadResponse));

    private async Task<string> CreateThreadAsync(IMessageHub client, string text, CancellationToken ct)
    {
        var response = await client.AwaitResponse(
            new CreateThreadRequest { Namespace = ContextPath, UserMessageText = text },
            o => o.WithTarget(new Address(ContextPath)), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.ThreadPath!;
    }

    /// <summary>
    /// Observes Thread.ThreadMessages via the client's remote stream to the thread hub.
    /// Uses Workspace.GetRemoteStream which subscribes to the thread hub's MeshNode collection.
    /// </summary>
    private IObservable<IReadOnlyList<string>> ObserveThreadMessages(IMessageHub client, string threadPath)
    {
        var workspace = client.GetWorkspace();
        return workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                var content = node?.Content as MeshThread;
                var ids = content?.ThreadMessages ?? [];
                Output.WriteLine($"ThreadMessages stream: {ids.Count} IDs");
                return (IReadOnlyList<string>)ids;
            });
    }

    [Fact]
    public async Task SubmitMessage_CreatesUserAndResponseNodes()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread
        var threadPath = await CreateThreadAsync(client, "Hello", ct);
        Output.WriteLine($"Thread at: {threadPath}");

        // 2. Subscribe to remote stream BEFORE submitting
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // 3. Submit message
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest { ThreadPath = threadPath, UserMessageText = "Hello agent" },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        // 4. Wait for 2 message IDs via reactive stream
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2, "should have user message ID + response message ID");
        Output.WriteLine($"ThreadMessages: [{string.Join(", ", msgIds)}]");
    }

    [Fact]
    public async Task SubmitMessage_SecondMessage_AccumulatesThreadMessages()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread
        var threadPath = await CreateThreadAsync(client, "Multi-message", ct);

        // 2. Subscribe BEFORE submitting — watches for 4 message IDs
        var fourMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 4)
            .FirstAsync()
            .ToTask(ct);

        // 3. First message
        var r1 = await client.AwaitResponse(
            new SubmitMessageRequest { ThreadPath = threadPath, UserMessageText = "First" },
            o => o.WithTarget(new Address(threadPath)), ct);
        r1.Message.Success.Should().BeTrue(r1.Message.Error);
        Output.WriteLine("First message submitted");

        // 4. Second message
        var r2 = await client.AwaitResponse(
            new SubmitMessageRequest { ThreadPath = threadPath, UserMessageText = "Second" },
            o => o.WithTarget(new Address(threadPath)), ct);
        r2.Message.Success.Should().BeTrue(r2.Message.Error);
        Output.WriteLine("Second message submitted");

        // 5. Wait for 4 message IDs — reactive, no polling
        var msgIds = await fourMessages;
        msgIds.Should().HaveCount(4, "should have 4 message IDs (2 per submission)");
        Output.WriteLine($"ThreadMessages: [{string.Join(", ", msgIds)}]");
    }

    #region Fake Chat Client Infrastructure

    private class FakeChatClient : IChatClient
    {
        private readonly string response;

        public FakeChatClient(string response) => this.response = response;

        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
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

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
        {
            var agent = new ChatClientAgent(
                chatClient: new FakeChatClient(FakeResponseText),
                instructions: config.Instructions ?? "You are a helpful test assistant.",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [],
                loggerFactory: null,
                services: null
            );
            return Task.FromResult(agent);
        }
    }

    #endregion
}
