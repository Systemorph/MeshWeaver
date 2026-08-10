using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Contract for <see cref="ReEstablishSchedule"/> — the scheduling half the three
/// HAND-ROLLED thread re-establish loops share
/// (<c>ThreadExecution.InstallExecRoundWatcher</c>,
/// <c>ThreadExecution.InitializeThreadLifecycle</c>,
/// <c>ThreadSubmissionServer.InstallServerWatcher</c>). Those loops are NOT
/// <c>ActivityControlPlaneExtensions.SubscribeWithReEstablish</c> — they only imitate it —
/// so the two teardown hazards below were never covered by that helper's tests, and had
/// no coverage of their own at all.
///
/// <para>Both hazards live in the same window: the 1 s delay between a stream faulting
/// and the re-establish firing. Teardown is EXACTLY when a watched stream faults, so that
/// window is the common case, not the rare one.</para>
///
/// <list type="number">
///   <item><description><b>The tick never re-read <c>disposed</c>.</b> The flag was checked
///     when the timer was ARMED; a second later, on a hub that had since been torn down,
///     <c>Establish()</c> ran anyway and re-subscribed a dead hub's stream — the #991 shape
///     (a <c>TimerQueue</c> entry, a strong GC root, holding the hub).</description></item>
///   <item><description><b>A synchronous throw escaped onto the timer thread.</b>
///     <c>Establish()</c> resolved <c>GetWorkspace()</c> — i.e.
///     <c>ServiceProvider.GetRequiredService&lt;IWorkspace&gt;()</c> — from inside the
///     factory; after teardown that scope is gone and throws
///     <see cref="ObjectDisposedException"/>. Rx rethrows out of an <c>OnNext</c> handler,
///     and on the default (thread-pool) scheduler an unhandled exception there kills the
///     process. A wedges-to-zero violation: the fault must reach a graceful sink.</description></item>
/// </list>
///
/// <para>All virtual-time via <see cref="HistoricalScheduler"/> — no wall clock, no
/// <c>Task.Delay</c>, deterministic.</para>
/// </summary>
public class ReEstablishScheduleTest
{
    private const string Context = "TestWatcher";
    private const string ThreadPath = "TestPartition/_Thread/thread-1";

    /// <summary>
    /// 🚨 HAZARD 1. Models the real teardown race: the fault arms the re-establish while the
    /// watcher is still live, teardown flips <c>disposed</c> during the 1 s delay, and the
    /// tick fires anyway. The schedule is deliberately NOT disposed here — that is the point.
    /// In production teardown does dispose it, but disposing an Rx timer is best-effort
    /// against a tick already dispatched to the pool, so the flag re-read at FIRE time is the
    /// only thing that can stop the re-establish. Arm-time is the wrong time to ask.
    /// </summary>
    [Fact]
    public void Tick_DoesNotReEstablish_WhenDisposalLandsDuringTheDelay()
    {
        var scheduler = new HistoricalScheduler();
        var disposed = false;
        var establishes = 0;

        // Live at arm time — a genuinely transient fault on a healthy hub.
        using var pending = ReEstablishSchedule.Arm(
            () => disposed, () => establishes++, logger: null, Context, ThreadPath, scheduler);

        // …and the hub tears down inside the delay.
        disposed = true;

        scheduler.AdvanceBy(ReEstablishSchedule.Delay);

        establishes.Should().Be(0,
            "the re-establish must re-read the disposal flag when the timer FIRES, not only "
            + "when it was armed — a second passes in between, and teardown is exactly when "
            + "the watched stream faults. Re-subscribing a disposed hub's stream is the #991 "
            + "leak shape: the TimerQueue entry roots Establish → the watcher closure → the hub");
    }

    /// <summary>
    /// 🚨 HAZARD 2. <c>Establish()</c> re-resolving <c>GetWorkspace()</c> inside the factory
    /// makes a disposed DI scope throw SYNCHRONOUSLY out of the timer tick. This reproduces
    /// the exact mechanism — <c>GetRequiredService</c> on a disposed
    /// <see cref="ServiceProvider"/> — rather than a stand-in exception.
    ///
    /// <para><see cref="HistoricalScheduler"/>'s <c>AdvanceBy</c> runs the tick on THIS thread, so an
    /// escape shows up as a throw out of <c>AdvanceBy</c>. In production the tick runs on a
    /// thread-pool thread, where the same escape is an unhandled exception and takes the
    /// process down.</para>
    /// </summary>
    [Fact]
    public void Tick_ContainsASynchronousEstablishThrow_AndSinksItToTheLogger()
    {
        var scheduler = new HistoricalScheduler();
        var faults = new List<(LogLevel Level, Exception? Exception)>();
        var logger = new CapturingLogger(faults);

        // The hub's DI scope after teardown. GetWorkspace() is exactly this call.
        var services = new ServiceCollection()
            .AddSingleton<WorkspaceStandIn>()
            .BuildServiceProvider();
        services.Dispose();

        // `disposed` is still FALSE: the hub's RegisterForDisposal hook has not run yet, but
        // the scope is already gone. This is the window the flag alone cannot close, and the
        // reason the throw must be contained rather than merely avoided.
        using var pending = ReEstablishSchedule.Arm(
            () => false,
            () => services.GetRequiredService<WorkspaceStandIn>(),
            logger, Context, ThreadPath, scheduler);

        var tick = () => scheduler.AdvanceBy(ReEstablishSchedule.Delay);

        tick.Should().NotThrow(
            "a disposed scope must not throw out of the timer tick — on the production "
            + "(thread-pool) scheduler that is an UNHANDLED exception that kills the process. "
            + "Every fault has to reach a graceful sink");

        faults.Should().ContainSingle("the contained fault must be surfaced, not swallowed")
            .Which.Exception.Should().BeOfType<ObjectDisposedException>();
    }

    /// <summary>
    /// The arm-time guard, kept explicit: once the watcher is torn down NOTHING is armed —
    /// not a cancelled timer, no <c>TimerQueue</c> entry at all.
    /// </summary>
    [Fact]
    public void Arm_WhenAlreadyDisposed_ArmsNoTimerAtAll()
    {
        var scheduler = new HistoricalScheduler();
        var establishes = 0;

        var handle = ReEstablishSchedule.Arm(
            () => true, () => establishes++, logger: null, Context, ThreadPath, scheduler);

        scheduler.AdvanceBy(ReEstablishSchedule.Delay);

        establishes.Should().Be(0, "a torn-down watcher must not re-establish");
        handle.Should().BeSameAs(Disposable.Empty,
            "nothing may be scheduled at all — an armed-then-cancelled timer still parks on "
            + "the process-wide TimerQueue for the instant it exists (#991)");
    }

    /// <summary>
    /// #991's other half, at this layer: the returned handle CANCELS the pending
    /// re-establish, so assigning it into the watcher's <c>pendingReEstablish</c>
    /// <see cref="SerialDisposable"/> lets teardown drop the TimerQueue entry.
    /// </summary>
    [Fact]
    public void DisposingTheHandle_CancelsThePendingReEstablish()
    {
        var scheduler = new HistoricalScheduler();
        var establishes = 0;

        var handle = ReEstablishSchedule.Arm(
            () => false, () => establishes++, logger: null, Context, ThreadPath, scheduler);
        handle.Dispose();

        scheduler.AdvanceBy(ReEstablishSchedule.Delay);

        establishes.Should().Be(0,
            "the handle must cancel the pending schedule — the watcher assigns it into a "
            + "SerialDisposable that teardown disposes before the live subscription (#991)");
    }

    /// <summary>
    /// Guard against over-fixing: a transient fault on a LIVE watcher must still re-establish,
    /// and must do it OFF the synchronous error stack (a Subscribe-time fault re-entering
    /// <c>Establish</c> inline recurses to a stack overflow — the Orleans-shard SIGABRT).
    /// </summary>
    [Fact]
    public void Arm_WhileLive_ReEstablishesAfterTheDelay_AndNotBefore()
    {
        var scheduler = new HistoricalScheduler();
        var establishes = 0;

        using var pending = ReEstablishSchedule.Arm(
            () => false, () => establishes++, logger: null, Context, ThreadPath, scheduler);

        establishes.Should().Be(0,
            "the re-establish must hop OFF the synchronous error stack, never run inline");

        scheduler.AdvanceBy(ReEstablishSchedule.Delay - TimeSpan.FromMilliseconds(1));
        establishes.Should().Be(0, "the delay has not elapsed yet");

        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(1));
        establishes.Should().Be(1,
            "a live watcher whose stream faulted transiently must be re-established, or the "
            + "thread is left unobserved and a claimed round never dispatches");
    }

    /// <summary>Stands in for <c>IWorkspace</c> — the service <c>GetWorkspace()</c> resolves.</summary>
    private sealed class WorkspaceStandIn;

    private sealed class CapturingLogger(List<(LogLevel Level, Exception? Exception)> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Add((logLevel, exception));
    }
}
