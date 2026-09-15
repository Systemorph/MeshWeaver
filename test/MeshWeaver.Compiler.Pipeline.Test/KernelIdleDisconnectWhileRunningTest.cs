using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Kernel;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 The kernel's idle-disconnect must not dispose a hub whose submission is still executing
/// (MeshWeaver#4422). The timer is re-armed only by messages DELIVERED to the hub, and a running
/// script delivers none — so on memex (2026-09-15) an approved OperationRequest's script died
/// silently 15 min after the last inbound message, mid-run. Idle now means "no message AND nothing
/// in flight", and the reclamation of a FINISHED hub (#1324/#1435) is unchanged — pinned in both
/// directions below.
///
/// <para>Real hubs, an injected clock. The kernel host and its executor are real hosted hubs, so
/// "disposed" is the hub's own <see cref="IMessageHub.IsDisposing"/> and a dead executor is one
/// that was really disposed. The window runs on a <see cref="ManualClock"/> — the
/// <see cref="KernelHubOptions.TimeProvider"/> seam — so every assertion is about WHEN the one-shot
/// timer fires, stepped deterministically through the production window rather than waited out
/// or shrunk.</para>
/// </summary>
public class KernelIdleDisconnectWhileRunningTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>The production window — the test steps through it, never shortens it.</summary>
    private static readonly TimeSpan Window = new KernelHubOptions().IdleDisconnectTimeout;

    /// <summary>How the executor's answer ends a submission — every one must restart the window.</summary>
    public enum Termination { Response, Error, Teardown }

    [Theory]
    [InlineData(Termination.Response)]
    [InlineData(Termination.Error)]
    [InlineData(Termination.Teardown)]
    public async Task ARunningSubmission_OutlivesTheWindow_AndTheWindowRestartsWhenItEnds(Termination end)
    {
        var (kernel, executor, idle, clock) = await ArrangeAsync();
        var answer = new Subject<Unit>();
        var subscription = idle.Track(claim => { claim.Bind(executor); return answer; })
            .Take(1)
            .Subscribe(_ => { }, _ => { });

        clock.Advance(Window);
        Assert.False(kernel.IsDisposing, "a submission still executing on a live executor keeps its hub past the window");
        Assert.Equal(1, idle.InFlight);

        clock.Advance(Window / 2);
        switch (end)
        {
            case Termination.Response: answer.OnNext(Unit.Default); break;
            case Termination.Error: answer.OnError(new InvalidOperationException("the script failed")); break;
            case Termination.Teardown: subscription.Dispose(); break;
        }
        Assert.Equal(0, idle.InFlight);

        // The keep at the first tick re-armed the timer for 2×Window. Ending the run half a window
        // later must have moved it to 2.5×Window — otherwise the hub goes half a window after its run.
        clock.Advance(Window / 2);
        Assert.False(kernel.IsDisposing, $"the window restarts when the run ends ({end}), so the hub is not reclaimed at the earlier due time");

        clock.Advance(Window / 2);
        Assert.True(kernel.IsDisposing, "a finished hub is still reclaimed one full window after its run (#1324/#1435)");
    }

    [Fact]
    public async Task ADeadExecutor_CannotKeepTheHubAliveForever()
    {
        var (kernel, executor, idle, clock) = await ArrangeAsync();
        idle.Track(claim => { claim.Bind(executor); return Observable.Never<Unit>(); }).Subscribe();

        clock.Advance(Window);
        Assert.False(kernel.IsDisposing, "kept while the executor is alive");

        executor.Dispose();
        // The keep above re-armed the one-shot timer; without that re-arm nothing would ever fire again.
        clock.Advance(Window);
        Assert.True(kernel.IsDisposing, "a dead executor cannot answer, so its submission no longer holds the hub");
        Assert.True(idle.Closed);
    }

    [Fact]
    public async Task TheWindowElapsingDuringExecutorSetup_KeepsTheHub()
    {
        var (kernel, executor, idle, clock) = await ArrangeAsync();

        idle.Track(claim =>
        {
            // The timer fires while the forwarder is still creating the executor.
            clock.Advance(Window);
            claim.Bind(executor);
            return Observable.Never<Unit>();
        }).Subscribe();

        Assert.False(kernel.IsDisposing, "the claim is taken BEFORE the setup, so the tick sees a submission being set up");
        Assert.Equal(1, idle.InFlight);
    }

    [Fact]
    public async Task ASetupThatThrows_ReleasesItsClaim()
    {
        var (kernel, _, idle, clock) = await ArrangeAsync();
        Exception? failure = null;

        // What IMessageHub.Observe does when the post itself fails: it throws before returning.
        idle.Track<Unit>(_ => throw new InvalidOperationException("the post was refused"))
            .Subscribe(_ => { }, ex => failure = ex);

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(0, idle.InFlight);
        clock.Advance(Window);
        Assert.True(kernel.IsDisposing, "a submission whose setup threw holds nothing, so the idle hub is reclaimed");
    }

    [Fact]
    public async Task AClaimAfterTheHubClosed_IsRefused_AndNothingIsSetUp()
    {
        var (kernel, _, idle, clock) = await ArrangeAsync();
        clock.Advance(Window);
        Assert.True(kernel.IsDisposing, "nothing was submitted: the idle hub is reclaimed as before");

        var dispatched = false;
        Exception? failure = null;
        idle.Track(_ => { dispatched = true; return Observable.Never<Unit>(); })
            .Subscribe(_ => { }, ex => failure = ex);

        Assert.False(dispatched, "the close and the claim are one ordered decision: a closed hub sets nothing up");
        Assert.IsType<ObjectDisposedException>(failure);
        Assert.Equal(0, idle.InFlight);
    }

    private async Task<(IMessageHub Kernel, IMessageHub Executor, KernelContainer.IdleState Idle, ManualClock Clock)> ArrangeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var kernel = GetHost().GetHostedHub(new Address("kernelhost", id), c => c)!;
        var executor = kernel.GetHostedHub(new Address("kernelExec", id), c => c)!;
        await kernel.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);
        await executor.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);
        var clock = new ManualClock();
        return (kernel, executor, new KernelContainer.IdleState(new WeakReference<IMessageHub>(kernel), Window, clock), clock);
    }

    /// <summary>
    /// A clock that moves only when told to, firing each due one-shot timer at its due instant —
    /// the injected <see cref="TimeProvider"/> behind <see cref="KernelHubOptions.TimeProvider"/>.
    /// Single-threaded by construction: the test drives it and every callback runs inline.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        private ImmutableList<ManualTimer> timers = ImmutableList<ManualTimer>.Empty;

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            timers = timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var target = now + by;
            while (timers.Where(t => t.Due <= target).OrderBy(t => t.Due).FirstOrDefault() is { } next)
            {
                now = next.Due!.Value;
                next.Fire();
            }
            now = target;
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
            return true;
        }

        /// <summary>One-shot: the due time is consumed BEFORE the callback, which may re-arm it.</summary>
        public void Fire()
        {
            Due = null;
            callback(state);
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
