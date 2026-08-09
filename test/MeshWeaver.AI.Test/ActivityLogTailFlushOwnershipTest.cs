using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// #995 — <c>ActivityLogLogger</c>'s throttle tail-flush timer must be CANCELLABLE by the hub
/// that owns it.
///
/// <para><b>The defect.</b> A burst of script log calls is coalesced by arming a 100 ms
/// <c>Observable.Timer</c> that publishes the latest snapshot. The subscription it returned was
/// discarded. <c>Observable.Timer</c> parks its entry on the process-wide <c>TimerQueue</c>, a
/// strong GC root, so a pending flush held the tick closure → the logger → the <c>IMessageHub</c>
/// it posts to. Nothing could cancel it, so a hub torn down within 100 ms of a script's last log
/// call stayed rooted past its own disposal — the same shape as the four watcher timers fixed in
/// #996, at a tenth of the window.</para>
///
/// <para><b>What this test pins, and why not GC.</b> The assertion is on OWNERSHIP, not on
/// collectability: after the hub is disposed the handle holding the pending flush is disposed
/// too. That is exact and timing-free — it holds whether or not the 100 ms timer has already
/// fired, so the test can neither flake nor pass by accident. A <c>WeakReference</c> probe would
/// instead be a sampling test of a 100 ms window (the reason <c>MeshHubDisposalLeakTest</c> pins
/// nothing — see its remarks). Negative control: drop the <c>RegisterForDisposal</c> from
/// <c>ActivityLogLogger.RegisterPendingFlush</c>, leaving the field a plain
/// <c>new SerialDisposable()</c>, and the final assertion fails.</para>
/// </summary>
public class ActivityLogTailFlushOwnershipTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Arm the throttled tail flush, then dispose the hub the logger posts to: the pending
    /// flush must go down with it.
    /// </summary>
    [Fact]
    public async Task DisposingTheHub_CancelsThePendingTailFlush()
    {
        var hub = GetClient();
        // Production default: the activity path IS the hub's own address (KernelExecutor).
        var logger = new ActivityLogLogger(hub, hub.Address.ToString());

        var pending = PendingFlushOf(logger);

        // The FIRST append publishes immediately and stamps the throttle window; an append that
        // lands inside that window is the one that arms the tail-flush timer. Append in a tight
        // loop rather than assuming two adjacent statements run within 100 ms of each other —
        // no sleeps, no fixed waits, just "keep appending until the throttle engages".
        for (var i = 0; i < 100 && pending.Disposable is null; i++)
            logger.LogInformation("append {Index}", i);

        pending.Disposable.Should().NotBeNull(
            "a throttled append must arm the tail-flush timer INTO the hub-owned handle — "
            + "if nothing is armed this test is asserting on an empty slot");
        pending.IsDisposed.Should().BeFalse("the hub is still alive");

        hub.Dispose();
        await hub.DisposalCompleted.FirstAsync().ToTask().WaitAsync(TimeSpan.FromSeconds(30));

        pending.IsDisposed.Should().BeTrue(
            "tearing down the hub must cancel a tail flush that is still pending — an "
            + "uncancelled Observable.Timer sits on the process-wide TimerQueue (a strong GC "
            + "root) and pins the logger, and through it the hub, past its own disposal (#995)");
    }

    /// <summary>
    /// Reads the logger's pending-flush handle. Private by design — the guard turns a rename
    /// into a loud failure instead of a silently vacuous test.
    /// </summary>
    private static SerialDisposable PendingFlushOf(ActivityLogLogger logger)
    {
        var field = typeof(ActivityLogLogger)
            .GetField("pendingFlush", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull(
            "ActivityLogLogger.pendingFlush is the handle this test is about — if it was "
            + "renamed or retyped, update this test rather than losing the coverage");
        return (SerialDisposable)field!.GetValue(logger)!;
    }
}
