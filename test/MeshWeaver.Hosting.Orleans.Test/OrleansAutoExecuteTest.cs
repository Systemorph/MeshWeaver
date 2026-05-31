using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

using System.Reactive.Linq;
namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans integration test: BuildThreadWithMessages + AutoExecutePendingMessage.
/// Creates a thread with pre-populated Messages + PendingUserMessage in one shot.
/// Verifies that:
/// 1. AutoExecutePendingMessage creates the child ThreadMessage nodes
/// 2. UpdateThreadMessageContent routes to the response grain
/// 3. Execution completes and response text is written
///
/// This reproduces the production bug where UpdateThreadMessageContent
/// went to the thread grain instead of the response message grain
/// because the child nodes weren't created in persistence.
///
/// 🚨 Tests are <c>void</c> + reactive assertions (no <c>async</c>/<c>await</c>):
/// blocking inside an async test deadlocks the in-process hub scheduler — the
/// agent's streaming execution shares the process and its continuations are
/// starved by the captured async SynchronizationContext. See
/// ReactiveTestAssertions.md §2 + ObservableAssertions remarks.
/// </summary>
public class OrleansAutoExecuteTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    // Synchronous client acquisition — the await-free test bodies resolve the
    // client hub once, up front, on the plain xUnit thread (no async context to
    // starve). The hub-reachable waits all live inside .Should() blocking
    // assertions afterward.
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"autoexec-{name}-{Guid.NewGuid():N}", "TestUser");

    /// <summary>
    /// Reactive single-node content read via the canonical
    /// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace, string)"/>
    /// path. Returns an <see cref="IObservable{T}"/> the caller asserts on with
    /// <c>.Should().Match(...)</c>; the stream filters pre-load empty snapshots so
    /// the first content-bearing emission carries the node.
    /// </summary>
    private static IObservable<T?> GetHubContent<T>(IMessageHub client, string path) where T : class
        => client.GetWorkspace().GetMeshNodeStream(path)
            .Select(node =>
            {
                if (node?.Content is T typed) return typed;
                if (node?.Content is JsonElement contentJe)
                    return contentJe.Deserialize<T>(client.JsonSerializerOptions);
                return null;
            });

    /// <summary>
    /// BuildThreadWithMessages creates thread + auto-executes.
    /// Response cell must be created, receive UpdateThreadMessageContent,
    /// and have final response text. Thread must end with IsExecuting=false.
    /// </summary>
    [Fact]
    public void AutoExecute_CreatesResponseCell_And_CompletesExecution()
    {
        SharedOrleansFixture.SwappableFactory.SetInner(new AutoExecEchoChatClientFactory());
        try
        {
            var client = GetClient();

            // Build thread with pre-populated messages (auto-execute on activation).
            // responseMsgId is allocated by DispatchAfterClaim (BuildThreadWithMessages
            // returns ""), so we read the real id from Thread.Messages after the
            // submission watcher claims — see ThreadNodeType.BuildThreadWithMessages.
            var (threadNode, userMsgId, _) = ThreadNodeType.BuildThreadWithMessages(
                "TestUser", "Hello Orleans auto-execute!",
                createdBy: "TestUser", agentName: "Orchestrator");
            var threadPath = threadNode.Path!;
            Output.WriteLine($"Thread: {threadPath}, user={userMsgId}");

            // Create the thread — AutoExecutePendingMessage should fire on grain activation
            var createResponse = client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("TestUser")))
                .Should().Within(30.Seconds()).Emit();
            createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error ?? "");
            Output.WriteLine("Thread created, waiting for execution...");

            // Subscribing to the thread stream also activates the per-thread hub
            // (WatchForExecution → auto-execute dispatch). Wait for execution to settle.
            var thread = GetHubContent<MeshThread>(client, threadPath)
                .Should().Within(30.Seconds())
                .Match(t => t is { IsExecuting: false, PendingUserMessage: null }
                    && t.Messages.Count >= 2);
            Output.WriteLine("Thread execution complete");

            // Response cell id is Messages[1] (user is [0], response is [1]) — the id
            // DispatchAfterClaim allocated for this round.
            var responseMsgId = thread!.Messages[1];
            var responsePath = $"{threadPath}/{responseMsgId}";
            var response = GetHubContent<ThreadMessage>(client, responsePath)
                .Should().Within(30.Seconds())
                .Match(m => !string.IsNullOrEmpty(m?.Text));
            response!.Text.Should().NotBeNullOrEmpty("agent should have written response text");
            Output.WriteLine($"Response: {response.Text![..Math.Min(100, response.Text.Length)]}");

            // Verify user cell exists.
            var userMsg = GetHubContent<ThreadMessage>(client, $"{threadPath}/{userMsgId}")
                .Should().Within(30.Seconds())
                .Match(m => m is not null);
            userMsg!.Text.Should().Be("Hello Orleans auto-execute!");
            userMsg.Role.Should().Be("user");

            Output.WriteLine("PASSED");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }

    /// <summary>
    /// Verifies that UpdateThreadMessageContent reaches the response grain (not the thread grain).
    /// The response cell should have text != "" and != "Allocating agent...".
    /// </summary>
    [Fact]
    public void AutoExecute_UpdateThreadMessageContent_RoutesToResponseGrain()
    {
        SharedOrleansFixture.SwappableFactory.SetInner(new AutoExecEchoChatClientFactory());
        try
        {
            var client = GetClient();

            var (threadNode, _, _) = ThreadNodeType.BuildThreadWithMessages(
                "TestUser", "Test routing to response grain",
                createdBy: "TestUser", agentName: "Orchestrator");
            var threadPath = threadNode.Path!;

            client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("TestUser")))
                .Should().Within(30.Seconds()).Emit();

            // Activate the per-thread hub by subscribing to its stream — CreateNodeRequest
            // above landed at TestUser, the catalog has the node, but the per-thread grain
            // is created lazily on its first inbound message. Without this the hub's
            // WithInitialization callbacks (WatchForExecution that fires the auto-execute
            // dispatch) never run and the response cell is never created.
            // Wait for the watcher to claim and allocate the response cell — its id is
            // Messages[1] (BuildThreadWithMessages returns "" for responseMsgId now;
            // DispatchAfterClaim allocates the real id).
            var claimed = GetHubContent<MeshThread>(client, threadPath)
                .Should().Within(30.Seconds()).Match(t => t is { Messages.Count: >= 2 });
            var responsePath = $"{threadPath}/{claimed!.Messages[1]}";

            // Wait for the response cell to have final text (not empty, not a placeholder).
            var msg = GetHubContent<ThreadMessage>(client, responsePath)
                .Should().Within(30.Seconds())
                .Match(m => m?.Text is { Length: > 0 } text
                    && !text.StartsWith("Allocating")
                    && !text.StartsWith("Loading")
                    && !text.StartsWith("Generating"));
            Output.WriteLine($"Response cell has final text: {msg!.Text![..Math.Min(80, msg.Text.Length)]}");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }

    #region Echo LLM

    private class AutoExecEchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("AutoExecEcho");
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

    private class AutoExecEchoChatClientFactory : IChatClientFactory
    {
        public string Name => "AutoExecEchoFactory";
        public IReadOnlyList<string> Models => ["echo-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => new(chatClient: new AutoExecEchoChatClient(), instructions: "Echo agent.",
                name: config.Id, description: config.Description ?? "",
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
