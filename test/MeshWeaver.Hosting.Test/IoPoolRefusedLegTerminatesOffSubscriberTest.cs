using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #4530 — a leg the pool REFUSES must not terminate on the thread that subscribed it.
///
/// <para>A refusal is what every entry point answers once the pool is terminal: the build-time
/// <c>Cancelled&lt;T&gt;()</c> for a leg issued after <see cref="IoPool.Drain"/>/<see cref="IoPool.Dispose"/>,
/// and the admission region's own refusal for a leg that was built while the pool was alive and
/// subscribed after disposal began. Both used <c>Observable.Throw</c>/<c>observer.OnError</c> on the
/// IMMEDIATE scheduler, so the terminal — and every <c>.Finally</c>, every downstream teardown
/// hanging off it — ran inside the caller's <c>Subscribe()</c> call, on the caller's thread. That
/// thread is a hub action block, a grain turn, or the pool's own cancel thread during teardown, which
/// is the one place this pool exists to keep work out of (#2394, #4524).</para>
///
/// <para>Measured on main before the fix, 50 subscribes per cell from a dedicated thread: every
/// refusal below was delivered on the subscriber's thread, inside <c>Subscribe()</c>, 50/50, at
/// 0.3–1.2 µs. Afterwards: 0/50 on the subscriber, 7–12 µs — the pool's own
/// <c>TaskPoolScheduler</c>, which is where every ADMITTED leaf's terminal already comes from
/// (13–30 µs on the same measurement).</para>
///
/// <para>Since #4545 the matrix also carries the cell that delivered NOTHING — a blocking leaf the
/// pool ENDED rather than refused. Its terminal is a cancellation-shaped fault, not a completion,
/// because <c>InvokeBlocking</c> is a single-value surface and callers fold an empty completion into
/// a VALUE (see the issue and <c>Doc/Architecture/ControlledIoPooling</c>).</para>
///
/// <para>The consequence this protects is in
/// <c>OrderedRouteDispatcherDrainRecursionTest</c>: a consumer that subscribes the next leg from the
/// previous leg's terminal walks its whole backlog by recursion when a refusal completes inside the
/// subscribe.</para>
/// </summary>
public class IoPoolRefusedLegTerminatesOffSubscriberTest
{
    private sealed record Cell(
        string Name,
        Func<IIoPool, IObservable<int>> Build,
        Action<IoPool> Kill,
        bool BuildBeforeKill,
        bool ExpectsCancellationFault = true);

    private sealed record Outcome(string Kind, bool IsCancellationFault, bool OnSubscriberThread, bool InsideSubscribe);

    private static async IAsyncEnumerable<int> OneItem([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return 1;
    }

    /// <summary>
    /// Every way the four entry points can end a leg they will not carry.
    ///
    /// <para>🚨 The last cell was the hole: <c>InvokeBlocking</c> built BEFORE a drain and subscribed
    /// after is not refused at build time at all — its task is created with an already-cancelled
    /// token, so the delegate never runs and the continuation's <c>IsCanceled</c> arm used to deliver
    /// NOTHING (measured: 0 terminals in 50 subscribes). #4545 gave that arm a terminal, so the cell
    /// is now asserted here with the other twelve rather than named as an exclusion.</para>
    /// </summary>
    private static IEnumerable<Cell> Cells()
    {
        var builders = new (string Name, Func<IIoPool, IObservable<int>> Build)[]
        {
            ("Invoke", pool => pool.Invoke(_ => Task.FromResult(1))),
            ("InvokeStream", pool => pool.InvokeStream(OneItem)),
            ("InvokeBlocking", pool => pool.InvokeBlocking(_ => 1)),
            ("SubscribeThroughPool", pool => pool.SubscribeThroughPool(Observable.Never<int>())),
        };

        foreach (var (name, build) in builders)
        {
            // Built once the pool is terminal — the build-time refusal.
            yield return new Cell($"{name} built after Drain()", build, pool => pool.Drain(), BuildBeforeKill: false);
            yield return new Cell($"{name} built after Dispose()", build, pool => pool.Dispose(), BuildBeforeKill: false);
            // Built while the pool was alive, subscribed after disposal began — the admission
            // region's refusal, which is reached on SUBSCRIBE and so cannot be answered at build time.
            yield return new Cell($"{name} built before Dispose(), subscribed after", build, pool => pool.Dispose(), BuildBeforeKill: true);
            // Built while the pool was alive, subscribed after the DRAIN — admitted, then ended by
            // the pool. InvokeBlocking is the cell that delivered nothing until #4545.
            yield return new Cell(
                $"{name} built before Drain(), subscribed after",
                build,
                pool => pool.Drain(),
                BuildBeforeKill: true,
                // 🚨 The ONE cell that must COMPLETE. SubscribeThroughPool is a subscription surface:
                // its terminal carries no value, so a drain ends the feed (#1789). Every other cell
                // is a value surface, where an empty completion is read as "the IO produced nothing"
                // and folded into a value by callers (DefaultIfEmpty → Undeclared / present) — so it
                // must FAULT with a cancellation (#4545).
                ExpectsCancellationFault: name != "SubscribeThroughPool");
        }
    }

    [Fact]
    public async Task ARefusedLeg_NeverTerminatesOnTheSubscribersThread()
    {
        var violations = ImmutableArray<string>.Empty;

        foreach (var cell in Cells())
        {
            var outcome = await Refuse(cell);

            if (outcome.Kind == "none")
                violations = violations.Add($"{cell.Name}: no terminal at all");
            // 🚨 THE THREAD IS THE PROPERTY — not "was the subscriber still inside Subscribe()".
            // A pool thread delivering CONCURRENTLY with the subscriber's call is the correct
            // behaviour and sets that flag whenever it wins the handful of instructions between
            // Subscribe() returning and the flag being cleared. Asserting on it made this test fail
            // inside the full suite while passing alone — an instrument measuring the test's own
            // scheduling, not the pool's. It stays in the REPORT, where it tells inline
            // (`onSubscriberThread=True, insideSubscribe=True`, the pre-fix reading, 50/50) from a
            // late delivery on the same thread.
            else if (outcome.OnSubscriberThread)
                violations = violations.Add($"{cell.Name}: {outcome.Kind} on the subscriber's thread "
                    + $"(insideSubscribe={outcome.InsideSubscribe})");
            // 🚨 THE KIND IS PART OF THE CONTRACT, not decoration: a regression from the cancellation
            // fault to OnCompleted is exactly what callers fold into a VALUE through DefaultIfEmpty,
            // and a matrix that only asked "did something arrive" would pass straight over it
            // (Copilot review).
            else if (outcome.IsCancellationFault != cell.ExpectsCancellationFault)
                violations = violations.Add(
                    $"{cell.Name}: terminal was {outcome.Kind}, expected "
                    + (cell.ExpectsCancellationFault ? "a cancellation fault" : "OnCompleted"));
        }

        violations.Should().BeEmpty(
            "a refused leg's terminal runs the caller's downstream teardown, and the caller is a hub "
            + "action block, a grain turn, or the pool's cancel thread — the pool exists to keep work "
            + "off it, and a consumer that subscribes its next leg from that terminal "
            + "(OrderedRouteDispatcher.DrainNext) recurses through its whole backlog when it is inline "
            + "(#4530). Violations: [{0}]",
            string.Join(" | ", violations));
    }

    private static async Task<Outcome> Refuse(Cell cell)
    {
        using var pool = new IoPool(2);
        var leg = cell.BuildBeforeKill ? cell.Build(pool) : null;
        cell.Kill(pool);
        leg ??= cell.Build(pool);

        var terminal = new AsyncSubject<Unit>();
        var kind = "none";
        var subscriberThread = 0;
        var terminalThread = 0;
        var insideSubscribe = 0;
        var insideAtTerminal = 0;

        var isCancellationFault = false;

        void Terminal(string what, Exception? error = null)
        {
            kind = what;
            isCancellationFault = error is OperationCanceledException;
            terminalThread = Environment.CurrentManagedThreadId;
            insideAtTerminal = Volatile.Read(ref insideSubscribe);
            terminal.OnNext(Unit.Default);
            terminal.OnCompleted();
        }

        IDisposable? subscription = null;
        // A dedicated thread stands in for the hub turn: it is never a pool thread, so "the terminal
        // ran on it" cannot be confused with a pool thread that happens to be reused.
        var subscriber = new Thread(() =>
        {
            subscriberThread = Environment.CurrentManagedThreadId;
            Volatile.Write(ref insideSubscribe, 1);
            subscription = leg.Subscribe(
                _ => { },
                ex => Terminal("OnError:" + ex.GetType().Name, ex),
                () => Terminal("OnCompleted"));
            Volatile.Write(ref insideSubscribe, 0);
        })
        {
            IsBackground = true,
            Name = "subscriber (hub turn)",
        };
        subscriber.Start();

        try
        {
            await terminal.Should().Within(TestTimeouts.Quick).Emit(
                $"{cell.Name}: a refused leg must TERMINATE — its .Finally is what releases a route "
                + "slot and advances the destination's FIFO (#1789)",
                cancellationToken: TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // A cell that never terminates is reported as a violation by the caller, with its name,
            // rather than as an anonymous timeout from inside this helper.
            return new Outcome("none", IsCancellationFault: false, OnSubscriberThread: false, InsideSubscribe: false);
        }
        finally
        {
            subscriber.Join(TestTimeouts.Quick);
            subscription?.Dispose();
        }

        return new Outcome(kind, isCancellationFault, terminalThread == subscriberThread, insideAtTerminal == 1);
    }
}
