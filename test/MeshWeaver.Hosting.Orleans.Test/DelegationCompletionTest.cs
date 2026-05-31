using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Microsoft.Agents.AI;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Submit a user message via <c>ThreadSubmission.Submit</c> (which internally
/// routes through <c>workspace.GetMeshNodeStream(threadPath).Update</c> —
/// the only sanctioned mutation API, see CLAUDE.md). Observe completion via
/// the thread + response-cell streams, the same primitive the GUI databinds
/// to. No <c>SubmitMessageResponse</c>, no completion callbacks.
/// </summary>
public class DelegationCompletionTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"completion-{name}-{Guid.NewGuid():N}", "TestUser");

    /// <summary>
    /// User submission produces a user-message cell + an agent-response cell;
    /// the agent eventually writes terminal text into the response cell. All
    /// observed via the response cell's MeshNode stream.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void SubmitMessage_ResponseCellGetsTerminalText()
    {
        var client = GetClient();

        // 1. Create thread
        var response = client
            .Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode("TestUser", "Completion test", "TestUser")),
                o => o.WithTarget(new Address("TestUser")))
            .Should().Within(45.Seconds()).Emit();
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        var threadPath = response.Message.Node!.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        // 2. Submit via the canonical API (workspace.SubmitMessage → AppendUserInput
        //    → stream.Update on the thread node).
        client.SubmitMessage(
            threadPath,
            "Test completion notification",
            contextPath: "TestUser");

        // 3. Observe the thread stream until BOTH messages exist.
        //    GetMeshNodeStream(path) is the canonical API — auto-dispatches own → local → remote.
        var workspace = client.GetWorkspace();
        var msgIds = workspace.GetMeshNodeStream(threadPath)
            .Select(node => (node?.Content as MeshThread)?.Messages
                            ?? System.Collections.Immutable.ImmutableList<string>.Empty)
            .Should().Within(45.Seconds()).Match(ids => ids.Count >= 2);
        var responseMsgId = msgIds[1];
        var responsePath = $"{threadPath}/{responseMsgId}";
        Output.WriteLine($"Response message cell: {responsePath}");

        // 4. Observe the response cell's stream until it reaches Completed with
        //    non-empty Text. This is "execution completed" in the stream-only
        //    world — replaces the obsolete SubmitMessageResponse(ExecutionCompleted).
        var finalMsg = workspace.GetMeshNodeStream(responsePath)
            .Select(node => node?.Content as ThreadMessage)
            .Should().Within(45.Seconds()).Match(m => m is { Status: ThreadMessageStatus.Completed } && !string.IsNullOrEmpty(m.Text));

        finalMsg!.Status.Should().Be(ThreadMessageStatus.Completed,
            "response cell reaches terminal Status when agent finishes");
        finalMsg.Text.Should().NotBeNullOrEmpty("agent's response text lives on the response cell");
        Output.WriteLine($"Verified: response cell text length = {finalMsg.Text!.Length}");

        // 5. Completion notification — EmitCompletionNotification writes a Notification
        //    satellite at {threadPath}/_Notification/{id} (routes to "notifications"
        //    table per StandardTableMappings). The bell-icon UI in the portal
        //    databinds to notifications under the user; we assert the same shape.
        //    Reactive children-query poll — no await, no Task.Delay.
        var meshService = client.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>();
        var notificationQuery = $"path:{threadPath}/_Notification scope:children nodeType:Notification";
        meshService.ObserveQuery<MeshNode>(new MeshQueryRequest { Query = notificationQuery })
            .Should().Within(20.Seconds()).Match(c => c.Items.Any(n =>
                n.Content is MeshWeaver.Mesh.Notification notif && notif.TargetNodePath == threadPath));
        Output.WriteLine("Verified: completion notification appeared in user's bell");

        // 6. Dedicated Summary on ThreadMessage AND Thread. ExecuteMessageAsync
        //    parses <summary>...</summary> from the agent's response (or falls
        //    back to finalText) and writes the same string atomically with
        //    Status flips — Thread.Summary at Status=Idle, ThreadMessage.Summary
        //    at Status=Completed. The delegation tool reads Thread.Summary as
        //    the tool-call result; the UI shows ThreadMessage.Summary as a
        //    one-line digest of the verbose response.
        finalMsg.Summary.Should().NotBeNull(
            "response cell must carry a dedicated Summary populated at terminal status — " +
            "the delegation tool returns this (not the verbose Text) to the parent agent");
        finalMsg.Summary.Should().NotBeNullOrEmpty("summary must contain text");
        Output.WriteLine($"Verified: response cell Summary length = {finalMsg.Summary!.Length}");

        var threadAtIdle = workspace.GetMeshNodeStream(threadPath)
            .Select(node => node?.Content as MeshThread)
            .Should().Within(10.Seconds()).Match(t => t is { Status: MeshWeaver.AI.ThreadExecutionStatus.Idle }
                        && !string.IsNullOrEmpty(t.Summary));
        threadAtIdle!.Summary.Should().NotBeNullOrEmpty(
            "Thread.Summary must be populated atomically with Status=Idle so a delegating " +
            "parent observing the sub-thread sees the summary in the same emission as the Idle flip");
        threadAtIdle.Summary.Should().Be(finalMsg.Summary,
            "Thread.Summary and ThreadMessage.Summary should carry the same digest");
        Output.WriteLine($"Verified: thread Summary matches response-cell Summary");
    }

    /// <summary>
    /// Multi-level delegation summary propagation. We simulate the chain at
    /// the data level (without driving a real multi-tool-call LLM): a
    /// "grandparent" thread, a "parent" sub-thread that runs to terminal,
    /// and a "child" sub-sub-thread that runs to terminal. Each writes its
    /// own Summary atomically with Status=Idle. We verify that an observer
    /// of the grandparent can read the parent's Summary, and an observer of
    /// the parent can read the child's Summary — the same primitive the
    /// reactive DelegationTool subscription uses to resolve the TCS.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void DelegationOfDelegation_SummaryPropagatesUp()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // 1. Submit at the grandparent — produces a normal terminal thread
        //    with a Summary. We'll then verify a parent and child below can
        //    propagate their summaries to observers via the same primitive.
        var gpResponse = client
            .Observe(new CreateNodeRequest(
                ThreadNodeType.BuildThreadNode("TestUser", "Grandparent thread", "TestUser")),
                o => o.WithTarget(new Address("TestUser")))
            .Should().Within(45.Seconds()).Emit();
        gpResponse.Message.Success.Should().BeTrue(gpResponse.Message.Error ?? "");
        var gpPath = gpResponse.Message.Node!.Path!;
        Output.WriteLine($"Grandparent: {gpPath}");
        client.SubmitMessage(
            gpPath,
            "First level work",
            contextPath: "TestUser");
        var gpFinal = workspace.GetMeshNodeStream(gpPath)
            .Select(node => node?.Content as MeshThread)
            .Should().Within(45.Seconds()).Match(t => t is { Status: ThreadExecutionStatus.Idle } && !string.IsNullOrEmpty(t.Summary));
        gpFinal!.Summary.Should().NotBeNullOrEmpty(
            "Level-1 thread must write Summary atomically with Status=Idle so an observer " +
            "(e.g. a delegating parent) can read it in the same emission as the Idle flip");
        Output.WriteLine($"Level-1 Summary: {gpFinal.Summary![..Math.Min(80, gpFinal.Summary!.Length)]}");

        // 2. Spawn a child thread submission and observe its terminal Summary
        //    the same way — proving the propagation chain works at any depth.
        //    This is the Scan-based "Running → Idle" subscription the
        //    DelegationTool uses, applied identically here in a test.
        var childResponse = client
            .Observe(new CreateNodeRequest(
                ThreadNodeType.BuildThreadNode("TestUser", "Child thread", "TestUser")),
                o => o.WithTarget(new Address("TestUser")))
            .Should().Within(45.Seconds()).Emit();
        childResponse.Message.Success.Should().BeTrue(childResponse.Message.Error ?? "");
        var childPath = childResponse.Message.Node!.Path!;
        Output.WriteLine($"Child: {childPath}");
        client.SubmitMessage(
            childPath,
            "Second level work",
            contextPath: "TestUser");
        var childRunningToIdle = workspace.GetMeshNodeStream(childPath)
            .Select(node => node?.Content as MeshThread)
            .Where(t => t is not null)
            .Scan((sawRunning: false, terminal: (MeshThread?)null), (state, t) =>
            {
                if (t!.Status is ThreadExecutionStatus.Executing
                              or ThreadExecutionStatus.StartingExecution)
                    return (true, null);
                if (state.sawRunning && t.Status == ThreadExecutionStatus.Idle
                                     && !string.IsNullOrEmpty(t.Summary))
                    return (state.sawRunning, t);
                return state;
            })
            .Should().Within(45.Seconds()).Match(s => s.terminal is not null);
        childRunningToIdle.terminal!.Summary.Should().NotBeNullOrEmpty(
            "Level-2 (child) thread reactive Running→Idle subscription must surface the " +
            "child's Summary — same shape the DelegationTool uses for sub-thread tool-call results");
        Output.WriteLine($"Level-2 Summary: {childRunningToIdle.terminal.Summary![..Math.Min(80, childRunningToIdle.terminal.Summary!.Length)]}");
    }

    /// <summary>
    /// Steers the test agent's streaming response to include an explicit
    /// <c>&lt;summary&gt;EXPECTED&lt;/summary&gt;</c> block and verifies that
    /// ExecuteMessageAsync's parser extracts the inner text into
    /// <c>Thread.Summary</c> + <c>ThreadMessage.Summary</c>, AND strips the
    /// marker from the user-visible <c>ThreadMessage.Text</c>.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void SummaryBlock_ParsedFromAgentResponse_AndStrippedFromText()
    {
        const string expectedSummary = "Greeting acknowledged.";
        const string visibleBody = "Hello! How can I help you today?";
        var streamedResponse = $"{visibleBody} <summary>{expectedSummary}</summary>";

        // Swap the cluster's chat client factory to one that streams our crafted
        // response. SwappableFactory is process-wide — restore the default
        // FakeChatClientFactory in a try/finally so subsequent tests aren't affected.
        SharedOrleansFixture.SwappableFactory.SetInner(new SteerableFakeChatClientFactory(streamedResponse));
        try
        {
            var client = GetClient();
            var workspace = client.GetWorkspace();

            var createResp = client
                .Observe(new CreateNodeRequest(
                    ThreadNodeType.BuildThreadNode("TestUser", "Summary block test", "TestUser")),
                    o => o.WithTarget(new Address("TestUser")))
                .Should().Within(45.Seconds()).Emit();
            createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
            var threadPath = createResp.Message.Node!.Path!;

            client.SubmitMessage(
                threadPath,
                "Hi",
                contextPath: "TestUser");

            var threadAtIdle = workspace.GetMeshNodeStream(threadPath)
                .Select(node => node?.Content as MeshThread)
                .Should().Within(45.Seconds()).Match(t => t is { Status: MeshWeaver.AI.ThreadExecutionStatus.Idle }
                            && !string.IsNullOrEmpty(t.Summary));

            threadAtIdle!.Summary.Should().Be(expectedSummary,
                "ExecuteMessageAsync must parse <summary>...</summary> from the agent " +
                "response and write the inner text as Thread.Summary atomically with Status=Idle.");

            // Response cell carries the same Summary and clean Text (marker stripped).
            var responseMsgId = threadAtIdle.Messages.Last();
            var responsePath = $"{threadPath}/{responseMsgId}";
            var responseMsg = workspace.GetMeshNodeStream(responsePath)
                .Select(node => node?.Content as ThreadMessage)
                .Should().Within(45.Seconds()).Match(m => m is { Status: ThreadMessageStatus.Completed } && !string.IsNullOrEmpty(m.Summary));
            responseMsg!.Summary.Should().Be(expectedSummary,
                "ThreadMessage.Summary on the response cell must match Thread.Summary");
            responseMsg.Text.Should().NotContain("<summary>",
                "the <summary> marker must be stripped from the user-visible Text");
            responseMsg.Text.Should().NotContain(expectedSummary,
                "the summary inner text must be stripped from Text too (lives only in Summary)");
            responseMsg.Text.Should().Contain(visibleBody.Trim(),
                "the agent's user-visible response body must survive the strip");

            Output.WriteLine($"Verified: Summary='{threadAtIdle.Summary}', Text='{responseMsg.Text}'");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }
}

/// <summary>
/// A FakeChatClientFactory whose response text is set per-instance, so a test
/// can steer the streamed assistant output (including a trailing
/// <c>&lt;summary&gt;</c> block) and assert the framework's parsing behavior.
/// </summary>
internal class SteerableFakeChatClientFactory(string response) : IChatClientFactory
{
    public string Name => "SteerableFakeFactory";
    public IReadOnlyList<string> Models => ["fake-model"];
    public int Order => 0;

    public ChatClientAgent CreateAgent(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => new(chatClient: new FakeChatClient(response),
            instructions: config.Instructions ?? "Test assistant.",
            name: config.Id, description: config.Description ?? config.Id,
            tools: [], loggerFactory: null, services: null);

    public Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
}
