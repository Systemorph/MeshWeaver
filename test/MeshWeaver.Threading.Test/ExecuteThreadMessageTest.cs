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
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
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
/// Every test here exercises the GUI submission path:
/// <list type="number">
///   <item><see cref="ThreadSubmission.CreateThreadAndSubmit"/> creates the thread node
///     with the first user message pre-seeded in <c>PendingUserMessages</c> â€” the
///     server-side watcher generates the response cell id and dispatches the round.</item>
///   <item><see cref="ThreadSubmission.Submit"/> posts <see cref="SubmitMessageRequest"/>
///     for subsequent messages on an existing thread.</item>
///   <item>State is observed via <c>client.GetWorkspace().GetMeshNodeStream(path)</c> â€”
///     the remote-stream-cache-backed reactive handle, never <c>GetDataRequest</c>
///     polling or ad-hoc remote streams.</item>
/// </list>
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

    /// <summary>
    /// Posts <see cref="CreateNodeRequest"/> for an empty thread node (no
    /// pre-seeded user message) so tests that exercise <see cref="ThreadSubmission.Submit"/>
    /// separately have a thread to target.
    /// </summary>
    private string CreateEmptyThread(IMessageHub client, string title)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, title, "Roland");
        var response = client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).Should().Emit();
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        return response.Message.Node!.Path!;
    }

    [Fact]
    public void SubmitMessage_CreatesUserAndResponseNodes()
    {
        var client = GetClient();

        var threadPath = CreateEmptyThread(client, "CreatesUserAndResponseNodes");
        var responseMsgId = ThreadFlow.SubmitAndWait(client, threadPath,
            "Hello agent", contextPath: ContextPath).Should().Within(15.Seconds()).Emit();

        var thread = ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Count >= 2).Should().Within(15.Seconds()).Emit();
        thread.Messages.Should().HaveCount(2);
        Output.WriteLine($"Messages: [{string.Join(", ", thread.Messages)}]");

        var userId = thread.Messages[0];
        var userContent = ThreadFlow.ReadMessage(client, threadPath, userId,
            m => m.Role == "user").Should().Emit();
        userContent.Text.Should().Be("Hello agent");
        userContent.Type.Should().Be(ThreadMessageType.ExecutedInput);

        var response = ThreadFlow.ReadMessage(client, threadPath, responseMsgId,
            m => m.Role == "assistant").Should().Emit();
        response.Type.Should().Be(ThreadMessageType.AgentResponse);
    }

    [Fact]
    public void SubmitMessage_SecondMessage_AccumulatesMessages()
    {
        var client = GetClient();

        var threadPath = CreateEmptyThread(client, "Multi-message");

        ThreadFlow.SubmitAndWait(client, threadPath, "First",
            contextPath: ContextPath).Should().Within(30.Seconds()).Emit();
        var thread1 = ThreadFlow.ReadThread(client, threadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 2).Should().Within(30.Seconds()).Emit();
        thread1.Messages.Should().HaveCount(2);
        Output.WriteLine($"After first: [{string.Join(", ", thread1.Messages)}]");

        ThreadFlow.SubmitAndWait(client, threadPath, "Second",
            contextPath: ContextPath).Should().Within(30.Seconds()).Emit();
        var thread2 = ThreadFlow.ReadThread(client, threadPath,
            t => t is { IsExecuting: false } && t.Messages.Count >= 4).Should().Within(30.Seconds()).Emit();
        thread2.Messages.Should().HaveCount(4);
        Output.WriteLine($"All: [{string.Join(", ", thread2.Messages)}]");

        for (var i = 0; i < thread2.Messages.Count; i++)
        {
            var msg = ThreadFlow.ReadMessage(client, threadPath, thread2.Messages[i],
                _ => true).Should().Emit();
            Output.WriteLine($"  [{i}] {msg.Role}: '{msg.Text}'");
        }
    }

    [Fact]
    public void SubmitMessage_BothNodesGetCorrectContentViaStream()
    {
        var client = GetClient();

        var threadPath = CreateEmptyThread(client, "Content verification");
        var responseMsgId = ThreadFlow.SubmitAndWait(client, threadPath,
            "Tell me something", contextPath: ContextPath).Should().Within(15.Seconds()).Emit();

        var thread = ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Count >= 2).Should().Within(15.Seconds()).Emit();
        Output.WriteLine($"Message IDs: [{string.Join(", ", thread.Messages)}]");

        var userContent = ThreadFlow.ReadMessage(client, threadPath, thread.Messages[0],
            m => m.Role == "user").Should().Emit();
        userContent.Text.Should().Be("Tell me something");
        userContent.Type.Should().Be(ThreadMessageType.ExecutedInput);
        Output.WriteLine($"User: role={userContent.Role}, text='{userContent.Text}'");

        var response = ThreadFlow.ReadMessage(client, threadPath, responseMsgId,
            m => !string.IsNullOrEmpty(m.Text) && m.Status != ThreadMessageStatus.Streaming).Should().Emit();
        response.Role.Should().Be("assistant");
        response.Type.Should().Be(ThreadMessageType.AgentResponse);
        response.Text.Length.Should().BeGreaterThan(10);
        Output.WriteLine($"Response: '{response.Text}' ({response.Text.Length} chars)");
    }

    [Fact]
    public void SubmitMessage_DoesNotDeadlock_ResponseWithin5Seconds()
    {
        var client = GetClient();

        var threadPath = CreateEmptyThread(client, "Deadlock test");
        var responseMsgId = ThreadFlow.SubmitAndWait(client, threadPath, "Quick test",
            contextPath: ContextPath, timeout: 5.Seconds()).Should().Within(5.Seconds()).Emit();

        responseMsgId.Should().NotBeNullOrEmpty(
            "SubmitMessageRequest should complete within 5 seconds without deadlock");
    }

    /// <summary>
    /// Pins the prod 2026-05-21 thread-loading invariant: a thread's chat
    /// execution must complete under the user's <see cref="AccessContext"/>
    /// â€” every message MeshNode the chat flow creates must carry the user's
    /// identity in <c>CreatedBy</c>, NOT a hub-self-impersonated address
    /// (<c>sync/...</c>, <c>activity/...</c>, <c>node/...</c>). Pin against the
    /// cross-cutting <see cref="MeshWeaver.Messaging.AccessContextCaptureExtensions.CarryAccessContext"/>
    /// wrap on every framework write primitive.
    ///
    /// <para>Without the wrap, the per-message-cell <c>CreateNode</c> in
    /// <c>ChatClientAgentFactory.ExecuteAsync</c>'s Subscribe callbacks would
    /// run on a thread where AsyncLocal AccessContext is wiped â†’ PostPipeline
    /// (after the 2026-05-21 hub-self-impersonation deletion) would fail closed
    /// â†’ the chat flow would deadlock at the first persisted write. This test
    /// is the canonical pin against that regression.</para>
    /// </summary>
    [Fact]
    public void SubmitMessage_PersistsMessageNodes_WithUserIdentity()
    {
        var client = GetClient();

        var threadPath = CreateEmptyThread(client, "AccessContext rides");
        var responseMsgId = ThreadFlow.SubmitAndWait(client, threadPath,
            "Identity probe", contextPath: ContextPath).Should().Within(10.Seconds()).Emit();

        var thread = ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Count >= 2).Should().Within(10.Seconds()).Emit();
        thread.Messages.Should().HaveCount(2,
            "user input + assistant response must both land");

        // Drill into the underlying MeshNode rows via the same cache that the
        // GUI uses â€” the .CreatedBy stamping is what AccessControl would have
        // seen at the moment of write.
        var workspace = client.GetWorkspace();
        foreach (var msgId in thread.Messages)
        {
            var msgPath = $"{threadPath}/{msgId}";
            var node = workspace.GetMeshNodeStream(msgPath).Should().Within(5.Seconds()).Emit();
            Output.WriteLine($"  - {msgPath} CreatedBy={node.CreatedBy ?? "(null)"}");
            // The critical assertion. Pre-2026-05-21, message nodes were stamped
            // with the chat-execution hub's own address (something starting with
            // "node/", "activity/", or "sync/"). Post-fix, every CreateNode runs
            // under the actual user's AccessContext â€” Roland (the canonical
            // test-base user from AddSampleUsers).
            node.CreatedBy.Should().NotStartWith("node/",
                $"message {msgPath} must not be written as a per-node hub identity");
            node.CreatedBy.Should().NotStartWith("activity/",
                $"message {msgPath} must not be written as an activity hub identity");
            node.CreatedBy.Should().NotStartWith("sync/",
                $"message {msgPath} must not be written as a Blazor sync hub identity");
            node.CreatedBy.Should().NotStartWith("mesh/",
                $"message {msgPath} must not be written as the mesh routing hub identity");
        }
    }

    /// <summary>
    /// DataChangeRequest is a CLIENT primitive â€” not the canonical thread mutation
    /// path (the GUI uses <c>workspace.GetMeshNodeStream(path).Update(...)</c>) but
    /// it must still apply correctly when used directly. The thread is observed
    /// through the same remote-stream-cache reactive handle the GUI uses.
    /// </summary>
    [Fact]
    public void SubmitMessage_DataChangeRequest_UpdatesMeshNodeOnThreadHub()
    {
        var client = GetClient();

        var threadPath = CreateEmptyThread(client, "DataChange test");
        var workspace = client.GetWorkspace();

        var current = workspace.GetMeshNodeStream(threadPath).Should().Within(5.Seconds()).Emit();
        var updatedNode = current with
        {
            Content = (current.Content as MeshThread ?? new MeshThread())
                with { Messages = ["test1", "test2"] }
        };

        client.Post(new DataChangeRequest { Updates = [updatedNode] },
            o => o.WithTarget(new Address(threadPath)));

        var msgIds = ThreadFlow.ObserveMessages(client, threadPath)
            .Should().Match(ids => ids.Count >= 2);
        msgIds.Should().BeEquivalentTo(new[] { "test1", "test2" }, client.JsonSerializerOptions);

        var thread = ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Count >= 2).Should().Emit();
        thread.Messages.Should().BeEquivalentTo(new[] { "test1", "test2" }, client.JsonSerializerOptions);
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
        vm2.Messages.Should().BeEquivalentTo(new[] { "msg1", "msg2", "abc123" }, options);

        IReadOnlyList<string>? converted = deserialized switch
        {
            ThreadViewModel tvm => tvm.Messages,
            _ => null
        };
        converted.Should().NotBeNull();
        converted.Should().BeEquivalentTo(new[] { "msg1", "msg2", "abc123" }, options);
    }

    /// <summary>
    /// End-to-end test that the full GUI data flow works: thread creation +
    /// submission via <see cref="ThreadSubmission"/> + layout-area subscription +
    /// data binding via <c>GetMeshNodeStream</c>.
    /// </summary>
    [Fact]
    public void EndToEnd_SubmitChat_FullGuiDataFlow()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadPath = CreateEmptyThread(client, "End-to-end test");
        Output.WriteLine($"Thread created: {threadPath}");

        // Open the layout area â€” same subscription the Blazor ThreadChatView holds.
        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(ThreadNodeType.ThreadArea));
        // The thread hub must serve the Thread layout area.
        layoutStream.Should().Within(20.Seconds()).Emit();

        var responseMsgId = ThreadFlow.SubmitAndWait(client, threadPath,
            "Hello from end-to-end test", contextPath: ContextPath).Should().Within(20.Seconds()).Emit();
        Output.WriteLine($"Response id: {responseMsgId}");

        var thread = ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Count >= 2).Should().Within(20.Seconds()).Emit();
        thread.Messages.Should().HaveCount(2);
        Output.WriteLine($"Messages: [{string.Join(", ", thread.Messages)}]");

        var userContent = ThreadFlow.ReadMessage(client, threadPath, thread.Messages[0],
            m => m.Role == "user").Should().Emit();
        userContent.Text.Should().Be("Hello from end-to-end test");
        userContent.Type.Should().Be(ThreadMessageType.ExecutedInput);
        Output.WriteLine($"User message: '{userContent.Text}'");

        var response = ThreadFlow.ReadMessage(client, threadPath, responseMsgId,
            m => !string.IsNullOrEmpty(m.Text) && m.Status != ThreadMessageStatus.Streaming).Should().Emit();
        response.Role.Should().Be("assistant");
        response.Type.Should().Be(ThreadMessageType.AgentResponse);
        Output.WriteLine($"Response: '{response.Text}' ({response.Text.Length} chars)");

        layoutStream.Dispose();
    }

    /// <summary>
    /// <c>workspace.GetMeshNodeStream(path)</c> from the client routes through the
    /// remote stream cache and returns the MeshNode owned by the per-node hub.
    /// </summary>
    [Fact]
    public void GetStream_OwnHub_ReturnsMeshNode()
    {
        var client = GetClient();
        var threadPath = CreateEmptyThread(client, "Stream test");

        var node = client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Should().Within(5.Seconds()).Emit();

        node.Path.Should().Be(threadPath);
        node.Content.Should().BeOfType<MeshThread>();
        Output.WriteLine($"Got MeshNode: {node.Path}, Content type: {node.Content?.GetType().Name}");
    }

    /// <summary>
    /// <c>workspace.GetMeshNodeStream(path)</c> from a client workspace is the
    /// canonical "live single-MeshNode" read â€” replaces the old
    /// <c>GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c> ad-hoc pattern.
    /// </summary>
    [Fact]
    public void GetRemoteStream_FromClient_ReturnsMeshNode()
    {
        var client = GetClient();
        var threadPath = CreateEmptyThread(client, "Remote stream test");

        var node = client.GetWorkspace().GetMeshNodeStream(threadPath)
            .Should().Within(5.Seconds()).Emit();

        node.Should().NotBeNull();
        node.Path.Should().Be(threadPath);
        Output.WriteLine($"Got node: {node.Path}, type: {node.Content?.GetType().Name}");
    }

    /// <summary>
    /// <c>workspace.GetMeshNodeStream(path).Update(...)</c> from a client routes
    /// the patch through the remote stream cache to the owning per-node hub â€”
    /// same primitive <see cref="ThreadInput.AppendUserInput"/> uses.
    /// </summary>
    [Fact]
    public void UpdateMeshNode_Local_UpdatesMessages()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadPath = CreateEmptyThread(client, "Local update test");

        // Subscribing to the Update observable performs the cross-hub write.
        workspace.GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = t with { Messages = ImmutableList.Create("msg1", "msg2") } };
        }).Should().Emit();

        var messages = ThreadFlow.ObserveMessages(client, threadPath)
            .Should().Match(m => m.Count >= 2);
        messages.Should().Contain("msg1");
        messages.Should().Contain("msg2");
        Output.WriteLine($"Update: [{string.Join(", ", messages)}]");
    }

    /// <summary>
    /// Same as <see cref="UpdateMeshNode_Local_UpdatesMessages"/> but the update
    /// is composed off the latest snapshot â€” verifies <c>.Update(current =&gt; ...)</c>
    /// observes the live node when the lambda runs.
    /// </summary>
    [Fact]
    public void UpdateMeshNode_ViaStreamUpdate_UpdatesMessages()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadPath = CreateEmptyThread(client, "Stream update test");

        // Subscribing to the Update observable performs the cross-hub write.
        workspace.GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = t with { Messages = t.Messages.AddRange(["msg1", "msg2"]) } };
        }).Should().Emit();

        var messages = ThreadFlow.ObserveMessages(client, threadPath)
            .Should().Match(m => m.Count >= 2);
        messages.Should().Contain("msg1");
        messages.Should().Contain("msg2");
        Output.WriteLine($"Stream update: [{string.Join(", ", messages)}]");
    }

    /// <summary>
    /// Creating a node with empty Id should return a validation failure, not crash.
    /// </summary>
    [Fact]
    public void CreateNode_WithEmptyId_ReturnsValidationError()
    {
        var client = GetClient();

        var nodeWithEmptyId = new MeshNode("", "SomeNamespace")
        {
            NodeType = "Markdown",
            Name = "Test"
        };

        var response = client.Observe(new CreateNodeRequest(nodeWithEmptyId),
            o => o.WithTarget(Mesh.Address)).Should().Within(5.Seconds()).Emit();

        response.Message.Success.Should().BeFalse("creating a node with empty Id should fail");
        response.Message.Error.Should().Contain("Id");
        Output.WriteLine($"Validation error: {response.Message.Error}");
    }

    /// <summary>
    /// Creating a node with null Id should return a validation failure.
    /// </summary>
    [Fact]
    public void CreateNode_WithNullId_ReturnsValidationError()
    {
        var client = GetClient();

        var nodeWithNullId = new MeshNode(null!, null)
        {
            NodeType = "Markdown",
            Name = "Test"
        };

        var response = client.Observe(new CreateNodeRequest(nodeWithNullId),
            o => o.WithTarget(Mesh.Address)).Should().Within(5.Seconds()).Emit();

        response.Message.Success.Should().BeFalse("creating a node with null Id should fail");
        response.Message.Error.Should().Contain("Id");
        Output.WriteLine($"Validation error: {response.Message.Error}");
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
