using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Reactive;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins <see cref="ParallelReadExtensions"/> and the Rx 7.0.0 <c>Zip</c> hazard it exists for (Doc/Architecture/AsynchronousCalls,
/// "Combining reads"). The hostile source below models what a serialising hub read does: its delivery runs under a lock, and
/// disposing it needs that same lock. Signals between the test's threads are volatile flags polled under a bounded
/// <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/> — no event, no semaphore, no blocking Rx wait (the repo's two ratchets).
/// </summary>
public class ParallelReadExtensionsTests
{
    /// <summary>A source that delivers under <see cref="Lock"/> and whose Dispose takes the same lock.</summary>
    private sealed class LockedSource<T> : IObservable<T>
    {
        public readonly object Lock = new();
        public IObserver<T>? Observer;
        public IDisposable Subscribe(IObserver<T> observer)
        {
            Observer = observer;
            return System.Reactive.Disposables.Disposable.Create(() => { lock (Lock) { } });
        }
        public void Deliver(Action<IObserver<T>> what)
        {
            lock (Lock) what(Observer ?? throw new InvalidOperationException("not subscribed"));
        }
    }

    private sealed class Flag
    {
        private volatile bool _set;
        public void Set() => _set = true;
        public bool Raised() => SpinWait.SpinUntil(() => _set, Bound);
    }

    /// <summary>All parties arrive before any proceeds (a barrier without a barrier object).</summary>
    private sealed class Rendezvous(int parties)
    {
        private int _arrived;
        public void Arrive()
        {
            Interlocked.Increment(ref _arrived);
            SpinWait.SpinUntil(() => Volatile.Read(ref _arrived) >= parties, Bound);
        }
    }

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>The single value of a source that answers synchronously inside Subscribe.</summary>
    private static T Sync<T>(IObservable<T> source)
    {
        var values = new List<T>();
        Exception? error = null;
        source.Subscribe(values.Add, e => error = e);
        if (error is not null)
            throw error;
        return values.Single();
    }

    // ───────────── the mechanism ─────────────

    /// <summary>
    /// Deterministic repro of the Rx 7.0.0 Zip hazard: thread A is inside Zip's gate (downstream Take(1) ends the pipeline and
    /// disposes the second source), thread B holds the second source's delivery lock and is waiting for that gate. The two threads
    /// stay blocked for ever (they are leaked background threads — that is the point of the test).
    /// </summary>
    [Fact]
    public void Zip_WedgesWhenAnEndedPipelineDisposesASourceWhoseDeliveryIsWaitingOnTheGate()
    {
        var s1 = new LockedSource<int>();
        var s2 = new LockedSource<int>();
        var inGate = new Flag();
        var bHoldsLock = new Flag();
        s1.Zip(s2, (a, b) => a + b).Take(1).Subscribe(_ => inGate.Set(), _ => { });
        s2.Deliver(o => o.OnNext(2)); // queued in Zip

        var b = new Thread(() => s2.Deliver(o =>
        {
            bHoldsLock.Set();
            inGate.Raised();            // A is now inside Zip's gate...
            o.OnCompleted();          // ...so this blocks on it while holding s2's lock
        })) { IsBackground = true };
        var a = new Thread(() =>
        {
            bHoldsLock.Raised();
            s1.Deliver(o => o.OnNext(1)); // inside the gate: pair → Take(1) → dispose s2 → needs s2.Lock
        }) { IsBackground = true };
        b.Start(); a.Start();

        a.Join(TimeSpan.FromSeconds(3)).Should().BeFalse("Zip held its gate across a disposal that needs a lock its other source's thread holds");
    }

    [Fact]
    public void WhenBoth_DoesNotWedgeOnTheSameShape()
    {
        var s1 = new LockedSource<int>();
        var s2 = new LockedSource<int>();
        var inDownstream = new Flag();
        var bHoldsLock = new Flag();
        var completed = new Flag();
        var results = new List<int>();
        s1.WhenBoth(s2, (a, b) => a + b).Take(1).Subscribe(v => { lock (results) results.Add(v); inDownstream.Set(); }, _ => { }, completed.Set);
        s2.Deliver(o => o.OnNext(2));

        var b = new Thread(() => s2.Deliver(o => { bHoldsLock.Set(); inDownstream.Raised(); o.OnCompleted(); })) { IsBackground = true };
        var a = new Thread(() => { bHoldsLock.Raised(); s1.Deliver(o => o.OnNext(1)); }) { IsBackground = true };
        b.Start(); a.Start();

        a.Join(Bound).Should().BeTrue();
        b.Join(Bound).Should().BeTrue();
        completed.Raised().Should().BeTrue();
        lock (results) results.Should().Equal(3);
    }

    // ───────────── stress ─────────────

    [Fact]
    public void WhenBoth_ManyIterations_TwoPoolThreadsCompletingAtOnce_NeverWedges()
    {
        for (var i = 0; i < 500; i++)
        {
            var s1 = new LockedSource<int>();
            var s2 = new LockedSource<int>();
            var done = new Flag();
            var seen = new List<int>();
            s1.WhenBoth(s2, (a, b) => a * 10 + b).Take(1).Subscribe(v => { lock (seen) seen.Add(v); }, _ => { }, done.Set);
            var start = new Rendezvous(2);
            Task.Run(() => { start.Arrive(); s1.Deliver(o => { o.OnNext(1); o.OnCompleted(); }); });
            Task.Run(() => { start.Arrive(); s2.Deliver(o => { o.OnNext(2); o.OnCompleted(); }); });
            done.Raised().Should().BeTrue($"iteration {i} wedged");
            lock (seen) seen.Should().Equal(12);
        }
    }

    [Fact]
    public void WhenAll_ManyIterations_FiveSourcesOnPoolThreads_EmitExactlyOnce()
    {
        for (var i = 0; i < 300; i++)
        {
            var sources = Enumerable.Range(0, 5).Select(_ => new LockedSource<int>()).ToArray();
            var done = new Flag();
            var count = 0;
            IReadOnlyList<int>? got = null;
            sources.Select(s => (IObservable<int>)s).WhenAll().Subscribe(v => { Interlocked.Increment(ref count); got = v; }, _ => { }, done.Set);
            var start = new Rendezvous(5);
            for (var k = 0; k < 5; k++)
            {
                var (s, v) = (sources[k], k);
                Task.Run(() => { start.Arrive(); s.Deliver(o => { o.OnNext(v); o.OnCompleted(); }); });
            }
            done.Raised().Should().BeTrue($"iteration {i} wedged");
            Volatile.Read(ref count).Should().Be(1);
            got.Should().Equal(0, 1, 2, 3, 4);
        }
    }

    // ───────────── semantics ─────────────

    [Fact]
    public void WhenBoth_PairsFirstValues_ThenCompletes()
    {
        var a = new Subject<int>();
        var b = new Subject<string>();
        var got = new List<(int, string)>();
        var completed = false;
        a.WhenBoth(b).Subscribe(got.Add, _ => { }, () => completed = true);
        a.OnNext(1); a.OnNext(99);
        got.Should().BeEmpty();
        b.OnNext("x");
        got.Should().Equal((1, "x"));
        completed.Should().BeTrue();
    }

    [Fact]
    public void WhenBoth_FirstError_IsForwarded_AndEndsTheOutput()
    {
        var a = new Subject<int>();
        var b = new Subject<int>();
        Exception? error = null;
        var values = 0;
        a.WhenBoth(b, (x, y) => x + y).Subscribe(_ => values++, e => error = e);
        a.OnError(new InvalidOperationException("boom"));
        b.OnNext(1);
        error.Should().BeOfType<InvalidOperationException>();
        values.Should().Be(0);
    }

    [Fact]
    public void WhenBoth_ASourceThatEndsEmpty_CompletesWithoutValue()
    {
        var b = new Subject<int>();
        var values = 0;
        var completed = false;
        Observable.Empty<int>().WhenBoth(b, (x, y) => x + y).Subscribe(_ => values++, _ => { }, () => completed = true);
        completed.Should().BeTrue();
        values.Should().Be(0);
    }

    [Fact]
    public void WhenBoth_ThrowingSelector_FaultsTheOutput()
    {
        Exception? error = null;
        Observable.Return(1).WhenBoth(Observable.Return(2), (x, y) => x / (y - 2))
            .Subscribe(_ => { }, e => error = e);
        error.Should().BeOfType<DivideByZeroException>();
    }

    [Fact]
    public void WhenBoth_Dispose_UnsubscribesEverySource_AndDeliversNothing()
    {
        var a = new Subject<int>();
        var b = new Subject<int>();
        var values = 0;
        var sub = a.WhenBoth(b, (x, y) => x + y).Subscribe(_ => values++, _ => { });
        a.HasObservers.Should().BeTrue();
        b.HasObservers.Should().BeTrue();
        sub.Dispose();
        a.HasObservers.Should().BeFalse();
        b.HasObservers.Should().BeFalse();
        a.OnNext(1); b.OnNext(2);
        values.Should().Be(0);
    }

    [Fact]
    public void WhenBoth_ReleasesTheOtherSourceOnAnError()
    {
        var a = new Subject<int>();
        var b = new Subject<int>();
        a.WhenBoth(b, (x, y) => x + y).Subscribe(_ => { }, _ => { });
        a.OnError(new Exception("x"));
        b.HasObservers.Should().BeFalse();
    }

    [Fact]
    public void WhenBoth_IsCold_EachSubscriptionSubscribesAgain()
    {
        var subscriptions = 0;
        var src = Observable.Defer(() => { subscriptions++; return Observable.Return(1); });
        var both = src.WhenBoth(src, (x, y) => x + y);
        both.Subscribe(_ => { }, _ => { });
        both.Subscribe(_ => { }, _ => { });
        subscriptions.Should().Be(4);
    }

    [Fact]
    public void WhenAll_ThreeAndFour_CombineInOrder()
    {
        Sync(ParallelReadExtensions.WhenAll(Observable.Return(1), Observable.Return("b"), Observable.Return(3.0), (x, y, z) => $"{x}{y}{z}"))
            .Should().Be("1b3");
        Sync(ParallelReadExtensions.WhenAll(Observable.Return(1), Observable.Return(2), Observable.Return(3), Observable.Return(4), (a, b, c, d) => a + b + c + d))
            .Should().Be(10);
    }

    [Fact]
    public void WhenAll_FiveToEight_CombineInOrder()
    {
        IObservable<int> R(int v) => Observable.Return(v);
        Sync(R(1).WhenAll(R(2), R(3), R(4), R(5), (a, b, c, d, e) => $"{a}{b}{c}{d}{e}")).Should().Be("12345");
        Sync(R(1).WhenAll(R(2), R(3), R(4), R(5), R(6), (a, b, c, d, e, f) => $"{a}{b}{c}{d}{e}{f}")).Should().Be("123456");
        Sync(R(1).WhenAll(R(2), R(3), R(4), R(5), R(6), R(7), (a, b, c, d, e, f, g) => $"{a}{b}{c}{d}{e}{f}{g}")).Should().Be("1234567");
        Sync(R(1).WhenAll(R(2), R(3), R(4), R(5), R(6), R(7), R(8), (a, b, c, d, e, f, g, h) => $"{a}{b}{c}{d}{e}{f}{g}{h}")).Should().Be("12345678");
    }

    [Fact]
    public void WhenBoth_ANullReadResult_IsDelivered_NotDropped()
    {
        Sync(Observable.Return<string?>(null).WhenBoth(Observable.Return<string?>("x"), (a, b) => (a, b)))
            .Should().Be(((string?)null, (string?)"x"));
    }

    [Fact]
    public void WhenAll_EmptyList_EmitsOneEmptyList()
    {
        Sync(Enumerable.Empty<IObservable<int>>().WhenAll()).Should().BeEmpty();
    }

    [Fact]
    public void WhenAll_OneFaultAmongMany_FaultsOnce()
    {
        var errors = 0;
        new[] { Observable.Return(1), Observable.Throw<int>(new Exception("x")), Observable.Return(3) }
            .WhenAll().Subscribe(_ => { }, _ => errors++);
        errors.Should().Be(1);
    }

    [Fact]
    public void WhenBoth_RunsTheReadsInParallel_NotOneAfterTheOther()
    {
        var delay = TimeSpan.FromMilliseconds(500);
        IObservable<int> Slow(int v) => Observable.Timer(delay).Select(_ => v);
        var done = new Flag();
        var result = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Slow(1).WhenBoth(Slow(2), (a, b) => a + b).Subscribe(v => result = v, _ => { }, done.Set);
        done.Raised().Should().BeTrue();
        sw.Stop();
        result.Should().Be(3);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(950), "two 500 ms reads must overlap");
    }

    [Fact]
    public void NullArguments_Throw()
    {
        // Null is passed through reflection so no null-forgiving operator is needed to compile the call.
        static void Throws(string method, params object?[] args)
        {
            var m = typeof(ParallelReadExtensions).GetMethods().First(x => x.Name == method && x.GetParameters().Length == args.Length)
                .MakeGenericMethod(Enumerable.Repeat(typeof(int), args.Length == 1 ? 1 : 3).ToArray());
            Action act = () => m.Invoke(null, args);
            act.Should().Throw<System.Reflection.TargetInvocationException>().WithInnerException<ArgumentNullException>();
        }

        Throws(nameof(ParallelReadExtensions.WhenBoth), null, Observable.Return(1), (Func<int, int, int>)((a, b) => a));
        Throws(nameof(ParallelReadExtensions.WhenBoth), Observable.Return(1), Observable.Return(1), null);
        Throws(nameof(ParallelReadExtensions.WhenAll), new object?[] { null });
    }
}
