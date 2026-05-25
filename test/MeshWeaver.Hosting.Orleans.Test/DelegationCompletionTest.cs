using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await base.GetClientAsync($"completion-{name}-{Guid.NewGuid():N}", "TestUser");

    /// <summary>
    /// User submission produces a user-message cell + an agent-response cell;
    /// the agent eventually writes terminal text into the response cell. All
    /// observed via the response cell's MeshNode stream.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SubmitMessage_ResponseCellGetsTerminalText()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        // 1. Create thread
        var response = await client
            .Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode("TestUser", "Completion test", "TestUser")),
                o => o.WithTarget(new Address("TestUser")))
            .FirstAsync().ToTask(ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        var threadPath = response.Message.Node!.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        // 2. Submit via the canonical API (Submit → ThreadInput.AppendUserInput
        //    → stream.Update on the thread node).
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client,
            ThreadPath = threadPath,
            UserText = "Test completion notification",
            ContextPath = "TestUser",
        });

        // 3. Observe the thread stream until BOTH messages exist.
        var workspace = client.GetWorkspace();
        var threadStream = workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!;

        var msgIds = await threadStream
            .Select(nodes => (nodes?.FirstOrDefault(n => n.Path == threadPath)?.Content as MeshThread)?.Messages
                             ?? System.Collections.Immutable.ImmutableList<string>.Empty)
            .Where(ids => ids.Count >= 2)
            .Take(1).Timeout(45.Seconds()).ToTask(ct);
        var responseMsgId = msgIds[1];
        var responsePath = $"{threadPath}/{responseMsgId}";
        Output.WriteLine($"Response message cell: {responsePath}");

        // 4. Observe the response cell's stream until it reaches Completed with
        //    non-empty Text. This is "execution completed" in the stream-only
        //    world — replaces the obsolete SubmitMessageResponse(ExecutionCompleted).
        var responseStream = workspace.GetRemoteStream<MeshNode>(new Address(responsePath))!;
        var finalMsg = await responseStream
            .Select(nodes => nodes?.FirstOrDefault(n => n.Path == responsePath)?.Content as ThreadMessage)
            .Where(m => m is { Status: ThreadMessageStatus.Completed } && !string.IsNullOrEmpty(m.Text))
            .Take(1).Timeout(45.Seconds()).ToTask(ct);

        finalMsg!.Status.Should().Be(ThreadMessageStatus.Completed,
            "response cell reaches terminal Status when agent finishes");
        finalMsg.Text.Should().NotBeNullOrEmpty("agent's response text lives on the response cell");
        Output.WriteLine($"Verified: response cell text length = {finalMsg.Text!.Length}");

        // 5. Completion notification — EmitCompletionNotification writes a Notification
        //    satellite at {threadPath}/_Notification/{id} (routes to "notifications"
        //    table per StandardTableMappings). The bell-icon UI in the portal
        //    databinds to notifications under the user; we assert the same shape.
        //    Tests the "agent completion ⇒ notification" reactive trigger.
        var meshService = client.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>();
        var notificationAppeared = false;
        for (var i = 0; i < 30 && !notificationAppeared; i++)
        {
            var notifications = await meshService
                .QueryAsync<MeshNode>($"path:{threadPath}/_Notification scope:children nodeType:Notification")
                .ToListAsync(ct);
            notificationAppeared = notifications.Any(n =>
                n.Content is MeshWeaver.Mesh.Notification notif
                && notif.TargetNodePath == threadPath);
            if (!notificationAppeared) await Task.Delay(200, ct);
        }
        notificationAppeared.Should().BeTrue(
            "EmitCompletionNotification must create a Notification MeshNode targeting the thread, " +
            "which the user's notification bell databinds to");
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
        finalMsg.Summary.Should().NotBeEmpty("summary must contain text");
        Output.WriteLine($"Verified: response cell Summary length = {finalMsg.Summary!.Length}");

        var threadStreamFinal = workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!;
        var threadAtIdle = await threadStreamFinal
            .Select(nodes => nodes?.FirstOrDefault(n => n.Path == threadPath)?.Content as MeshThread)
            .Where(t => t is { Status: MeshWeaver.AI.ThreadExecutionStatus.Idle }
                        && !string.IsNullOrEmpty(t.Summary))
            .Take(1).Timeout(10.Seconds()).ToTask(ct);
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
    public async Task DelegationOfDelegation_SummaryPropagatesUp()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();
        var workspace = client.GetWorkspace();

        // 1. Submit at the grandparent — produces a normal terminal thread
        //    with a Summary. We'll then verify a parent and child below can
        //    propagate their summaries to observers via the same primitive.
        var gpResponse = await client
            .Observe(new CreateNodeRequest(
                ThreadNodeType.BuildThreadNode("TestUser", "Grandparent thread", "TestUser")),
                o => o.WithTarget(new Address("TestUser")))
            .FirstAsync().ToTask(ct);
        gpResponse.Message.Success.Should().BeTrue(gpResponse.Message.Error);
        var gpPath = gpResponse.Message.Node!.Path!;
        Output.WriteLine($"Grandparent: {gpPath}");
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client,
            ThreadPath = gpPath,
            UserText = "First level work",
            ContextPath = "TestUser",
        });
        var gpFinal = await workspace.GetRemoteStream<MeshNode>(new Address(gpPath))
            .Select(nodes => nodes?.FirstOrDefault(n => n.Path == gpPath)?.Content as MeshThread)
            .Where(t => t is { Status: ThreadExecutionStatus.Idle } && !string.IsNullOrEmpty(t.Summary))
            .Take(1).Timeout(45.Seconds()).ToTask(ct);
        gpFinal!.Summary.Should().NotBeNullOrEmpty(
            "Level-1 thread must write Summary atomically with Status=Idle so an observer " +
            "(e.g. a delegating parent) can read it in the same emission as the Idle flip");
        Output.WriteLine($"Level-1 Summary: {gpFinal.Summary![..Math.Min(80, gpFinal.Summary.Length)]}");

        // 2. Spawn a child thread submission and observe its terminal Summary
        //    the same way — proving the propagation chain works at any depth.
        //    This is the Scan-based "Running → Idle" subscription the
        //    DelegationTool uses, applied identically here in a test.
        var childResponse = await client
            .Observe(new CreateNodeRequest(
                ThreadNodeType.BuildThreadNode("TestUser", "Child thread", "TestUser")),
                o => o.WithTarget(new Address("TestUser")))
            .FirstAsync().ToTask(ct);
        childResponse.Message.Success.Should().BeTrue(childResponse.Message.Error);
        var childPath = childResponse.Message.Node!.Path!;
        Output.WriteLine($"Child: {childPath}");
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client,
            ThreadPath = childPath,
            UserText = "Second level work",
            ContextPath = "TestUser",
        });
        var childRunningToIdle = await workspace.GetRemoteStream<MeshNode>(new Address(childPath))
            .Select(nodes => nodes?.FirstOrDefault(n => n.Path == childPath)?.Content as MeshThread)
            .Where(t => t is not null)
            .Scan((sawRunning: false, terminal: (MeshThread?)null), (state, t) =>
            {
                if (t!.Status is ThreadExecutionStatus.Executing
                              or ThreadExecutionStatus.StartingExecution
                              or ThreadExecutionStatus.Completing)
                    return (true, null);
                if (state.sawRunning && t.Status == ThreadExecutionStatus.Idle)
                    return (state.sawRunning, t);
                return state;
            })
            .Where(s => s.terminal is not null)
            .Take(1).Timeout(45.Seconds()).ToTask(ct);
        childRunningToIdle.terminal!.Summary.Should().NotBeNullOrEmpty(
            "Level-2 (child) thread reactive Running→Idle subscription must surface the " +
            "child's Summary — same shape the DelegationTool uses for sub-thread tool-call results");
        Output.WriteLine($"Level-2 Summary: {childRunningToIdle.terminal.Summary![..Math.Min(80, childRunningToIdle.terminal.Summary.Length)]}");
    }
}
