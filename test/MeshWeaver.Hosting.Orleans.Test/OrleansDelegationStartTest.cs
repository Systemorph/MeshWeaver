using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
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

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans test: delegation sub-thread starts execution automatically.
/// Simulates the exact delegation flow:
/// 1. Create user cell
/// 2. Create response cell
/// 3. Create thread with Messages + IsExecuting=true + PendingUserMessage
/// 4. WatchForExecution triggers -> starts streaming -> response cell gets text
///
/// 🚨 Test is <c>void</c> + reactive assertions (no <c>async</c>/<c>await</c>):
/// blocking inside an async test deadlocks the in-process hub scheduler — the
/// agent's streaming execution shares the process and its continuations are
/// starved by the captured async SynchronizationContext. See
/// ReactiveTestAssertions.md §2 + ObservableAssertions remarks.
/// </summary>
public class OrleansDelegationStartTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"deleg-{name}-{Guid.NewGuid():N}", "TestUser");

    private IMessageDelivery<CreateNodeResponse> CreateNode(IMessageHub client, MeshNode node, string targetAddress)
        => client.Observe(new CreateNodeRequest(node), o => o.WithTarget(new Address(targetAddress)))
            .Should().Within(30.Seconds()).Emit();

    /// <summary>
    /// Reactive single-node content read via the canonical
    /// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace, string)"/>
    /// path. Returns an <see cref="IObservable{T}"/> the caller asserts on with
    /// <c>.Should().Match(...)</c>.
    /// </summary>
    private IObservable<T?> GetHubContent<T>(IMessageHub client, string path) where T : class
        => client.GetWorkspace().GetMeshNodeStream(path)
            .Select(node =>
            {
                if (node?.Content is T typed) return typed;
                if (node?.Content is JsonElement contentJe)
                    return contentJe.Deserialize<T>(client.JsonSerializerOptions);
                return null;
            });

    /// <summary>
    /// Simulates delegation: create cells first, then thread with IsExecuting=true.
    /// WatchForExecution should detect fresh execution and start streaming.
    /// Response cell should have agent text when execution completes.
    /// </summary>
    [Fact]
    public void Delegation_CreateCellsThenThread_ExecutionStartsAndCompletes()
    {
        SharedOrleansFixture.SwappableFactory.SetInner(new DelegationEchoChatClientFactory());
        try
        {
            var client = GetClient();

            // Create a parent thread first (delegations live under a response message)
            var parentNode = ThreadNodeType.BuildThreadNode("TestUser", "Parent for delegation test", "TestUser");
            var parentResp = CreateNode(client, parentNode, "TestUser");
            parentResp.Message.Success.Should().BeTrue(parentResp.Message.Error ?? "");
            var parentPath = parentResp.Message.Node!.Path!;
            Output.WriteLine($"Parent thread: {parentPath}");

            // Simulate a response message on the parent (delegation lives under it)
            var parentResponseId = Guid.NewGuid().ToString("N")[..8];
            CreateNode(client, new MeshNode(parentResponseId, parentPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "TestUser",
                Content = new ThreadMessage { Role = "assistant", Text = "", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse }
            }, parentPath);
            var parentMsgPath = $"{parentPath}/{parentResponseId}";

            // Now simulate delegation: create the sub-thread (exact ChatClientAgentFactory
            // flow). BuildThreadWithMessages seeds PendingUserMessages at Status=Idle;
            // the submission watcher claims, drains the pending message into Messages,
            // allocates the response cell, and dispatches execution — the test no longer
            // pre-creates the user/response cells (that was the old contract, before
            // DispatchAfterClaim owned cell allocation; responseMsgId is "" now).
            var (subThreadNode, userMsgId, _) = ThreadNodeType.BuildThreadWithMessages(
                parentMsgPath, "Delegation task: do something", createdBy: "TestUser", agentName: "Worker");
            subThreadNode = subThreadNode with { MainNode = "TestUser" };
            var subThreadPath = subThreadNode.Path!;
            Output.WriteLine($"Sub-thread: {subThreadPath}, user={userMsgId}");

            // Create the sub-thread — WatchForExecution claims + dispatches the round.
            var threadResp = CreateNode(client, subThreadNode, parentMsgPath);
            threadResp.Message.Success.Should().BeTrue(threadResp.Message.Error ?? "");
            Output.WriteLine("Sub-thread created — WatchForExecution should trigger");

            // Wait for execution to complete; the response cell id is Messages[1]
            // (allocated by DispatchAfterClaim).
            var settled = GetHubContent<MeshThread>(client, subThreadPath)
                .Should().Within(30.Seconds())
                .Match(t => t is { IsExecuting: false } && t.Messages.Count >= 2);
            Output.WriteLine("Execution complete");

            var responsePath = $"{subThreadPath}/{settled!.Messages[1]}";
            var responseMsg = GetHubContent<ThreadMessage>(client, responsePath)
                .Should().Within(30.Seconds()).Match(m => !string.IsNullOrEmpty(m?.Text));
            responseMsg!.Text.Should().NotBeNullOrEmpty("agent must have written response");
            Output.WriteLine($"Response: {responseMsg.Text![..Math.Min(100, responseMsg.Text.Length)]}");
            Output.WriteLine("PASSED");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }

    #region Echo LLM

    private class DelegationEchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("DelegationEcho");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Delegation echo: {messages.Count()} messages")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"Delegation complete: processed {messages.Count()} messages.");
            await Task.Delay(10, ct);
        }

        public object? GetService(Type serviceType, object? key = null) => serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class DelegationEchoChatClientFactory : IChatClientFactory
    {
        public string Name => "DelegationEchoFactory";
        public IReadOnlyList<string> Models => ["echo-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => new(chatClient: new DelegationEchoChatClient(), instructions: "Delegation echo.",
                name: config.Id, description: config.Description ?? "",
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
