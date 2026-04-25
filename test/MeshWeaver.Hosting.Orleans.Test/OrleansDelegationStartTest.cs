using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans test: delegation sub-thread starts execution automatically.
/// Simulates the exact delegation flow:
/// 1. Create user cell
/// 2. Create response cell
/// 3. Create thread with Messages + IsExecuting=true + PendingUserMessage
/// 4. WatchForExecution triggers â†’ starts streaming â†’ response cell gets text
/// </summary>
[Collection(nameof(OrleansClusterCollection))]
public class OrleansDelegationStartTest(SharedOrleansFixture fixture, ITestOutputHelper output) : TestBase(output)
{
    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await fixture.GetClientAsync($"deleg-{name}-{Guid.NewGuid():N}", "Roland");

    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        // Canonical CQRS-correct read via per-node MeshNodeReference reducer.
        var response = await client.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path))).FirstAsync().ToTask(ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(fixture.ClientMesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(fixture.ClientMesh.JsonSerializerOptions);
        return null;
    }

    /// <summary>
    /// Simulates delegation: create cells first, then thread with IsExecuting=true.
    /// WatchForExecution should detect fresh execution and start streaming.
    /// Response cell should have agent text when execution completes.
    /// </summary>
    [Fact]
    public async Task Delegation_CreateCellsThenThread_ExecutionStartsAndCompletes()
    {
        SharedOrleansFixture.SwappableFactory.SetInner(new DelegationEchoChatClientFactory());
        try
        {
            var ct = new CancellationTokenSource(30.Seconds()).Token;
            var client = await GetClientAsync();

            // Create a parent thread first (delegations live under a response message)
            var parentNode = ThreadNodeType.BuildThreadNode("User/Roland", "Parent for delegation test", "Roland");
            var parentResp = await client.Observe(new CreateNodeRequest(parentNode), o => o.WithTarget(new Address("User/Roland"))).FirstAsync().ToTask(ct);
            parentResp.Message.Success.Should().BeTrue(parentResp.Message.Error);
            var parentPath = parentResp.Message.Node!.Path!;
            Output.WriteLine($"Parent thread: {parentPath}");

            // Simulate a response message on the parent (delegation lives under it)
            var parentResponseId = Guid.NewGuid().ToString("N")[..8];
            await client.Observe(new CreateNodeRequest(new MeshNode(parentResponseId, parentPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage { Role = "assistant", Text = "", Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse }
            }), o => o.WithTarget(new Address(parentPath))).FirstAsync().ToTask(ct);
            var parentMsgPath = $"{parentPath}/{parentResponseId}";

            // Now simulate delegation: create cells, then thread (exact ChatClientAgentFactory flow)
            var (subThreadNode, userMsgId, responseMsgId) = ThreadNodeType.BuildThreadWithMessages(
                parentMsgPath, "Delegation task: do something", createdBy: "Roland", agentName: "Worker");
            subThreadNode = subThreadNode with { MainNode = "User/Roland" };
            var subThreadPath = subThreadNode.Path!;
            var responsePath = $"{subThreadPath}/{responseMsgId}";
            Output.WriteLine($"Sub-thread: {subThreadPath}, user={userMsgId}, response={responseMsgId}");

            // Step 1: Create user cell
            var userCellResp = await client.Observe(new CreateNodeRequest(new MeshNode(userMsgId, subThreadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage
                {
                    Role = "user", Text = "Delegation task: do something", Timestamp = DateTime.UtcNow,
                    Type = ThreadMessageType.ExecutedInput, CreatedBy = "Roland"
                }
            }), o => o.WithTarget(new Address(subThreadPath))).FirstAsync().ToTask(ct);
            Output.WriteLine($"User cell created: success={userCellResp.Message.Success}");

            // Step 2: Create response cell
            var responseCellResp = await client.Observe(new CreateNodeRequest(new MeshNode(responseMsgId, subThreadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = "User/Roland",
                Content = new ThreadMessage
                {
                    Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                    Type = ThreadMessageType.AgentResponse, AgentName = "Worker"
                }
            }), o => o.WithTarget(new Address(subThreadPath))).FirstAsync().ToTask(ct);
            Output.WriteLine($"Response cell created: success={responseCellResp.Message.Success}");

            // Step 3: Create thread with IsExecuting=true (triggers WatchForExecution)
            var threadResp = await client.Observe(new CreateNodeRequest(subThreadNode), o => o.WithTarget(new Address(parentMsgPath))).FirstAsync().ToTask(ct);
            threadResp.Message.Success.Should().BeTrue(threadResp.Message.Error);
            Output.WriteLine("Sub-thread created â€” WatchForExecution should trigger");

            // Step 4: Poll for execution to complete
            for (var i = 0; i < 60; i++)
            {
                var thread = await GetHubContentAsync<MeshThread>(client, subThreadPath, ct);
                if (thread is { IsExecuting: false })
                {
                    Output.WriteLine($"Execution complete after {i * 500}ms");

                    var responseMsg = await GetHubContentAsync<ThreadMessage>(client, responsePath, ct);
                    responseMsg.Should().NotBeNull("response cell must exist");
                    responseMsg!.Text.Should().NotBeNullOrEmpty("agent must have written response");
                    Output.WriteLine($"Response: {responseMsg.Text[..Math.Min(100, responseMsg.Text.Length)]}");
                    Output.WriteLine("PASSED");
                    return;
                }
                await Task.Delay(500, ct);
            }
            throw new TimeoutException("Delegation execution did not complete");
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
