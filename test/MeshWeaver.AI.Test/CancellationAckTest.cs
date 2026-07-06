#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// #321 (Stop not acknowledged immediately): pressing Stop writes
/// <c>RequestedStatus = Cancelled</c> to the thread node, which the round's own CTS
/// self-cancel tears down only after the in-flight tool call finishes/timeouts (~30s).
/// The fix stamps <see cref="MeshThread.ExecutionStatus"/> =
/// <see cref="ThreadExecution.CancellationRequestedStatus"/> the instant the cancel is
/// observed while executing, so the UI confirms the Stop was registered instead of freezing
/// on the previous tool-call status.
///
/// <para>Model-free: a fake <see cref="IChatClientFactory"/> yields one token and then hangs
/// until its cancellation token fires — no live language model, but a real round that reaches
/// <see cref="ThreadExecutionStatus.Executing"/> so the cancellation watcher's ack path runs.</para>
/// </summary>
public class CancellationAckTest : AITestBase
{
    public CancellationAckTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new HangingChatClientFactory());
                return services;
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public async Task Stop_ImmediatelyStampsCancellationAck_BeforeRoundTearsDown()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Cancellation Ack Test Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread { CreatedBy = "rbuergi@systemorph.com" }
        }).Should().Emit();

        GetClient().SubmitMessage(
            threadPath, "trigger the hanging stream",
            modelName: "hanging-model", createdBy: "rbuergi@systemorph.com");

        // Wait until the round is genuinely executing (hung mid-stream) so the cancellation
        // watcher's `IsExecuting` filter — which gates the ack — matches.
        await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n?.Content as MeshThread)
            .Where(t => t is not null)
            .Should().Within(TimeSpan.FromSeconds(45))
            .Match(t => t!.Status.IsExecuting() && t.ActiveMessageId is not null);

        // Subscribe to the ack BEFORE pressing Stop (ToTask subscribes eagerly), so a transient
        // ExecutionStatus == ack emission is never missed even though the terminal Cancelled write
        // clears ExecutionStatus back to null shortly afterwards.
        var ackObserved = Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => (n?.Content as MeshThread)?.ExecutionStatus)
            .Where(es => es == ThreadExecution.CancellationRequestedStatus)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(ct);

        // Press Stop: the canonical thread-cancel is RequestedStatus = Cancelled on the node.
        Mesh.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
                node?.Content is MeshThread t
                    ? node with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                    : node!)
            .Subscribe(_ => { }, _ => { });

        var ack = await ackObserved;
        ack.Should().Be(ThreadExecution.CancellationRequestedStatus,
            "the Stop must be acknowledged on ExecutionStatus immediately, before the round " +
            "finishes draining the in-flight operation");

        // And the round still settles into a terminal, non-executing state (no wedge).
        await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n?.Content as MeshThread)
            .Where(t => t is not null)
            .Should().Within(TimeSpan.FromSeconds(30))
            .Match(t => !t!.Status.IsExecuting());
    }

    // ─── Chat client that yields one token, then hangs until cancelled ───
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
            // Hang until the round's linked CTS fires — the same shape a hung HTTP read
            // surfaces when its token is cancelled. This is the in-flight tool call that the
            // Stop must be acknowledged over.
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
