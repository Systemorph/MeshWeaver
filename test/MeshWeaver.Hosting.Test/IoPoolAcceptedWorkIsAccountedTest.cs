using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Issue #4555 — between a caller's <c>Subscribe()</c> returning and the ThreadPool running the
/// leaf's prologue, the leaf was counted by NOTHING: not <c>_gateUsers</c>, not <c>_inFlight</c>, not
/// <see cref="IIoPool.CurrentlyWaiting"/>. The caller believed the work was queued; the pool did not
/// know it existed.
///
/// <para><b>What that costs.</b> <see cref="IoPool.Drain"/> waits while anything is outstanding and
/// gives it a grace. A leaf inside this window is outstanding to nobody, so the drain ends its grace
/// — or never enters it — and cancels the pool token. The leaf then arrives, finds the token
/// cancelled, and is discarded: too late even to be counted in
/// <see cref="IoPool.LeavesCancelledAfterGrace"/>, which is captured before the cancel. Accepted work,
/// thrown away, reported nowhere — the opposite of what #3291 established (teardown lets accepted work
/// finish and NAMES what it had to stop).</para>
///
/// <para><b>Measured on main before the fix</b>, subscribing a leaf and draining immediately: 50 of
/// 50 attempts for <c>Invoke</c> and 50 of 50 for <c>InvokeStream</c> ended with the work never run,
/// the subscriber cancelled, and <c>LeavesCancelledAfterGrace == 0</c>.</para>
///
/// <para>The window is a ThreadPool hop wide, so the test does not race it: it parks the prologue on
/// <c>IoPool.OnLeafPrologueStarting</c>, which puts the leaf INSIDE the window by construction, and
/// then asks the pool what it knows.</para>
/// </summary>
public class IoPoolAcceptedWorkIsAccountedTest
{
    /// <summary>A drain grace short enough to spend in a test, long enough to be unmistakable next to a drain that never waits at all.</summary>
    private static TimeSpan ShortGrace => TestTimeouts.Quick / 10;

    public static TheoryData<string> EntryPoints() => new("Invoke", "InvokeStream");

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task ALeafInsideTheSubscribeWindow_IsAccountedByTheDrain_NotSilentlyDiscarded(string entryPoint)
    {
        using var pool = new IoPool(2, IoPool.DefaultDrainTimeout, ShortGrace);
        var prologueParked = new AsyncSubject<Unit>();
        var terminal = new AsyncSubject<Unit>();
        var releasePrologue = 0;
        var workRan = 0;
        var terminalKind = "none";

        pool.OnLeafPrologueStarting = () =>
        {
            prologueParked.OnNext(Unit.Default);
            prologueParked.OnCompleted();
            // The park IS the subject — a bounded spin on a volatile flag, released in the finally.
            SpinWait.SpinUntil(() => Volatile.Read(ref releasePrologue) == 1, TestTimeouts.Quick);
        };

        void Terminal(string kind)
        {
            terminalKind = kind;
            terminal.OnNext(Unit.Default);
            terminal.OnCompleted();
        }

        var leg = entryPoint == "Invoke"
            ? pool.Invoke(_ => { Volatile.Write(ref workRan, 1); return Task.FromResult(1); })
            : pool.InvokeStream(_ => OneItem(() => Volatile.Write(ref workRan, 1)));

        using var subscription = leg.Subscribe(
            _ => { },
            ex => Terminal(ex.GetType().Name),
            () => Terminal("OnCompleted"));

        try
        {
            await prologueParked.Should().Within(TestTimeouts.Quick).Emit(
                "precondition: Subscribe() has returned and the leaf's prologue has not reached the "
                + "pool — the window this test is about",
                cancellationToken: TestContext.Current.CancellationToken);

            var elapsed = Stopwatch.StartNew();
            pool.Drain();
            elapsed.Stop();

            Volatile.Write(ref releasePrologue, 1);

            await terminal.Should().Within(TestTimeouts.Quick).Emit(
                "the leaf must terminate either way — it is cancelled here, and a cancelled leg still "
                + "owes its subscriber a terminal",
                cancellationToken: TestContext.Current.CancellationToken);

            Volatile.Read(ref workRan).Should().Be(0,
                "the drain cancelled it before it could run — which is the premise of the assertions below, "
                + $"not the defect (terminal was {terminalKind})");

            // 🚨 THE TWO HALVES OF #3291'S CONTRACT, on work the caller had already handed over.
            pool.LeavesCancelledAfterGrace.Should().BeGreaterThan(0,
                "a leaf whose Subscribe() returned before the drain began is ACCEPTED work: when the "
                + "drain has to cancel it, it must be COUNTED. Before #4555 the pool could not see it at "
                + "all, so it was discarded in silence — LeavesCancelledAfterGrace read 0 while the "
                + "subscriber got a cancellation");
            // Half the grace, not the whole of it: SpinUntil measures its own budget on a clock this
            // Stopwatch did not start, and returned 0.6 ms short of it here. The discrimination does
            // not need the precision — a drain that cannot see the leaf returns in microseconds, one
            // that can spends a grace.
            elapsed.Elapsed.Should().BeGreaterThan(ShortGrace / 2,
                "and it must be given the GRACE first: a drain that returns without waiting has not "
                + "offered accepted work its chance to finish, it has only failed to notice it");
        }
        finally
        {
            Volatile.Write(ref releasePrologue, 1);
        }
    }

    private static async IAsyncEnumerable<int> OneItem(
        Action onRun,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        onRun();
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return 1;
    }
}
