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
/// Creates a thread with a pre-populated queued message (PendingUserMessages) in one shot.
/// Verifies that:
/// 1. AutoExecutePendingMessage creates the child ThreadMessage nodes
/// 2. UpdateThreadMessageContent routes to the response grain
/// 3. Execution completes and response text is written
///
/// This reproduces the production bug where UpdateThreadMessageContent
/// went to the thread grain instead of the response message grain
/// because the child nodes weren't created in persistence.
///
/// 🚨 Tests <c>await</c> the reactive assertions: each terminal
/// <c>ObservableAssertions</c> method bridges the stream to a Task at the test
/// edge (the sanctioned <c>.FirstAsync()/.ToTask()</c> bridge) — no blocking
/// wait inside the test body. See ObservableAssertions remarks.
/// </summary>
public class OrleansAutoExecuteTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    /// <summary>
    /// The node PATH of the built-in default agent — the form the composer's picker
    /// stores, so <c>AgentChatClient.ResolveSelectedAgent</c> resolves it by exact path.
    /// <para>🚨 It must name an agent that actually SHIPS (<c>content/ai/Agent/*.md</c>).
    /// These tests used to ask for <c>"Orchestrator"</c>, which was renamed to
    /// <c>Assistant</c> in c31fd04da — since then no such agent has existed, so every
    /// round could only ever produce the terminal "agent not found" error. The weak
    /// assertions (any non-empty response text) accepted that error, and the
    /// "Allocating agent..." placeholder, as success.</para>
    /// </summary>
    private const string DefaultAgentPath = "Agent/Assistant";

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
    public async Task AutoExecute_CreatesResponseCell_And_CompletesExecution()
    {
        Fixture.ChatFactory.SetInner(new AutoExecEchoChatClientFactory());
        var client = GetClient();

        // Build thread with pre-populated messages (auto-execute on activation).
        // responseMsgId is allocated by DispatchAfterClaim (BuildThreadWithMessages
        // returns ""), so we read the real id from Thread.Messages after the
        // submission watcher claims — see ThreadNodeType.BuildThreadWithMessages.
        var (threadNode, userMsgId, _) = ThreadNodeType.BuildThreadWithMessages(
            "TestUser", "Hello Orleans auto-execute!",
            createdBy: "TestUser", agentName: DefaultAgentPath);
        var threadPath = threadNode.Path!;
        Output.WriteLine($"Thread: {threadPath}, user={userMsgId}");

        // Create the thread — AutoExecutePendingMessage should fire on grain activation
        var createResponse = await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("TestUser")))
            .Should().Within(30.Seconds()).Emit();
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error ?? "");
        Output.WriteLine("Thread created, waiting for execution...");

        // Subscribing to the thread stream also activates the per-thread hub
        // (WatchForExecution → auto-execute dispatch). Wait for the watcher to
        // claim and allocate the response cell. `Messages` only ever grows, so
        // this predicate is monotonic — unlike `IsExecuting == false`, which is
        // ALSO true in the pre-execution window and made the wait a coin flip.
        var claimed = await GetHubContent<MeshThread>(client, threadPath)
            .Should().Within(30.Seconds())
            .Match(t => t is { Messages.Count: >= 2 });

        // Response cell id is Messages[1] (user is [0], response is [1]) — the id
        // DispatchAfterClaim allocated for this round.
        var responseMsgId = claimed!.Messages[1];
        var responsePath = $"{threadPath}/{responseMsgId}";

        // 🚨 The round-done gate is the response cell's TERMINAL Status write — the
        // same signal the submission watcher treats as round-done. Waiting on "any
        // non-empty Text" matched the "Allocating agent..." PLACEHOLDER the
        // framework writes when it allocates the cell, so the test passed without
        // any agent having run.
        var response = await GetHubContent<ThreadMessage>(client, responsePath)
            .Should().Within(30.Seconds())
            .Match(m => m is { Status: ThreadMessageStatus.Completed });
        response!.Text.Should().Contain("Echo:", "the echo agent should have written the response text");
        Output.WriteLine($"Response: {response.Text![..Math.Min(100, response.Text.Length)]}");

        // Only NOW is "execution settled" a real observed condition: the terminal
        // Status write has landed, so the thread must flip out of executing.
        await GetHubContent<MeshThread>(client, threadPath)
            .Should().Within(30.Seconds())
            .Match(t => t is { IsExecuting: false });
        Output.WriteLine("Thread execution complete");

        // Verify user cell exists.
        var userMsg = await GetHubContent<ThreadMessage>(client, $"{threadPath}/{userMsgId}")
            .Should().Within(30.Seconds())
            .Match(m => m is not null);
        userMsg!.Text.Should().Be("Hello Orleans auto-execute!");
        userMsg.Role.Should().Be("user");

        Output.WriteLine("PASSED");
    }

    /// <summary>
    /// Verifies that UpdateThreadMessageContent reaches the response grain (not the thread grain).
    /// The response cell should have text != "" and != "Allocating agent...".
    /// </summary>
    [Fact]
    public async Task AutoExecute_UpdateThreadMessageContent_RoutesToResponseGrain()
    {
        Fixture.ChatFactory.SetInner(new AutoExecEchoChatClientFactory());
        var client = GetClient();

        var (threadNode, _, _) = ThreadNodeType.BuildThreadWithMessages(
            "TestUser", "Test routing to response grain",
            createdBy: "TestUser", agentName: DefaultAgentPath);
        var threadPath = threadNode.Path!;

        await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("TestUser")))
            .Should().Within(30.Seconds()).Emit();

        // Activate the per-thread hub by subscribing to its stream — CreateNodeRequest
        // above landed at TestUser, the catalog has the node, but the per-thread grain
        // is created lazily on its first inbound message. Without this the hub's
        // WithInitialization callbacks (WatchForExecution that fires the auto-execute
        // dispatch) never run and the response cell is never created.
        // Wait for the watcher to claim and allocate the response cell — its id is
        // Messages[1] (BuildThreadWithMessages returns "" for responseMsgId now;
        // DispatchAfterClaim allocates the real id).
        var claimed = await GetHubContent<MeshThread>(client, threadPath)
            .Should().Within(30.Seconds()).Match(t => t is { Messages.Count: >= 2 });
        var responsePath = $"{threadPath}/{claimed!.Messages[1]}";

        // Wait for the response cell's TERMINAL Status write — the round-done gate.
        // 🚨 Screening the text for placeholder PREFIXES is not enough: an agent
        // selection failure also writes non-placeholder text (the "agent not found"
        // error), so this passed on a round in which UpdateThreadMessageContent
        // never routed anywhere. Assert the echo agent's actual output instead.
        var msg = await GetHubContent<ThreadMessage>(client, responsePath)
            .Should().Within(30.Seconds())
            .Match(m => m is { Status: ThreadMessageStatus.Completed });
        msg!.Text.Should().Contain("Echo:",
            "the streamed agent output must reach the RESPONSE grain, not the thread grain");
        Output.WriteLine($"Response cell has final text: {msg.Text![..Math.Min(80, msg.Text.Length)]}");
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
