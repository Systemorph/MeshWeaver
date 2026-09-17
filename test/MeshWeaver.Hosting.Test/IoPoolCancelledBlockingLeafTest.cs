using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #4545 — a blocking leaf the POOL cancels owes its subscriber a terminal; a leaf the
/// SUBSCRIBER cancels owes it silence. The continuation could not tell them apart, so it stayed
/// silent for both.
///
/// <para><b>Both are one arm.</b> <c>InvokeBlocking</c> starts its work with a token linked to the
/// pool's, so a drain, a dispose and an unsubscribe all land the task on <c>IsCanceled</c>. The arm
/// named only the unsubscribe — *"Unsubscribed before completion — silent teardown"* — and the other
/// two reach it in two ways: a leaf still QUEUED on the limited-concurrency scheduler when
/// <see cref="IoPool.Drain"/> cancels the pool, and a leaf built before a drain and subscribed after,
/// whose task is created with an already-cancelled token so its delegate never runs. Measured before
/// the fix: 0 terminals in 50 subscribes of the second shape.</para>
///
/// <para><b>Why the terminal is a FAULT and not a completion.</b> <c>InvokeBlocking&lt;T&gt;</c> emits
/// one value and completes, so an empty completion does not read as "cancelled" — it reads as "the
/// IO ran and produced nothing", and callers turn exactly that into a value:
/// <c>CatalogLayoutAreas</c> folds an empty read into the Undeclared verdict with
/// <c>DefaultIfEmpty</c> (*"an absent record is a value here … never a silence"*),
/// <c>InstalledPackageRepairService</c> folds it into "partition present", and
/// <c>PublishedBundleCatalogue</c> states the rule: *"'I could not look' is NOT 'there is nothing
/// here', and the difference decides the verdict"*. <c>Invoke</c>/<c>InvokeStream</c> already fault
/// with <c>TaskCanceledException</c> when the pool cancels them, and every refusal answers
/// <see cref="OperationCanceledException"/>, so the fault also keeps the value-producing entry points
/// identical. <see cref="IIoPool.SubscribeThroughPool{T}"/> is the one surface that COMPLETES on a
/// drain, and it is not a counter-example: a change feed's terminal carries no value — nobody reads a
/// result out of it, its job is to run the <c>.Finally</c> bookkeeping (#1789).</para>
/// </summary>
public class IoPoolCancelledBlockingLeafTest
{
    /// <summary>
    /// 🚨 The path that is NOT reachable by building after the drain: a leaf already accepted and
    /// QUEUED on the limited-concurrency scheduler when the pool is drained. Its delegate never runs,
    /// so it can report nothing itself, and the continuation is the only place a terminal can come
    /// from.
    /// </summary>
    [Fact]
    public async Task ABlockingLeafQueuedWhenThePoolDrains_TerminatesWithACancellation_OffTheSubscribersThread()
    {
        // Cap 1, so leaf A holds the only slot and leaf B can only ever be queued behind it.
        using var pool = new IoPool(1, IoPool.DefaultDrainTimeout, TestTimeouts.Quick / 10);
        var headRunning = new AsyncSubject<Unit>();
        var releaseHead = 0;
        var queuedRan = 0;

        var head = pool.InvokeBlocking(_ =>
        {
            headRunning.OnNext(Unit.Default);
            headRunning.OnCompleted();
            // Parked deliberately — the park IS the subject, so it is a bounded spin on a volatile
            // flag the test releases in its finally.
            SpinWait.SpinUntil(() => Volatile.Read(ref releaseHead) == 1, TestTimeouts.Quick);
            return 1;
        }).Subscribe(_ => { }, _ => { });

        try
        {
            await headRunning.Should().Within(TestTimeouts.Quick).Emit(
                "precondition: the head leaf holds the pool's only blocking slot",
                cancellationToken: TestContext.Current.CancellationToken);

            var terminal = new AsyncSubject<Unit>();
            var kind = "none";
            var terminalThread = 0;
            var subscriberThread = 0;

            // Subscribed from a dedicated thread — never a pool thread, so "the terminal ran on the
            // subscriber" cannot be confused with a reused pool thread.
            var subscriber = new Thread(() =>
            {
                subscriberThread = Environment.CurrentManagedThreadId;
                pool.InvokeBlocking(_ => { Volatile.Write(ref queuedRan, 1); return 2; })
                    .Subscribe(
                        _ => { },
                        ex =>
                        {
                            kind = ex.GetType().Name;
                            terminalThread = Environment.CurrentManagedThreadId;
                            terminal.OnNext(Unit.Default);
                            terminal.OnCompleted();
                        },
                        () =>
                        {
                            kind = "OnCompleted";
                            terminalThread = Environment.CurrentManagedThreadId;
                            terminal.OnNext(Unit.Default);
                            terminal.OnCompleted();
                        });
            })
            {
                IsBackground = true,
                Name = "subscriber (hub turn)",
            };
            subscriber.Start();
            subscriber.Join(TestTimeouts.Quick);

            Assert.True(SpinWait.SpinUntil(() => pool.CurrentlyWaiting >= 1, TestTimeouts.Quick),
                "precondition: the second leaf is QUEUED on the scheduler — the state whose "
                + "cancellation this test is about");

            // The drain cancels the pool token while that leaf is still queued: its task goes
            // straight to Canceled and its delegate never runs.
            var drain = Task.Run(pool.Drain, TestContext.Current.CancellationToken);

            await terminal.Should().Within(TestTimeouts.Quick).Emit(
                "a blocking leaf the POOL cancelled must terminate — it delivered nothing at all "
                + "before #4545, so its subscriber's .Finally never ran and a bounded caller saw only "
                + "its own timeout",
                cancellationToken: TestContext.Current.CancellationToken);

            kind.Should().Be(nameof(OperationCanceledException),
                "the leg is a SINGLE-VALUE surface: an empty OnCompleted reads as 'the IO produced "
                + "nothing', which callers fold into a value (DefaultIfEmpty → Undeclared / present). "
                + "A cancellation must arrive as a cancellation — the same shape Invoke and every "
                + "refusal already deliver");
            terminalThread.Should().NotBe(subscriberThread,
                "and off the subscriber's thread, like every other pool terminal (#4530)");
            Volatile.Read(ref queuedRan).Should().Be(0,
                "the queued leaf was cancelled before it ran — this is the arm where the work itself "
                + "can report nothing");

            Volatile.Write(ref releaseHead, 1);
            (await drain.WaitAsync(TestTimeouts.Quick, TestContext.Current.CancellationToken))
                .Should().Be(0, "the head leaf was released, so the drain joins clean");
        }
        finally
        {
            // A failing assertion above must not strand the parked head leaf on a pool thread.
            Volatile.Write(ref releaseHead, 1);
            head.Dispose();
        }
    }

    /// <summary>
    /// 🚨 THE OTHER HALF, and the one the old comment named: a leaf the SUBSCRIBER ended gets no
    /// terminal. Rx forbids delivering anything after an unsubscribe, so #4545's fix must stay silent
    /// here — a terminal for every cancelled task would have been the easy, wrong version of it.
    /// </summary>
    [Fact]
    public async Task ABlockingLeafTheSubscriberUnsubscribed_DeliversNoTerminal()
    {
        using var pool = new IoPool(1);
        var running = new AsyncSubject<Unit>();
        var releaseWork = 0;
        var terminal = new AsyncSubject<Unit>();

        var subscription = pool.InvokeBlocking(ct =>
        {
            running.OnNext(Unit.Default);
            running.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref releaseWork) == 1 || ct.IsCancellationRequested, TestTimeouts.Quick);
            ct.ThrowIfCancellationRequested();
            return 1;
        }).Subscribe(
            _ => { terminal.OnNext(Unit.Default); terminal.OnCompleted(); },
            _ => { terminal.OnNext(Unit.Default); terminal.OnCompleted(); },
            () => { terminal.OnNext(Unit.Default); terminal.OnCompleted(); });

        try
        {
            await running.Should().Within(TestTimeouts.Quick).Emit(
                "precondition: the work is running, so the unsubscribe below cancels a leaf in flight",
                cancellationToken: TestContext.Current.CancellationToken);

            subscription.Dispose();

            // A negative assertion spends its whole window by construction, so the window is a small
            // fraction of the budget — it must stay well inside xunit's flat 30 s methodTimeout even
            // with the CI scaling factor applied.
            await terminal.Should().NotEmit(TestTimeouts.Quick / 10,
                "after an unsubscribe nothing may be delivered — the subscriber is gone, and #4545 "
                + "gives a terminal to the POOL's cancellation only");
        }
        finally
        {
            Volatile.Write(ref releaseWork, 1);
        }
    }
}
