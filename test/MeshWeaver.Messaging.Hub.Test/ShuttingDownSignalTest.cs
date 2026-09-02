using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins <see cref="IMessageHub.ShuttingDown"/> — the hub's teardown state as a SOURCE
/// (Systemorph/MeshWeaver#3026).
///
/// <para><b>Why the source exists.</b> A hub-owned watcher must stop at the FIRST instant its hub
/// becomes part of a shutdown. That instant is not the hub's own <c>Dispose()</c> alone: an
/// ancestor's <c>Dispose()</c> freezes hosted-hub creation across the whole subtree synchronously
/// (issue #613), and a descendant's own disposal begins only in the ancestor's DisposeHostedHubs
/// phase — potentially seconds later, after the ancestor has drained its own callbacks for the
/// entire quiesce budget. <see cref="IMessageHub.IsShuttingDown"/> already reports that window as
/// a property; nothing reported it as an event, so a watcher could only sample it.</para>
///
/// <para>Deterministic by construction, the same way <c>TeardownHubCreationFreezeTest</c> is: the
/// root's single-threaded action block is stalled by a gated handler, so the posted
/// <c>ShutdownRequest</c> provably cannot be processed during the assertions — the only thing that
/// can have raised the signal on a descendant is the synchronous cascade inside <c>Dispose()</c>.</para>
/// </summary>
public class ShuttingDownSignalTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Blocker;

    [Fact]
    public async Task Dispose_RaisesShuttingDownOnTheWholeSubtree_BeforeAnyDisposalPhaseRuns()
    {
        // 🚨 No hand-woven gate: handler → test is an AsyncSubject the handler completes; test →
        // parked handler is a volatile flag polled under a bounded SpinUntil, released in `finally`.
        var handlerEntered = new AsyncSubject<Unit>();
        var releaseHandler = 0;

        var client = GetClient();
        var root = client.ServiceProvider.CreateMessageHub(
            new Address("shutting-down-root", "1"),
            c => c
                .WithPostingIdentity(PostingIdentity.System)
                .WithTypes(typeof(Blocker))
                .WithHandler<Blocker>((_, request) =>
                {
                    handlerEntered.OnNext(Unit.Default);
                    handlerEntered.OnCompleted();
                    // Deliberately ignores cancellation: keeps the root's action block busy so
                    // the ShutdownRequest posted by Dispose() cannot be processed until released.
                    SpinWait.SpinUntil(() => Volatile.Read(ref releaseHandler) == 1, TestTimeouts.Convergence);
                    return request.Processed();
                }));
        var child = root.GetHostedHub(new Address("shutting-down-child", "1"));
        var grandchild = child.GetHostedHub(new Address("shutting-down-grandchild", "1"));

        var rootSignalled = 0;
        var childSignalled = 0;
        var grandchildSignalled = 0;
        using var rootSub = root.ShuttingDown.Subscribe(
            _ => Interlocked.Exchange(ref rootSignalled, 1),
            ex => Output.WriteLine($"root ShuttingDown faulted: {ex}"));
        using var childSub = child.ShuttingDown.Subscribe(
            _ => Interlocked.Exchange(ref childSignalled, 1),
            ex => Output.WriteLine($"child ShuttingDown faulted: {ex}"));
        using var grandchildSub = grandchild.ShuttingDown.Subscribe(
            _ => Interlocked.Exchange(ref grandchildSignalled, 1),
            ex => Output.WriteLine($"grandchild ShuttingDown faulted: {ex}"));

        try
        {
            root.Post(new Blocker(), o => o.WithTarget(root.Address));
            await handlerEntered.Should().Within(10.Seconds()).Emit("the blocker handler must be running");

            Volatile.Read(ref rootSignalled).Should().Be(0, "a live hub has not begun shutting down");
            Volatile.Read(ref childSignalled).Should().Be(0, "a live hub has not begun shutting down");
            Volatile.Read(ref grandchildSignalled).Should().Be(0, "a live hub has not begun shutting down");

            root.Dispose();

            // Synchronous: Dispose() has returned and the async shutdown pipeline is provably
            // NOT running (the action block is parked), so every signal below came from the
            // cascade inside Dispose() itself.
            Volatile.Read(ref rootSignalled).Should().Be(1,
                "the disposing hub's own ShuttingDown fires inside Dispose(), before any phase runs");
            Volatile.Read(ref childSignalled).Should().Be(1,
                "an ancestor's Dispose() freezes the subtree synchronously — that IS the child's first "
                + "instant of teardown, and it must be observable as an event, not only as IsShuttingDown");
            Volatile.Read(ref grandchildSignalled).Should().Be(1,
                "the cascade reaches arbitrarily deep, and so must the signal");

            child.IsShuttingDown.Should().BeTrue("the freeze has reached the child");
            child.IsDisposing.Should().BeFalse(
                "the child's OWN disposal has not started — its DisposeRequest arrives only in the "
                + "root's DisposeHostedHubs phase, which cannot run while the root's action block is "
                + "parked. This is the window nothing but ShuttingDown can see");
            child.RunLevel.Should().Be(MessageHubRunLevel.Started,
                "no disposal phase has run on the child, so RunLevelChanged cannot have reported anything");

            var late = 0;
            using var lateSub = grandchild.ShuttingDown.Subscribe(
                _ => Interlocked.Exchange(ref late, 1),
                ex => Output.WriteLine($"late ShuttingDown faulted: {ex}"));
            Volatile.Read(ref late).Should().Be(1,
                "the source replays: a subscriber attaching after the moment is told at once, so "
                + "subscribing can never race the moment it exists to observe");
        }
        finally
        {
            Volatile.Write(ref releaseHandler, 1);
        }
    }
}
