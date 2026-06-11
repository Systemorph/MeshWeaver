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
/// Pins the fault path of the reactive round: a chat client that THROWS from inside the
/// streaming loop — i.e. after the <see cref="ThreadExecutionStatus.Executing"/> flip — must
/// drive the thread to a TERMINAL state (response cell <see cref="ThreadMessageStatus.Error"/>,
/// thread back to <see cref="ThreadExecutionStatus.Idle"/>) deterministically, well inside a
/// budget far below any stuck-round watchdog grace. A faulted round that parks the node at
/// Executing is the wedge class the IObservable round refactor exists to kill.
/// </summary>
public class RoundFaultTerminalStateTest : AITestBase
{
    public RoundFaultTerminalStateTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new ThrowingChatClientFactory());
                return services;
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public void StreamThrows_AfterExecutingFlip_ThreadReachesTerminalState_NoWatchdog()
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Fault Test Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread { CreatedBy = "rbuergi@systemorph.com" }
        }).Should().Emit();

        var client = GetClient();
        client.SubmitMessage(
            threadPath, "trigger the throwing stream",
            modelName: "throwing-model", createdBy: "rbuergi@systemorph.com");

        // The round must reach a terminal state DETERMINISTICALLY via the in-round error
        // handling (catch → Error cell → Idle) or, if the fault escapes, via the watcher's
        // terminal-state writer — never by waiting out a watchdog. Budget is far below any
        // stuck-round grace period.
        var terminal = Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n?.Content as MeshThread)
            .Where(t => t is not null)
            .Should().Within(TimeSpan.FromSeconds(20))
            .Match(t => t!.Status == ThreadExecutionStatus.Idle && t.IngestedMessageIds.Count >= 1);

        terminal!.Status.Should().Be(ThreadExecutionStatus.Idle,
            "a faulted round must land back at Idle — a node parked at Executing is the wedge");
        terminal.ActiveMessageId.Should().BeNull("the faulted round must release its active cell");

        // The response cell carries the error.
        terminal.Messages.Should().HaveCountGreaterThanOrEqualTo(2, "user cell + error response cell");
        var responseCellPath = $"{threadPath}/{terminal.Messages[^1]}";
        var cell = Mesh.GetWorkspace().GetMeshNodeStream(responseCellPath)
            .Select(n => n?.Content as ThreadMessage)
            .Should().Within(TimeSpan.FromSeconds(10))
            .Match(m => m is { Status: ThreadMessageStatus.Error });
        cell!.Text.Should().Contain("boom mid-stream",
            "the fault's message must surface on the error cell so the user sees why the round died");
    }

    // ─── Chat client that throws mid-stream ───

    private sealed class ThrowingChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("ThrowingProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom mid-stream");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // First a real token (the round is genuinely streaming — past the Executing
            // flip and into the loop), THEN the fault.
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial ");
            await Task.Yield();
            throw new InvalidOperationException("boom mid-stream");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class ThrowingChatClientFactory : IChatClientFactory
    {
        public string Name => "ThrowingFactory";
        public IReadOnlyList<string> Models => ["throwing-model"];
        public int Order => 0;

        public Microsoft.Agents.AI.ChatClientAgent CreateAgent(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, Microsoft.Agents.AI.ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(
                chatClient: new ThrowingChatClient(),
                instructions: config.Instructions ?? "You throw mid-stream.",
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
