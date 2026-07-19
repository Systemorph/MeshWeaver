using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// 🚨 Deterministic repro for issue #539 — a thread round wedged at
/// <see cref="ThreadExecutionStatus.StartingExecution"/> forever when the
/// <c>_Exec</c> commit edge is lost.
///
/// <para>Round dispatch is a two-edge handshake: (1) the submission watcher
/// writes the CLAIM (<c>Idle → StartingExecution</c>); (2) the <c>_Exec</c>
/// round watcher's first emission writes the COMMIT
/// (<c>StartingExecution → Executing</c>, allocating the response cell and
/// stamping <c>ActiveMessageId</c>). When the hub is recycled between claim and
/// commit (a NodeType release, a redeploy, a grain migration), the claim
/// survives as durable node state but nothing re-drives the commit: the thread
/// parks at <c>StartingExecution</c> — the message stuck in
/// <c>PendingUserMessages</c>, no cell, no round — forever.
/// <c>InitializeThreadLifecycle</c> used to self-heal every state on activation
/// EXCEPT <c>StartingExecution</c> (a deliberate no-op that assumed the
/// <c>_Exec</c> round watcher would fire on its own first emission).</para>
///
/// <para>This test seeds that exact post-crash shape DIRECTLY to storage (the
/// claim made by a previous, now-dead hub incarnation — no cell, no
/// <c>ActiveMessageId</c>), then activates the thread hub cold and asserts:</para>
/// <list type="number">
///   <item>the activation-recovery emits its distinctive warning (proof the new
///     <c>StartingExecution</c>-orphaned-claim recovery branch ran) — this is the
///     <b>revert-proven</b> pin: without the fix that branch is a <c>default</c>
///     no-op and the warning is never emitted, so the assertion fails; and</item>
///   <item>the round runs to completion and <c>PendingUserMessages</c> drains
///     (the recovery re-queues the round so it actually executes).</item>
/// </list>
///
/// <para><b>Why the log pin, not a stuck-forever state check:</b> in the
/// in-process monolith harness the <c>_Exec</c> round watcher reliably observes
/// the REPLAYED claim on cold activation and would recover the round on its own,
/// so the wedge does not reproduce as a hang here (it is a distributed
/// grain-timing phenomenon where the watcher misses the replay — see issue
/// #539). The recovery's warning fires deterministically the instant the loaded
/// <c>StartingExecution</c>+no-cell state is read on activation, independent of
/// that race, so asserting on it pins the new recovery branch specifically.</para>
/// </summary>
public class OrphanedStartingExecutionRecoveryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/TestUser";
    private const string CreatedBy = "TestUser";

    // Captures Warning+ log lines so the test can assert the new orphaned-claim
    // recovery branch ran. Instance field (never static) — dies with the test.
    private readonly CapturingLoggerProvider _logCapture = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new EchoChatClientFactory());
                services.AddSingleton<ILoggerProvider>(_logCapture);
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddData();
    }

    [Fact(Timeout = 90_000)]
    public async Task OrphanedStartingExecutionClaim_RecoversOnActivation_AndRoundRuns()
    {
        var ct = TestContext.Current.CancellationToken;

        // ── Seed the orphaned-claim state directly to storage ────────────────
        // This is the post-crash state of a PREVIOUS hub incarnation: the
        // submission watcher wrote the claim (Idle → StartingExecution) with the
        // user message still queued in PendingUserMessages, but the hub died
        // BEFORE the _Exec round watcher committed — so there is NO response cell
        // and NO ActiveMessageId. Bypassing CreateNodeRequest means no hub ever
        // activated for this node: the claim is genuinely orphaned.
        var adapter = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var options = Mesh.JsonSerializerOptions;

        var speakingId = $"orphan-{Guid.NewGuid():N}"[..16];
        var threadNs = $"{ContextPath}/{ThreadNodeType.ThreadPartition}";
        var threadPath = $"{threadNs}/{speakingId}";
        var userMsgId = Guid.NewGuid().ToString("N")[..8];

        var pendingMessage = new ThreadMessage
        {
            Role = "user",
            Text = "hello from the orphaned claim",
            CreatedBy = CreatedBy,
            SubmitterObjectId = CreatedBy,
            SubmitterName = CreatedBy,
            ContextPath = ContextPath,
            Timestamp = DateTime.UtcNow.AddMinutes(-5),
            Type = ThreadMessageType.ExecutedInput,
            Status = ThreadMessageStatus.Submitted
        };

        var seededThread = new MeshNode(speakingId, threadNs)
        {
            Name = "Orphaned StartingExecution claim",
            NodeType = ThreadNodeType.NodeType,
            MainNode = ContextPath,
            CreatedBy = CreatedBy,
            Content = new MeshThread
            {
                CreatedBy = CreatedBy,
                // 🚨 The wedge shape: the CLAIM landed (StartingExecution) with the
                // message still queued, but the COMMIT never did — so NO cell and
                // NO ActiveMessageId (the commit is what stamps both).
                Status = ThreadExecutionStatus.StartingExecution,
                ActiveMessageId = null,
                ExecutionStartedAt = null,
                UserMessageIds = ImmutableList.Create(userMsgId),
                PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                    .Add(userMsgId, pendingMessage)
            }
        };

        await adapter.Write(seededThread, options).FirstAsync().ToTask(ct);
        Output.WriteLine($"Seeded orphaned StartingExecution claim at {threadPath}.");

        // ── Activate the thread hub cold ─────────────────────────────────────
        // Subscribing to the node stream is exactly what a portal-side access
        // (open the thread page) does — it activates the per-node hub, which
        // reads the persisted StartingExecution state and runs recovery.
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // First: the loaded state must surface (proof the hub activated cold and
        // read the persisted orphaned claim).
        var loaded = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(30.Seconds()).Match(t => t is not null);
        loaded!.PendingUserMessages.Should().ContainKey(userMsgId,
            "the queued message must still be present on the orphaned claim");
        Output.WriteLine($"Thread hub activated cold; status={loaded.Status}.");

        // ── The round must run to completion ─────────────────────────────────
        // Recovery rolls the orphaned StartingExecution claim back to Idle; the
        // submission watcher re-claims the queued message as a fresh, live
        // transition the _Exec round watcher observes, and the Echo round runs.
        var doneState = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Should().Within(60.Seconds())
            .Match(t => t is { IsExecuting: false } && t.Messages.Count >= 2);

        doneState!.PendingUserMessages.Should().BeEmpty(
            "the queued message must have been drained into the round");
        doneState.Status.Should().Be(ThreadExecutionStatus.Idle,
            "the recovered round must terminate cleanly at Idle, not stay wedged at StartingExecution");
        doneState.ExecutionStartedAt.Should().BeNull("started-at is cleared on completion");

        // The Echo agent's reply must have reached a response cell.
        var lastMsgId = doneState.Messages[^1];
        var finalMessage = await ThreadFlow.ReadMessage(client, threadPath, lastMsgId,
            m => m.CompletedAt != null && !string.IsNullOrEmpty(m.Text),
            timeout: 15.Seconds()).Should().Within(15.Seconds()).Emit();
        finalMessage.Text.Should().Contain("I received",
            "the recovered round must actually execute the Echo agent, not just settle to Idle");

        // ── 🚨 Revert-proven pin ─────────────────────────────────────────────
        // The new StartingExecution-orphaned-claim recovery branch in
        // InitializeThreadLifecycle emits this distinctive Warning the instant it
        // reads the loaded StartingExecution+no-cell state on activation. WITHOUT
        // the fix, StartingExecution falls through to the `default` no-op and this
        // warning is never emitted — this assertion is the deterministic proof
        // the fix ran (the completion above is masked by the in-process _Exec
        // watcher, so it cannot pin the fix on its own).
        _logCapture.Contains("orphaned StartingExecution claim").Should().BeTrue(
            "InitializeThreadLifecycle must run the #539 orphaned-claim recovery branch on activation " +
            "(emit the 'recovered orphaned StartingExecution claim' warning and roll the claim back to Idle); " +
            "without the fix StartingExecution is a no-op on activation and the round wedges on the distributed portal.");
    }

    #region Capturing logger

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public bool Contains(string substring) =>
            _messages.Any(m => m.Contains(substring, StringComparison.Ordinal));
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    sink.Enqueue(formatter(state, exception));
            }
        }
    }

    #endregion

    #region Echo IChatClient + factory (same shape as IsExecutingLifecycleTest)

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
