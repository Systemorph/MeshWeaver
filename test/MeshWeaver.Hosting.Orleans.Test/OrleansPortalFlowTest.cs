using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans integration: portal/side-panel chat flow end-to-end.
/// Uses <see cref="ThreadFlow"/> â€” the GUI-shaped static primitives â€” so
/// the test stays in lockstep with what the user actually sees. NO inline
/// re-implementation of the flow.
///
/// <para>Previously this test exercised the legacy "client pre-creates user +
/// response cells, then posts SubmitMessageRequest with explicit
/// UserMessageId + ResponseMessageId" flow. That path is dead:
/// <see cref="ThreadExecution.HandleSubmitMessage"/> now routes through
/// <see cref="ThreadInput.AppendUserInput"/>; the submission watcher
/// allocates its OWN cell ids via DispatchRound. The test's pre-created
/// cells stayed orphaned and the poll-on-pre-created-responseMsgId waited
/// indefinitely for text that never arrived (CI failure 2026-05-23).
/// Rewritten 2026-05-24 to use ThreadFlow + read the server-allocated
/// cell ids.</para>
/// </summary>
public class OrleansPortalFlowTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await base.GetClientAsync($"portal-{name}-{Guid.NewGuid():N}", "TestUser");

    [Fact]
    public async Task PortalFlow_CreateThread_CreateCells_Submit_ExecutionCompletes()
    {
        SharedOrleansFixture.SwappableFactory.SetInner(new PortalFlowEchoChatClientFactory());
        try
        {
            var ct = new CancellationTokenSource(60.Seconds()).Token;
            var client = await GetClientAsync();

            // Step 1: Create thread
            var threadNode = ThreadNodeType.BuildThreadNode("TestUser", "Portal flow Orleans test", "TestUser");
            var createResp = await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("TestUser"))).FirstAsync().ToTask(ct);
            createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
            var threadPath = createResp.Message.Node!.Path!;
            Output.WriteLine($"Thread: {threadPath}");

            // Step 2: Submit via the GUI path + wait for the round to complete.
            // ThreadFlow.SubmitAndWait returns the response message id â€”
            // the server-allocated cell at Messages[^1] after IsExecuting flips
            // back to false.
            var responseMsgId = await ThreadFlow.SubmitAndWait(
                client, threadPath, "Portal flow Orleans test",
                contextPath: "TestUser",
                timeout: 50.Seconds()).FirstAsync().ToTask(ct);
            Output.WriteLine($"Round complete. Response cell: {responseMsgId}");

            // Step 3: Verify the cells. Same workspace.GetMeshNodeStream
            // primitive â€” read via ThreadFlow.ReadMessage.
            var finalThread = await ThreadFlow.ReadThread(
                client, threadPath,
                t => t.Messages.Count >= 2).FirstAsync().ToTask(ct);
            Output.WriteLine($"Messages: [{string.Join(", ", finalThread.Messages)}]");

            var userMsg = await ThreadFlow.ReadMessage(
                client, threadPath, finalThread.Messages[0]).FirstAsync().ToTask(ct);
            userMsg.Text.Should().Be("Portal flow Orleans test");
            Output.WriteLine($"User cell: '{userMsg.Text}'");

            var responseMsg = await ThreadFlow.ReadMessage(
                client, threadPath, finalThread.Messages[^1]).FirstAsync().ToTask(ct);
            responseMsg.Text.Should().NotBeNullOrEmpty("agent must have written response");
            Output.WriteLine($"Response: {responseMsg.Text[..Math.Min(100, responseMsg.Text.Length)]}");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }

    /// <summary>
    /// Mimics real user behavior: type and submit several messages rapidly
    /// in succession. The user doesn't wait for each round to finish â€” they
    /// pile up. The submission watcher's contract: every pending message
    /// gets ingested into <see cref="MeshThread.Messages"/> with a response.
    ///
    /// Flow:
    /// 1. Submit msg1 â†’ claim round 1, dispatch
    /// 2. Submit msg2 immediately (lands in <see cref="MeshThread.PendingUserMessages"/>
    ///    while round 1 is running)
    /// 3. Submit msg3 immediately (joins the queue)
    /// 4. Round 1 completes â†’ watcher dispatches round 2 with the entire
    ///    pending queue drained ([msg2, msg3] share one response cell per
    ///    <see cref="ThreadSubmission.PlanNextRound"/> semantics)
    /// 5. Final state: every submitted text appears as a satellite cell,
    ///    thread is Idle, all UserMessageIds are ingested
    /// </summary>
    [Fact]
    public async Task RapidSubmits_PileUpAndAllIngest()
    {
        SharedOrleansFixture.SwappableFactory.SetInner(new PortalFlowEchoChatClientFactory());
        try
        {
            var ct = new CancellationTokenSource(60.Seconds()).Token;
            var client = await GetClientAsync();

            var threadNode = ThreadNodeType.BuildThreadNode("TestUser", "Rapid submits test", "TestUser");
            var createResp = await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("TestUser"))).FirstAsync().ToTask(ct);
            var threadPath = createResp.Message.Node!.Path!;
            Output.WriteLine($"Thread: {threadPath}");

            // Rapid-fire three submits â€” mimics a user typing follow-ups
            // without waiting for the agent. Submits 2 + 3 should land in
            // PendingUserMessages while round 1 is still running, then
            // drain into round 2 as a single multi-message round.
            string[] userTexts = ["First question", "Second question", "Third question"];
            foreach (var text in userTexts)
                client.SubmitMessage(threadPath, text, contextPath: "TestUser");
            Output.WriteLine($"Submitted {userTexts.Length} messages rapidly");

            // Wait for the thread to settle: Idle + all user messages ingested.
            // Single workspace.GetMeshNodeStream subscription (via ThreadFlow)
            // filters for the final state. Ingestion = UserMessageIds count
            // matches IngestedMessageIds count and equals the submitted count.
            var finalThread = await ThreadFlow.ReadThread(
                    client, threadPath,
                    t => !t.IsExecuting
                         && t.UserMessageIds.Count >= userTexts.Length
                         && t.IngestedMessageIds.Count >= userTexts.Length,
                    timeout: 45.Seconds())
                .FirstAsync().ToTask(ct);

            Output.WriteLine($"Settled. Messages: [{string.Join(", ", finalThread.Messages)}]");
            Output.WriteLine($"UserMessageIds: [{string.Join(", ", finalThread.UserMessageIds)}]");

            // Verify every submitted text is present in the satellite cells.
            // Reactive Merge across the user-cell streams â€” each stream is the
            // GUI primitive ThreadFlow.ReadMessage (workspace.GetMeshNodeStream
            // + Where + Take(1)) which completes once the cell text is present.
            // .ToList() aggregates after every stream completes; .FirstAsync()
            // takes that aggregated list, .ToTask(ct) bridges at the test edge.
            var userCells = await finalThread.UserMessageIds
                .Select(id => ThreadFlow.ReadMessage(client, threadPath, id))
                .Merge()
                .ToList()
                .FirstAsync()
                .ToTask(ct);

            userCells.Should().HaveCount(userTexts.Length);
            userCells.Select(c => c.Text).Should().BeEquivalentTo(userTexts, client.JsonSerializerOptions);
            Output.WriteLine($"All {userTexts.Length} user submissions ingested with text");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }

    #region Echo LLM

    private class PortalFlowEchoChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("PortalFlowEcho");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Echo: {messages.Count()} messages")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"Portal echo: received {messages.Count()} messages.");
            await Task.Delay(10, ct);
        }

        public object? GetService(Type serviceType, object? key = null) => serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class PortalFlowEchoChatClientFactory : IChatClientFactory
    {
        public string Name => "PortalFlowEchoFactory";
        public IReadOnlyList<string> Models => ["echo-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => new(chatClient: new PortalFlowEchoChatClient(), instructions: "Echo agent.",
                name: config.Id, description: config.Description ?? "",
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}
