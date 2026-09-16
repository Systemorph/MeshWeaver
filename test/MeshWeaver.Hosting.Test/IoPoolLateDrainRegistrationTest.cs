using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #4524 — a pooled subscribe that was admitted before a drain and registers its drain callback
/// AFTER the drain's cancel landed ran that subscription's downstream teardown on the SUBSCRIBER's
/// thread.
///
/// <para><b>The mechanism.</b> <see cref="IIoPool.SubscribeThroughPool{T}"/> refuses what is BUILT once
/// <c>Drain()</c> has begun, but the check is read when the cold observable is built — and
/// <c>Drain()</c> never sets <c>_disposing</c>, so the subscribe itself is still admitted.
/// It then calls <c>_poolCts.Token.Register(teardown)</c>, and <see cref="CancellationToken.Register(Action)"/>
/// on a token that is ALREADY cancelled does not register anything: it runs the callback
/// synchronously, on the calling thread. That callback is <c>inner.Dispose(); observer.OnCompleted();</c>
/// — the subscription's whole downstream teardown, on whoever was subscribing: a hub action block, a
/// grain turn, or <c>OrderedRouteDispatcher.DrainNext</c>, which documents that a leg "can never
/// complete inside its own subscribe call". <c>StartCanceller</c> (#2394) closed the path where
/// <c>Cancel()</c> is CALLED on the wrong thread; this is the path where a callback runs because it was
/// REGISTERED late, and nothing closed it.</para>
///
/// <para><b>The window, widened.</b> In production it is the few instructions between the build-time
/// <c>_draining</c> read and the <c>Register</c> call. Here the whole drain — cancel requested, cancel
/// RETURNED (Drain joins it) — is placed inside that window by building the leg before the drain and
/// subscribing it after, which is the same admission with the race removed.</para>
///
/// <para><b>Why the seam.</b> The setup leaf is the other producer of a terminal: its gate wait sees
/// the cancelled token and reports <c>OnCompleted</c> from a pool thread. Left alone it races the late
/// registration, and whichever terminal arrives first is the only one the subscriber can see (Rx
/// drops the second), so an unseamed test would pass whenever the pool thread happened to win.
/// Parking the setup leaf until <c>Subscribe()</c> has returned makes the assertion about the
/// registration's thread, and nothing else.</para>
/// </summary>
public class IoPoolLateDrainRegistrationTest
{
    [Fact]
    public async Task ASubscribeAdmittedBeforeADrain_IsTerminatedOffTheSubscribersThread()
    {
        using var pool = new IoPool(2);
        var sourceSubscribed = 0;
        var subscriberThread = 0;
        var subscribeReturned = 0;
        var releaseSetupLeaf = 0;
        var terminalThread = 0;
        var terminal = new AsyncSubject<Unit>();
        var subscription = new SingleAssignmentDisposable();

        void Terminal()
        {
            Interlocked.CompareExchange(ref terminalThread, Environment.CurrentManagedThreadId, 0);
            terminal.OnNext(Unit.Default);
            terminal.OnCompleted();
        }

        // BUILT while the pool is alive: this is where the `_draining` refusal is evaluated, so the
        // leg is admitted exactly as one issued a moment before the drain would be.
        var leg = pool.SubscribeThroughPool(Observable.Create<int>(_ =>
        {
            Volatile.Write(ref sourceSubscribed, 1);
            return Disposable.Empty;
        }));

        // The drain lands in full — Drain() joins the cancel, so the pool token IS cancelled when this
        // returns. Nothing was admitted yet, so it has nothing to report.
        pool.Drain().Should().Be(0, "precondition: nothing was in flight, so the drain reports no residual");

        // Whether the seam let the setup leaf go on its BUDGET rather than on the test's release. A leaf
        // released that way could deliver its terminal before Subscribe() returned, and the thread
        // assertion below would then prove nothing — so it is asserted, not assumed.
        var setupLeafReleasedByBudget = 0;
        pool.OnSubscribeSetupLeafStarting = () =>
        {
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref releaseSetupLeaf) == 1, TestTimeouts.Quick))
                Volatile.Write(ref setupLeafReleasedByBudget, 1);
        };

        try
        {
            // A dedicated thread stands in for the hub turn: it is never a pool thread, so "the
            // terminal ran on it" cannot be confused with "a pool thread happened to be reused".
            var subscriber = new Thread(() =>
            {
                Volatile.Write(ref subscriberThread, Environment.CurrentManagedThreadId);
                subscription.Disposable = leg.Subscribe(_ => { }, _ => Terminal(), Terminal);
                Volatile.Write(ref subscribeReturned, 1);
            })
            {
                IsBackground = true,
                Name = "subscriber (hub turn)",
            };
            subscriber.Start();

            SpinWait.SpinUntil(() => Volatile.Read(ref subscribeReturned) == 1, TestTimeouts.Quick)
                .Should().BeTrue("precondition: Subscribe() returned — the setup leaf is parked, so it cannot "
                    + "have delivered anything yet, and whatever DID run inside Subscribe() ran on the subscriber");

            Volatile.Write(ref releaseSetupLeaf, 1);

            await terminal.Should().Within(TestTimeouts.Quick).Emit(
                "a leg the drain cancelled must still TERMINATE — its .Finally is what releases a route slot "
                + "and advances OrderedRouteDispatcher's FIFO (#1789)",
                cancellationToken: TestContext.Current.CancellationToken);

            Volatile.Read(ref setupLeafReleasedByBudget).Should().Be(0,
                "precondition: the setup leaf was held by the seam until the test released it — one that "
                + "left on its budget could have terminated the observer first, making the thread "
                + "assertion below vacuous");

            Volatile.Read(ref terminalThread).Should().NotBe(Volatile.Read(ref subscriberThread),
                "a pooled subscription's downstream teardown must never run on the thread that SUBSCRIBED — "
                + "that thread is a hub action block or a grain turn, and the teardown is arbitrary "
                + "application code. CancellationToken.Register on an already-cancelled token runs its "
                + "callback inline on the registering thread, which is exactly where this one ran (#4524)");
            Volatile.Read(ref sourceSubscribed).Should().Be(0,
                "the cancel preceded the subscribe, so the source must never be opened");
        }
        finally
        {
            Volatile.Write(ref releaseSetupLeaf, 1);
            subscription.Dispose();
        }
    }
}
