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
using MeshWeaver.AI.Persistence;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
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
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text, "Roland");
        var delivery = client.Post(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address))!;
        var response = await client.RegisterCallback(delivery, (d, _) => Task.FromResult(d), ct);
        var createResponse = ((IMessageDelivery<CreateNodeResponse>)response).Message;
        createResponse.Success.Should().BeTrue(createResponse.Error);
        return createResponse.Node!.Path!;
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
                Output.WriteLine($"Messages stream: {ids.Count} IDs");
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
        var twoMessages = ObserveMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest { ThreadPath = threadPath, UserMessageText = "Hello agent" },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        // 2. Wait for message IDs to appear
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"Messages: [{string.Join(", ", msgIds)}]");

        // 3. Verify thread content via GetDataRequest
        var threadContent = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        threadContent.Should().NotBeNull("thread hub should return Thread content");
        threadContent!.Messages.Should().HaveCount(2, "thread should have 2 message IDs");

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
    public async Task SubmitMessage_SecondMessage_AccumulatesMessages()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread
        var threadPath = await CreateThreadAsync(client, "Multi-message", ct);
        var messagesStream = ObserveMessages(client, threadPath);

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
        threadContent!.Messages.Should().HaveCount(4, "thread should have 4 message IDs after 2 submits");

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
        var twoMessages = ObserveMessages(client, threadPath)
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
            Content = currentContent with { Messages = ["test1", "test2"] }
        };

        var updatedMessages = ObserveMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);
        client.Post(new DataChangeRequest { Updates = [updatedNode] },
            o => o.WithTarget(new Address(threadPath)));

        var msgIds = await updatedMessages;
        msgIds.Should().BeEquivalentTo(["test1", "test2"]);

        // 3. Verify via GetDataRequest
        var threadContent = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        threadContent.Should().NotBeNull();
        threadContent!.Messages.Should().BeEquivalentTo(["test1", "test2"]);
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

    /// <summary>
    /// End-to-end test that verifies the full GUI data flow:
    /// 1. Create thread via CreateThreadRequest
    /// 2. Subscribe to Thread hub's layout area stream (like Blazor does)
    /// 3. Submit message via SubmitMessageRequest
    /// 4. Verify ThreadViewModel arrives via the layout area data section
    /// 5. Verify Thread content via GetDataRequest (Thread.Messages populated)
    /// 6. Verify each ThreadMessage content via GetDataRequest (correct role/text/type)
    /// 7. Wait for streaming to complete, verify response text is non-empty
    /// This catches issues that only manifest in the full GUI pipeline (e.g., PostgreSQL persistence).
    /// </summary>
    [Fact]
    public async Task EndToEnd_SubmitChat_FullGuiDataFlow()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread
        var threadPath = await CreateThreadAsync(client, "End-to-end test", ct);
        Output.WriteLine($"Thread created: {threadPath}");

        // 2. Subscribe to layout area stream — same path the Blazor ThreadChatView takes.
        // The Thread hub pushes ThreadViewModel to the data section.
        var workspace = client.GetWorkspace();
        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(ThreadNodeType.ThreadArea));
        layoutStream.Should().NotBeNull("Thread hub should serve the Thread layout area");

        // 3. Subscribe to Messages via workspace stream (like ThreadChatView data binding)
        var twoMessages = ObserveMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        // 4. Submit message
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Hello from end-to-end test",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);
        Output.WriteLine("Message submitted successfully");

        // 5. Wait for 2 message IDs
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2, "should have user + response message IDs");
        Output.WriteLine($"Messages: [{string.Join(", ", msgIds)}]");

        // 6. Verify Thread content via GetDataRequest
        var threadContent = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        threadContent.Should().NotBeNull("Thread hub should return Thread content via GetDataRequest");
        threadContent!.Messages.Should().HaveCount(2);
        threadContent.Messages[0].Should().Be(msgIds[0]);
        threadContent.Messages[1].Should().Be(msgIds[1]);
        Output.WriteLine($"Thread content verified: {threadContent.Messages.Count} messages");

        // 7. Verify user message via GetDataRequest
        var userContent = await GetHubContentAsync<ThreadMessage>(
            client, $"{threadPath}/{msgIds[0]}", ct);
        userContent.Should().NotBeNull("user message hub should return ThreadMessage via GetDataRequest");
        userContent!.Role.Should().Be("user");
        userContent.Text.Should().Be("Hello from end-to-end test");
        userContent.Type.Should().Be(ThreadMessageType.ExecutedInput);
        Output.WriteLine($"User message verified: '{userContent.Text}'");

        // 8. Verify response message via GetDataRequest — poll until streaming completes
        ThreadMessage? responseContent = null;
        var previousLength = 0;
        var stableCount = 0;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            responseContent = await GetHubContentAsync<ThreadMessage>(
                client, $"{threadPath}/{msgIds[1]}", ct);
            var len = responseContent?.Text?.Length ?? 0;
            if (len > 0 && len == previousLength && ++stableCount >= 2) break;
            else stableCount = 0;
            previousLength = len;
            await Task.Delay(200, ct);
        }

        responseContent.Should().NotBeNull("response message hub should return ThreadMessage via GetDataRequest");
        responseContent!.Role.Should().Be("assistant");
        responseContent.Type.Should().Be(ThreadMessageType.AgentResponse);
        responseContent.Text.Should().NotBeNullOrEmpty("streaming should produce non-empty response");
        Output.WriteLine($"Response message verified: '{responseContent.Text}' ({responseContent.Text.Length} chars)");

        // 9. Clean up layout stream
        layoutStream.Dispose();
    }

    /// <summary>
    /// a) GetStream on own hub returns the MeshNode for the hub's address.
    /// b) GetRemoteStream from client returns the same MeshNode.
    /// </summary>
    [Fact]
    public async Task GetStream_OwnHub_ReturnsMeshNode()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        // Create a thread node
        var client = GetClient();
        var threadPath = await CreateThreadAsync(client, "Stream test", ct);
        Output.WriteLine($"Thread: {threadPath}");

        // a) Get stream from own hub — use GetStream<MeshNode>()
        // We access the thread hub's workspace via GetRemoteStream<MeshNode> from client
        var nodes = await client.GetWorkspace()
            .GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Timeout(5.Seconds())
            .FirstAsync(n => n != null && n.Any());

        var node = nodes.FirstOrDefault(n => n.Path == threadPath);
        node.Should().NotBeNull("Thread node should be in the stream");
        node!.Content.Should().BeOfType<MeshThread>("Content should be Thread");
        Output.WriteLine($"a) Got MeshNode: {node.Path}, Content type: {node.Content?.GetType().Name}");
    }

    [Fact]
    public async Task GetRemoteStream_FromClient_ReturnsMeshNode()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        // Create a thread node
        var client = GetClient();
        var threadPath = await CreateThreadAsync(client, "Remote stream test", ct);
        Output.WriteLine($"Thread: {threadPath}");

        // b) Get remote stream for specific entity via EntityReference
        var workspace = client.GetWorkspace();
        var entityStream = workspace.GetRemoteStream<object, EntityReference>(
            new Address(threadPath),
            new EntityReference(nameof(MeshNode), threadPath));

        var entity = await entityStream
            .Timeout(5.Seconds())
            .Select(ci => ci.Value)
            .FirstAsync(v => v != null);

        entity.Should().NotBeNull();
        Output.WriteLine($"b) Got entity: {entity?.GetType().Name}");
    }

    /// <summary>
    /// Tests UpdateMeshNode on LOCAL stream — thread hub updates its own MeshNode.
    /// Uses GetStream(typeof(MeshNode)) internally.
    /// </summary>
    [Fact]
    public async Task UpdateMeshNode_Local_UpdatesMessages()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread
        var threadPath = await CreateThreadAsync(client, "Local update test", ct);

        // 2. Observe Messages from client
        var messagesChanged = client.GetWorkspace()
            .GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                return (node?.Content as MeshThread)?.Messages ?? [];
            })
            .Where(m => m.Count >= 2)
            .FirstAsync().ToTask(ct);

        // 3. Update via local stream (null address = own hub)
        // Post a DataChangeRequest to the thread hub to trigger the update on its own workspace
        client.Post(new DataChangeRequest
        {
            Updates = [new MeshNode(threadPath)
            {
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread
                {
                    Messages = ImmutableList.Create("msg1", "msg2")
                }
            }]
        }, o => o.WithTarget(new Address(threadPath)));

        // 4. Verify
        var messages = await messagesChanged;
        messages.Should().Contain("msg1");
        messages.Should().Contain("msg2");
        Output.WriteLine($"Local update: [{string.Join(", ", messages)}]");
    }

    /// <summary>
    /// Tests UpdateMeshNode extension via stream.Update on own hub's EntityStore stream.
    /// </summary>
    [Fact]
    public async Task UpdateMeshNode_ViaStreamUpdate_UpdatesMessages()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;
        var client = GetClient();

        // 1. Create thread
        var threadPath = await CreateThreadAsync(client, "Stream update test", ct);

        // 2. Observe Messages from client
        var messagesChanged = client.GetWorkspace()
            .GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                return (node?.Content as MeshThread)?.Messages ?? [];
            })
            .Where(m => m.Count >= 2)
            .FirstAsync().ToTask(ct);

        // 3. Get the thread hub's own EntityStore stream and update via UpdateMeshNode
        var threadHubStream = client.GetWorkspace()
            .GetRemoteStream<EntityStore, CollectionsReference>(
                new Address(threadPath),
                new CollectionsReference(nameof(MeshNode)));
        threadHubStream.Should().NotBeNull();

        threadHubStream!.UpdateMeshNode(threadPath, node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with
            {
                Content = thread with { Messages = thread.Messages.AddRange(["msg1", "msg2"]) }
            };
        });

        // 4. Verify
        var messages = await messagesChanged;
        messages.Should().Contain("msg1");
        messages.Should().Contain("msg2");
        Output.WriteLine($"Stream update: [{string.Join(", ", messages)}]");
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
