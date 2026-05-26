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
/// ðŸš¨ Repro for "chat stuck on Generating response..." â€” a single end-to-end
/// chat round that reactively asserts the full IsExecuting lifecycle.
///
/// Drives the GUI handler (<see cref="ThreadSubmission.Submit"/>) and observes
/// via <c>client.GetWorkspace().GetMeshNodeStream(path)</c> â€” the same reactive
/// handle the Blazor view holds; if the streaming pipeline hangs, the
/// IsExecuting=false wait times out and the test fails loud.
/// </summary>
public class IsExecutingLifecycleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
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

    [Fact(Timeout = 30_000)]
    public async Task SingleMessage_IsExecuting_FlipsTrueThenFalse_WithRealResponse()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "hello", "TestUser");
        var createDelivery = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(ct);
        createDelivery.Message.Success.Should().BeTrue(createDelivery.Message.Error);
        var threadPath = createDelivery.Message.Node!.Path!;

        // Warm up the remote stream subscription BEFORE submit so the
        // IsExecuting=trueâ†’false transition is captured. Same pattern
        // ThreadFlow.SubmitAndWait uses.
        var baselineThread = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t != null)
            .Take(1)
            .Timeout(10.Seconds())
            .ToTask(ct);
        baselineThread!.IsExecuting.Should().BeFalse("thread should not be executing yet");

        // Subscribe to the executing transition BEFORE submit. Wait for the
        // committed `Executing` state â€” NOT just any non-Idle state â€” because
        // `IsExecuting` is true during the transient `StartingExecution`
        // claim window where `ActiveMessageId` is still null (the responseMsgId
        // is generated downstream by DispatchAfterClaim's commit, which flips
        // Status â†’ Executing AND stamps ActiveMessageId in one update).
        var executingTask = workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { Status: ThreadExecutionStatus.Executing })
            .Take(1)
            .Timeout(10.Seconds())
            .ToTask(ct);

        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client,
            ThreadPath = threadPath,
            UserText = "hello",
            ContextPath = ContextPath,
        });

        // 1) IsExecuting must flip to true within ~10s.
        var executingState = await executingTask;
        executingState!.IsExecuting.Should().BeTrue();
        executingState.ActiveMessageId.Should().NotBeNullOrEmpty(
            "ActiveMessageId must point at the response cell during streaming");

        // 2) IsExecuting must flip BACK to false within 30s.
        var doneState = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { IsExecuting: false } && t.Messages.Count >= 2)
            .Take(1)
            .Timeout(30.Seconds())
            .ToTask(ct);
        doneState!.IsExecuting.Should().BeFalse(
            "execution must terminate cleanly, not stay running until the watchdog");
        doneState.ExecutionStartedAt.Should().BeNull("started-at is cleared on completion");

        // 3) Final ThreadMessage.CompletedAt must be set AND text non-empty.
        var lastMsgId = doneState.Messages[^1];
        var finalMessage = await ThreadFlow.ReadMessage(client, threadPath, lastMsgId,
            m => m.CompletedAt != null && !string.IsNullOrEmpty(m.Text),
            timeout: 15.Seconds()).FirstAsync().ToTask(ct);

        finalMessage.Text.Should().Contain("I received",
            "the Echo agent's streaming reply must reach the response cell â€” "
            + "if this fails with the placeholder, the streaming Task.Run hung "
            + "but the parent flipped IsExecuting=false anyway, masking a real bug.");

        // 4) Final ThreadMessage.Status must be Completed (terminal-status guard).
        finalMessage.Status.Should().Be(ThreadMessageStatus.Completed,
            "terminal-status guard must prevent a late Sample-flushed Streaming push "
            + "from regressing the cell from Completed back to Streaming");
    }

    #region Echo IChatClient + factory (same shape as ChatHistoryTest)

    private sealed class EchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("EchoProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, $"I received {messages.Count()} messages.")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var n = messages.Count();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                $"I received {n} messages in this conversation.");
            await Task.Delay(5, ct);
        }

        public object? GetService(Type serviceType, object? key = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private sealed class EchoChatClientFactory : IChatClientFactory
    {
        public string Name => "EchoFactory";
        public IReadOnlyList<string> Models => ["echo-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new EchoChatClient(), instructions: "Echo agent.",
                name: config.Id, description: config.Description ?? "",
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
