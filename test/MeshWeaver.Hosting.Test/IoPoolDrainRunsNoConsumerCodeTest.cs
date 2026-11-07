using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #7968 — at a silo stop the Query pool's cancel parked for the whole 30 s join, inside a
/// subscriber's <c>SelectMany</c>: the drain callback ran <c>observer.OnCompleted()</c> inline on the
/// <c>IoPool-cancel</c> thread, the completion reached <c>SelectMany.ObservableSelector._.Final()</c>,
/// and <c>Final()</c> waited in <c>Monitor.Enter</c> for the operator's gate, which another thread
/// held. The pool's join expired and the silo released over live work.
///
/// <para><b>The contract this pins:</b> the pool's cancel runs NO consumer code. It tears down its
/// own side of a pooled subscription (the subscription to the source) and delivers nothing; a
/// subscription still attached when the pool is cancelled is one its owner never disposed, and it is
/// reported as an UNOWNED LEAK naming the consumer. A consumer's bookkeeping runs because its OWNER
/// disposes it — Rx runs <c>.Finally</c> on dispose — on the owner's thread.</para>
///
/// <para><b>The other half of the deadlock</b> is a thread that holds the consumer's lock while it
/// waits for teardown. That is modelled literally: a thread takes the <c>SelectMany</c>'s own gate
/// and holds it until <c>DrainAll()</c> has returned. Under the old pool that is a two-thread cycle
/// broken only by the drain's budget expiring (the measured negative control in the PR); under this
/// one the cancel never touches the gate.</para>
/// </summary>
public class IoPoolDrainRunsNoConsumerCodeTest
{
    private static readonly TimeSpan Timeout10 = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainBudget = TimeSpan.FromMilliseconds(250);

    // The consumer's selector. A named local so the leak report can be asserted to NAME it: a
    // compiler-generated method name carries its enclosing name.
    private static IObservable<int> ConsumerSelector(int value) => Observable.Return(value);

    /// <summary>
    /// 🚨 THE incident shape: a pooled query feeding a <c>SelectMany</c> whose gate another thread
    /// holds — and that thread waits for the teardown. The drain must return on its own, with no
    /// residual, the consumer must receive nothing from the pool, and the leak must name the consumer.
    /// </summary>
    [Fact]
    public void Drain_WhenAnotherThreadHoldsTheConsumersSelectManyGateUntilTeardown_TheCancelReturns()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions { DrainTimeout = DrainBudget });
        var pool = (IoPool)registry.Get(IoPoolNames.Query);
        var feed = new Subject<int>();
        object? poolsObserver = null;
        var subscribed = 0;
        var consumerTerminalThread = 0;

        // The query's change feed: opened through the pool, then left open (a feed never completes).
        var pooled = pool.SubscribeThroughPool(Observable.Create<int>(o =>
        {
            poolsObserver = o;
            var d = feed.Subscribe(o);
            Volatile.Write(ref subscribed, 1);
            return d;
        }));

        // UNOWNED on purpose: nothing disposes this subscription before the drain — that is the leak.
        var subscription = pooled
            .SelectMany(ConsumerSelector)
            .Subscribe(
                _ => { },
                _ => Volatile.Write(ref consumerTerminalThread, Environment.CurrentManagedThreadId),
                () => Volatile.Write(ref consumerTerminalThread, Environment.CurrentManagedThreadId));

        var gateHeld = 0;
        var drainReturned = 0;
        Thread? holder = null;
        try
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref subscribed) == 1, Timeout10)
                .Should().BeTrue("precondition: the pooled subscribe ran and the drain registration exists");

            // One frame through the feed, so the SelectMany is past its first inner (which completes at
            // once) and an outer completion would go straight to Final() — the incident's frame.
            feed.OnNext(1);

            var gate = SelectManyGate(poolsObserver!);
            gate.Should().NotBeNull(
                "precondition: the SelectMany's own gate was found on the consumer's observer chain — "
                + "without it this test would not be holding the lock the incident's Final() waited on");

            holder = new Thread(() =>
            {
                lock (gate!)
                {
                    Volatile.Write(ref gateHeld, 1);
                    // The other half of the cycle: hold the consumer's lock until teardown has returned.
                    SpinWait.SpinUntil(() => Volatile.Read(ref drainReturned) == 1, Timeout10);
                }
            })
            {
                IsBackground = true,
                Name = "holds the SelectMany gate until teardown",
            };
            holder.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref gateHeld) == 1, Timeout10)
                .Should().BeTrue("precondition: another thread holds the consumer's SelectMany gate");

            var sw = Stopwatch.StartNew();
            var residual = registry.DrainAll(out var byPool);
            sw.Stop();
            Volatile.Write(ref drainReturned, 1);

            residual.Should().Be(0,
                "the pool's cancel must never wait on a consumer's lock — it runs no consumer code, so a "
                + "held SelectMany gate cannot park it. Residual reported: [{0}]",
                string.Join(", ", byPool));
            sw.Elapsed.Should().BeLessThan(DrainBudget,
                "the cancel join returns because the callbacks FINISHED, not because the budget expired — "
                + "the incident's join only ever returned on its budget");
            Volatile.Read(ref consumerTerminalThread).Should().Be(0,
                "the pool delivered NOTHING to the consumer: a terminal from the pool's cancel is consumer "
                + "code on the pool's thread, which is what parked the cancel in #7968");
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"drain {sw.Elapsed.TotalMilliseconds:0} ms, residual {residual}; leak: {string.Join(" | ", pool.UnownedLeakSites)}");
            pool.UnownedLeaks.Should().Be(1, "the subscription was still attached when the pool was cancelled");
            pool.UnownedLeakSites.Should().ContainSingle()
                .Which.Should().Contain(nameof(ConsumerSelector),
                    "the leak names the consumer — its SelectMany selector — so the next occurrence says "
                    + "which code to give an owner, which the incident's stack (all System.__Canon) could not");
        }
        finally
        {
            Volatile.Write(ref drainReturned, 1);
            holder?.Join(Timeout10);
            subscription.Dispose();
        }
    }

    /// <summary>
    /// The owned shape — what every consumer must be: its owner disposes the subscription before the
    /// pool drains, its <c>.Finally</c> runs on the OWNER's thread, and the drain finds nothing.
    /// </summary>
    [Fact]
    public void AnOwnedSubscription_ReleasesItsFinallyOnTheOwnersThread_AndTheDrainFindsNoLeak()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions { DrainTimeout = DrainBudget });
        var pool = (IoPool)registry.Get(IoPoolNames.Query);
        var feed = new Subject<int>();
        var subscribed = 0;
        var finallyThread = 0;

        var subscription = pool
            .SubscribeThroughPool(Observable.Create<int>(o =>
            {
                var d = feed.Subscribe(o);
                Volatile.Write(ref subscribed, 1);
                return d;
            }))
            .SelectMany(ConsumerSelector)
            .Finally(() => Volatile.Write(ref finallyThread, Environment.CurrentManagedThreadId))
            .Subscribe(_ => { }, _ => { });

        SpinWait.SpinUntil(() => Volatile.Read(ref subscribed) == 1, Timeout10)
            .Should().BeTrue("precondition: the pooled subscribe ran");

        // The owner's teardown (a hub's disposal, the routing gauge's release) — strictly before the drain.
        subscription.Dispose();

        Volatile.Read(ref finallyThread).Should().Be(Environment.CurrentManagedThreadId,
            "the consumer's bookkeeping runs on its OWNER's thread when the owner disposes it — never on "
            + "the pool's");
        feed.HasObservers.Should().BeFalse("disposing the leg tore the source down too");

        registry.DrainAll(out var byPool).Should().Be(0, "nothing is in flight: [{0}]", string.Join(", ", byPool));
        pool.UnownedLeaks.Should().Be(0, "an owned subscription is never a leak");
    }

    /// <summary>
    /// Finds the private gate of the <c>SelectMany</c> sink on the observer chain the pool holds. A
    /// test-only reflection walk over Rx internals, asserted as a precondition by the caller.
    /// </summary>
    private static object? SelectManyGate(object observer)
    {
        object? current = observer;
        for (var depth = 0; current is not null && depth < 16; depth++)
        {
            var type = current.GetType();
            if ((type.FullName ?? "").Contains("SelectMany", StringComparison.Ordinal))
                return FindField(type, "_gate")?.GetValue(current);
            current = FindField(type, "_observer")?.GetValue(current);
        }
        return null;
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } f)
                return f;
        return null;
    }
}
