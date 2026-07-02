#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the #147 streaming wall-clock cap: an LLM stream that yields one token and then
/// NEVER produces another update (and never faults) is the primary wedge defect — the
/// awaiting continuation parked forever, occupying the grain's scheduler slot with no
/// recovery short of a pod restart. With the cap, the round's linked
/// CancellationTokenSource fires at <see cref="AiStreamingLimits.MaxStreamingDuration"/>
/// and the round MUST terminate as a graceful ERROR: response cell
/// <see cref="ThreadMessageStatus.Error"/> naming the timeout, thread back to
/// <see cref="ThreadExecutionStatus.Idle"/> — NEVER a silent hang, and NEVER
/// masquerading as a user cancel (<see cref="ThreadExecutionStatus.Cancelled"/> is
/// reserved for the Stop button / hub disposal via executionCts).
/// </summary>
public class StreamingTimeoutTest : AITestBase
{
    public StreamingTimeoutTest(ITestOutputHelper output) : base(output) { }

    /// <summary>
    /// Short cap so the test pins the timeout deterministically. The clock starts inside
    /// the round's I/O-pool lambda (after init / history load), so this bounds only the
    /// streaming wait itself. Production default is 30 min (ThreadExecution.MaxStreamingDuration).
    /// </summary>
    private static readonly TimeSpan TestCap = TimeSpan.FromSeconds(3);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new HangingChatClientFactory());
                // Mesh-scoped override (instance singleton, dies with the mesh — never a
                // mutable static): the production 30-min cap shrunk to seconds.
                services.AddSingleton(new AiStreamingLimits(TestCap));
                return services;
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public async Task StreamHangsForever_RoundEndsWithTimeoutError_NotSilentWedge()
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Streaming Timeout Test Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread { CreatedBy = "rbuergi@systemorph.com" }
        }).Should().Emit();

        var client = GetClient();
        client.SubmitMessage(
            threadPath, "trigger the hanging stream",
            modelName: "hanging-model", createdBy: "rbuergi@systemorph.com");

        // The round must reach the terminal state via the wall-clock cap: TestCap after
        // the streaming lambda arms the linked CTS, the OperationCanceledException
        // resumes the parked continuation, is converted to TimeoutException, and the
        // standard error path lands the thread back at Idle. Budget = cap + generous
        // slack for init + the terminal writes under CI load — far below the 90 s
        // stuck-round watchdog grace, proving it's the cap (not the watchdog) rescuing.
        var terminal = await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n?.Content as MeshThread)
            .Where(t => t is not null)
            .Should().Within(TimeSpan.FromSeconds(45))
            .Match(t => t!.Status == ThreadExecutionStatus.Idle && t.IngestedMessageIds.Count >= 1);

        terminal!.Status.Should().Be(ThreadExecutionStatus.Idle,
            "a timed-out round is a round ERROR landing at Idle — Cancelled is reserved " +
            "for a requested cancel (Stop button / hub disposal), and a node parked at " +
            "Executing is exactly the #147 wedge");
        terminal.ActiveMessageId.Should().BeNull("the timed-out round must release its active cell");

        // The response cell carries the timeout as a graceful, explanatory ERROR.
        terminal.Messages.Should().HaveCountGreaterThanOrEqualTo(2, "user cell + error response cell");
        var responseCellPath = $"{threadPath}/{terminal.Messages[^1]}";
        var cell = await Mesh.GetWorkspace().GetMeshNodeStream(responseCellPath)
            .Select(n => n?.Content as ThreadMessage)
            .Should().Within(TimeSpan.FromSeconds(10))
            .Match(m => m is { Status: ThreadMessageStatus.Error });
        cell!.Text.Should().Contain("exceeded the maximum round duration",
            "the timeout's message must surface on the error cell so the user sees WHY " +
            "the round was aborted — never a silent swallow, never a bare 'operation was canceled'");
    }

    // ─── Chat client that yields one token, then hangs forever (until cancelled) ───

    /// <summary>
    /// Reproduces the #147 defect shape: a genuinely-hung model endpoint. One real token
    /// (the round is past the Executing flip, genuinely streaming), then an await that
    /// only the CancellationToken can complete — exactly like an LLM connection that
    /// stays open but never sends another byte.
    /// </summary>
    private sealed class HangingChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("HangingProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("non-streaming path must not be used");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial ");
            // Hang until the round's linked timeout CTS fires. Task.Delay throws
            // TaskCanceledException (an OperationCanceledException) — the same shape a
            // hung HTTP read surfaces when its token is cancelled.
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class HangingChatClientFactory : IChatClientFactory
    {
        public string Name => "HangingFactory";
        public IReadOnlyList<string> Models => ["hanging-model"];
        public int Order => 0;

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(
                chatClient: new HangingChatClient(),
                instructions: config.Instructions ?? "You hang mid-stream.",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [],
                loggerFactory: null,
                services: null);

        public Task<Microsoft.Agents.AI.ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }
}
