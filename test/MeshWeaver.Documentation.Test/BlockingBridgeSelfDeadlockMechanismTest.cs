using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Deterministic repro of the self-deadlock mechanism <see cref="BlockingBridgeInTestRatchetGuard"/>
/// ratchets against (#2013): a <c>.GetAwaiter().GetResult()</c> bridge parks the calling thread until
/// its <see cref="Task"/> completes. If that Task's continuation is captured back onto the SAME
/// single-threaded <see cref="SynchronizationContext"/> — exactly what xUnit's own
/// <c>MaxConcurrencySyncContext</c> gives every test method here (<c>test/xunit.runner.json</c>:
/// <c>maxParallelThreads: 1</c>) — the continuation can never run: the one thread that would run it
/// is the one thread already blocked waiting for it.
///
/// <para>This does NOT depend on xUnit internals — it reproduces the identical shape with a minimal
/// hand-rolled single-threaded context, so the repro is deterministic and stays valid even if
/// xUnit's own context implementation changes. It exists because "I ran the fixed tests and they
/// passed" proves the fix didn't regress correctness, not that the ORIGINAL shape actually could
/// have deadlocked — this test proves the mechanism directly, once, so nobody has to take that on
/// faith.</para>
/// </summary>
public class BlockingBridgeSelfDeadlockMechanismTest
{
    /// <summary>
    /// The blocking bridge, reproduced directly: a `.GetAwaiter().GetResult()` call, made from code
    /// running on a captured single-threaded context, over a Task whose continuation needs that same
    /// context to run. Proven via a BOUNDED wait from a genuinely separate thread (this test's own) —
    /// that outer wait can never itself join the deadlock, which is what makes this repro safe to run
    /// in CI rather than an actual wedge.
    ///
    /// <para>🚨 By construction this intentionally leaves <see cref="SingleThreadedContext"/>'s
    /// background thread permanently parked for the life of the test process — the same forever-park
    /// this whole ratchet exists to keep out of the real test suite. It is safe here ONLY because the
    /// thread is <see cref="Thread.IsBackground"/> (does not block process exit) and there is exactly
    /// one of it, created to prove the point rather than left behind by accident.</para>
    /// </summary>
    [Fact]
    public async Task BlockingBridge_UnderASingleThreadedCapturedContext_Deadlocks()
    {
        var ctx = new SingleThreadedContext();
        var reachedAfterTheBlockingCall = new TaskCompletionSource<bool>();

        ctx.Post(_ =>
        {
            // Blocks ctx's ONE thread until HopThroughAndBack's Task completes. That Task's
            // continuation (after Task.Run's hop) is captured back onto ctx (SynchronizationContext
            // .Current == ctx at the await, ConfigureAwait(true) is the implicit default) — so it can
            // only run by being posted to ctx and picked up by ctx's message loop. That loop is this
            // same thread, which this line has already blocked. Deadlock.
            HopThroughAndBack(ctx).GetAwaiter().GetResult();
            reachedAfterTheBlockingCall.TrySetResult(true); // never reached if the deadlock reproduces
        }, null);

        // A bounded wait, off THIS test method's own thread (Task.Run) and never ctx's, is the
        // safety net: it proves the deadlock by timing out instead of by hanging the test run.
        var completedInTime = await Task.Run(
            () => reachedAfterTheBlockingCall.Task.Wait(TimeSpan.FromSeconds(3)));

        Assert.False(completedInTime,
            "A `.GetAwaiter().GetResult()` bridge whose Task's continuation is captured back onto "
            + "the SAME single-threaded SynchronizationContext must self-deadlock — this is the "
            + "#2013 mechanism. If this now completes, .NET's continuation-capture semantics changed "
            + "underneath the whole premise of BlockingBridgeInTestRatchetGuard.");

        // No ctx.Dispose()/join here — the thread is genuinely, permanently stuck by design (see the
        // class remarks). It is IsBackground, so it does not keep the test process alive.
    }

    /// <summary>
    /// The sanctioned fix, under the IDENTICAL captured context: <c>await</c> instead of
    /// <c>.GetAwaiter().GetResult()</c>. Awaiting suspends the caller and returns control to the
    /// context's message loop instead of blocking its one thread, so the posted continuation gets a
    /// turn to run and the Task completes promptly — proving "await the stream instead" (the allow
    /// file's own prescribed fix) actually resolves the mechanism above, not merely sidesteps it.
    /// </summary>
    [Fact]
    public async Task AwaitingInstead_UnderTheSameCapturedContext_CompletesPromptly()
    {
        var ctx = new SingleThreadedContext();
        try
        {
            var completedAfterAwait = new TaskCompletionSource<bool>();

            ctx.Post(async _ =>
            {
                await HopThroughAndBack(ctx); // suspends — never blocks ctx's thread
                completedAfterAwait.TrySetResult(true);
            }, null);

            // Waited from a threadpool thread (Task.Run), not this test method's own async
            // continuation, so a genuine regression here would report as a bounded timeout rather
            // than hanging the assembly.
            var completedInTime = await Task.Run(
                () => completedAfterAwait.Task.Wait(TimeSpan.FromSeconds(5)));

            Assert.True(completedInTime,
                "await must complete promptly under the same captured single-threaded context that "
                + "deadlocks a blocking bridge — this is the sanctioned #2013 fix: suspend the test "
                + "instead of parking its thread.");
        }
        finally
        {
            ctx.Shutdown();
        }
    }

    /// <summary>A real thread hop (off the captured context) then a resume that needs the captured
    /// context back — the shape any <c>StartAsync</c>/hosted-service-style method exhibits unless
    /// every <c>await</c> inside it uses <c>ConfigureAwait(false)</c> throughout, which
    /// <c>ConfigureAwait(true)</c> (the implicit default) does not.</summary>
    private static async Task HopThroughAndBack(SynchronizationContext ctx)
    {
        Assert.Same(ctx, SynchronizationContext.Current);
        await Task.Run(() => Thread.Sleep(50));
        // Resumed here only if the continuation was actually posted to and run by ctx — i.e. only
        // if the caller awaited instead of blocking ctx's one thread.
    }

    /// <summary>
    /// Minimal single-threaded <see cref="SynchronizationContext"/>: <see cref="Post"/> queues work
    /// for ONE dedicated background thread to run, one item at a time — the same shape xUnit's
    /// <c>MaxConcurrencySyncContext</c> gives a test method when <c>maxParallelThreads</c> bounds
    /// concurrency, reproduced directly so this repro does not depend on xUnit internals.
    /// </summary>
    private sealed class SingleThreadedContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadedContext()
        {
            _thread = new Thread(RunLoop) { IsBackground = true, Name = nameof(SingleThreadedContext) };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => Post(d, state);

        private void RunLoop()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }

        /// <summary>Stops accepting new work once the queue drains. NOT called by the deadlock test
        /// — see its remarks for why that thread is left intentionally, permanently parked.</summary>
        public void Shutdown() => _queue.CompleteAdding();

        public void Dispose() => Shutdown();
    }
}
