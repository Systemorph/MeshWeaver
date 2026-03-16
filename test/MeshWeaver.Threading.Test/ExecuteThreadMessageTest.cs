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
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

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

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    private async Task<string> CreateThreadAsync(IMessageHub client, string text, CancellationToken ct)
    {
        var response = await client.AwaitResponse(
            new CreateThreadRequest { Namespace = ContextPath, UserMessageText = text },
            o => o.WithTarget(new Address(ContextPath)), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.ThreadPath!;
    }

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

    /// <summary>
    /// Sends GetDataRequest with EntityReference to a hub to get the MeshNode,
    /// then extracts and deserializes the Content as T.
    /// </summary>
    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        var response = await client.AwaitResponse(
            new GetDataRequest(new EntityReference(nameof(MeshNode), path)),
            o => o.WithTarget(new Address(path)), ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(Mesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(Mesh.JsonSerializerOptions);
        return null;
    }

    [Fact]
    public async Task SubmitMessage_CreatesUserAndResponseNodes()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread and submit
        var threadPath = await CreateThreadAsync(client, "Hello", ct);
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest { ThreadPath = threadPath, UserMessageText = "Hello agent" },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        // 2. Wait for message IDs to appear
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"ThreadMessages: [{string.Join(", ", msgIds)}]");

        // 3. Verify thread content via GetDataRequest
        var threadContent = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        threadContent.Should().NotBeNull("thread hub should return Thread content");
        threadContent!.ThreadMessages.Should().HaveCount(2, "thread should have 2 message IDs");

        // 4. Verify user message content via GetDataRequest
        var userContent = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{msgIds[0]}", ct);
        userContent.Should().NotBeNull("user message hub should return ThreadMessage content");
        userContent!.Role.Should().Be("user");
        userContent.Text.Should().Be("Hello agent");
        userContent.Type.Should().Be(ThreadMessageType.ExecutedInput);

        // 5. Verify response message content via GetDataRequest
        var responseContent = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{msgIds[1]}", ct);
        responseContent.Should().NotBeNull("response message hub should return ThreadMessage content");
        responseContent!.Role.Should().Be("assistant");
        responseContent.Type.Should().Be(ThreadMessageType.AgentResponse);
    }

    [Fact]
    public async Task SubmitMessage_SecondMessage_AccumulatesThreadMessages()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread
        var threadPath = await CreateThreadAsync(client, "Multi-message", ct);
        var messagesStream = ObserveThreadMessages(client, threadPath);

        // 2. First message
        var twoMessages = messagesStream.Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);
        var r1 = await client.AwaitResponse(
            new SubmitMessageRequest { ThreadPath = threadPath, UserMessageText = "First" },
            o => o.WithTarget(new Address(threadPath)), ct);
        r1.Message.Success.Should().BeTrue(r1.Message.Error);
        var firstIds = await twoMessages;
        Output.WriteLine($"First batch: [{string.Join(", ", firstIds)}]");

        // 3. Second message
        var fourMessages = messagesStream.Where(ids => ids.Count >= 4).FirstAsync().ToTask(ct);
        var r2 = await client.AwaitResponse(
            new SubmitMessageRequest { ThreadPath = threadPath, UserMessageText = "Second" },
            o => o.WithTarget(new Address(threadPath)), ct);
        r2.Message.Success.Should().BeTrue(r2.Message.Error);
        var allIds = await fourMessages;
        Output.WriteLine($"All IDs: [{string.Join(", ", allIds)}]");

        // 4. Verify thread content via GetDataRequest — has 4 message IDs
        var threadContent = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        threadContent.Should().NotBeNull();
        threadContent!.ThreadMessages.Should().HaveCount(4, "thread should have 4 message IDs after 2 submits");

        // 5. Verify each message node content via GetDataRequest
        for (var i = 0; i < allIds.Count; i++)
        {
            var msgContent = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{allIds[i]}", ct);
            msgContent.Should().NotBeNull("message {0} should have ThreadMessage content", i);
            Output.WriteLine($"  [{i}] {msgContent!.Role}: '{msgContent.Text}'");
        }
    }

    [Fact]
    public async Task SubmitMessage_BothNodesGetCorrectContentViaGetDataRequest()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread and submit
        var threadPath = await CreateThreadAsync(client, "Content verification", ct);
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        var response = await client.AwaitResponse(
            new SubmitMessageRequest { ThreadPath = threadPath, UserMessageText = "Tell me something" },
            o => o.WithTarget(new Address(threadPath)), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);

        var msgIds = await twoMessages;
        Output.WriteLine($"Message IDs: [{string.Join(", ", msgIds)}]");

        // 2. Verify USER message node via GetDataRequest — should be populated immediately
        var userContent = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{msgIds[0]}", ct);
        userContent.Should().NotBeNull("user message hub should return ThreadMessage content");
        userContent!.Role.Should().Be("user");
        userContent.Text.Should().Be("Tell me something");
        userContent.Type.Should().Be(ThreadMessageType.ExecutedInput);
        Output.WriteLine($"User node: role={userContent.Role}, text='{userContent.Text}'");

        // 3. Verify RESPONSE message node via GetDataRequest — poll until streaming completes.
        // Wait for text to stabilize (same length on two consecutive polls = streaming done).
        ThreadMessage? responseContent = null;
        var previousLength = 0;
        var stableCount = 0;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            responseContent = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{msgIds[1]}", ct);
            var currentLength = responseContent?.Text?.Length ?? 0;
            if (currentLength > 0 && currentLength == previousLength)
            {
                if (++stableCount >= 2)
                    break; // Text hasn't changed for 2 polls — streaming is done
            }
            else
            {
                stableCount = 0;
            }
            previousLength = currentLength;
            await Task.Delay(200, ct);
        }

        responseContent.Should().NotBeNull("response message hub should return ThreadMessage content");
        responseContent!.Role.Should().Be("assistant");
        responseContent.Type.Should().Be(ThreadMessageType.AgentResponse);
        responseContent.Text.Should().NotBeNullOrEmpty("agent should have streamed a non-empty response");
        responseContent.Text.Length.Should().BeGreaterThan(10, "response should have meaningful content");
        Output.WriteLine($"Response node: role={responseContent.Role}, text='{responseContent.Text}' ({responseContent.Text.Length} chars)");
    }

    [Fact]
    public async Task SubmitMessage_DoesNotDeadlock_ResponseWithin5Seconds()
    {
        var ct = new CancellationTokenSource(5.Seconds()).Token;
        var client = GetClient();

        var threadPath = await CreateThreadAsync(client, "Deadlock test", ct);
        var response = await client.AwaitResponse(
            new SubmitMessageRequest { ThreadPath = threadPath, UserMessageText = "Quick test" },
            o => o.WithTarget(new Address(threadPath)), ct);

        response.Message.Success.Should().BeTrue("SubmitMessageResponse should arrive without deadlock");
    }

    /// <summary>
    /// Posts DataChangeRequest with MeshNode directly to the Thread hub.
    /// Verifies the workspace stream AND hub content both reflect the update.
    /// </summary>
    [Fact]
    public async Task SubmitMessage_DataChangeRequest_UpdatesMeshNodeOnThreadHub()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread
        var threadPath = await CreateThreadAsync(client, "DataChange test", ct);

        // 2. Post DataChangeRequest with updated MeshNode
        var workspace = client.GetWorkspace();
        var remoteNodes = await workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .FirstAsync().ToTask(ct);
        var threadNode = remoteNodes?.FirstOrDefault(n => n.Path == threadPath);
        threadNode.Should().NotBeNull();

        var currentContent = threadNode!.Content as MeshThread ?? new MeshThread();
        var updatedNode = threadNode with
        {
            Content = currentContent with { ThreadMessages = ["test1", "test2"] }
        };

        var updatedMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);
        client.Post(new DataChangeRequest { Updates = [updatedNode] },
            o => o.WithTarget(new Address(threadPath)));

        var msgIds = await updatedMessages;
        msgIds.Should().BeEquivalentTo(["test1", "test2"]);

        // 3. Verify via GetDataRequest
        var threadContent = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        threadContent.Should().NotBeNull();
        threadContent!.ThreadMessages.Should().BeEquivalentTo(["test1", "test2"]);
    }

    /// <summary>
    /// Mimics the exact GUI DataBind deserialization path for ThreadViewModel.
    /// </summary>
    [Fact]
    public void DataBind_ThreadViewModel_CanDeserializeAsObjectAndExtractMessages()
    {
        var options = Mesh.JsonSerializerOptions;
        var vm = new ThreadViewModel { Messages = ["msg1", "msg2", "abc123"] };
        var serialized = System.Text.Json.JsonSerializer.SerializeToElement(vm, options);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize<object>(
            serialized.GetRawText(), options);
        deserialized.Should().BeOfType<ThreadViewModel>();

        var vm2 = (ThreadViewModel)deserialized!;
        vm2.Messages.Should().BeEquivalentTo(new[] { "msg1", "msg2", "abc123" });

        IReadOnlyList<string>? converted = deserialized switch
        {
            ThreadViewModel tvm => tvm.Messages,
            _ => null
        };
        converted.Should().NotBeNull();
        converted.Should().BeEquivalentTo(new[] { "msg1", "msg2", "abc123" });
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

        public Task<ChatClientAgent> CreateAgentAsync(
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
            return Task.FromResult(agent);
        }
    }

    #endregion
}
