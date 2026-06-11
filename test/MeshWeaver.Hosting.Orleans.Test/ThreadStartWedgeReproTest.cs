#pragma warning disable CS1591

using System;
using System.Runtime.CompilerServices;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Regression repro for the 2026-06-11 thread-execution wedge. A brand-new chat started via
/// <see cref="HubThreadExtensions.StartThread"/> — create the thread node AND submit the first
/// message interleaved (the "hello world" trigger) — wedged the thread grain inside
/// <c>MessageHubGrain.DeliverMessage</c>: the fire-and-forget <c>ExecuteMessageAsync</c> (void)
/// meant the submission watcher never observed round completion, so the round parked
/// non-terminal while concurrent <c>SubscribeRequest</c>/<c>CreateNodeRequest</c> to the thread
/// aged out (60s+ STALE-CALLBACKs) and the stream-routing storm built up (155% CPU).
///
/// <para>With <c>ExecuteMessageAsync</c> returning <c>IObservable&lt;Unit&gt;</c> — subscribed by
/// the submission watcher and completing only when the terminal Status write lands — a StartThread
/// round reaches terminal <c>Idle</c> AND the thread hub stays responsive throughout.</para>
/// </summary>
public class ThreadStartWedgeReproTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"startwedge-{name}-{Guid.NewGuid():N}", "TestUser");

    [Fact(Timeout = 60000)]
    public void StartThread_NewChat_ReachesTerminal_AndHubStaysResponsive()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // StartThread = the real new-chat trigger: creates the thread node AND submits the first
        // message in one go — the create/execute interleave that wedged the grain in prod.
        var threadCreated = new AsyncSubject<MeshNode>();
        client.StartThread(
            "TestUser",
            "hello world",
            contextPath: "TestUser",
            createdBy: "TestUser",
            onCreated: node => { threadCreated.OnNext(node); threadCreated.OnCompleted(); },
            onError: err => threadCreated.OnError(new InvalidOperationException(err)));

        var created = threadCreated.Should().Within(35.Seconds()).Match(n => n is not null);
        var threadPath = created!.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        // (a) The thread hub stays RESPONSIVE while the round runs — a one-shot read on the SAME
        //     thread hub completes promptly. Under the wedge this queued behind the blocked
        //     DeliverMessage and aged out (the pending SubscribeRequest/CreateNodeRequest).
        var liveRead = client.GetMeshNode(threadPath, TimeSpan.FromSeconds(15))
            .Should().Within(20.Seconds()).Match(n => n is not null);
        liveRead.Should().NotBeNull(
            "the thread hub must answer reads while a round executes — not wedge in DeliverMessage");

        // (b) The round reaches terminal Idle — the submission watcher observed completion via the
        //     returned round observable (the fix). Under the fire-and-forget wedge it parked
        //     non-terminal forever.
        var threadAtIdle = workspace.GetMeshNodeStream(threadPath)
            .Select(node => node?.Content as MeshThread)
            .Should().Within(45.Seconds()).Match(t => t is { Status: ThreadExecutionStatus.Idle });
        threadAtIdle!.Status.Should().Be(ThreadExecutionStatus.Idle,
            "a StartThread round must reach a terminal state — no fire-and-forget wedge");
        Output.WriteLine("Verified: StartThread round reached terminal Idle and the hub stayed responsive");
    }
}
