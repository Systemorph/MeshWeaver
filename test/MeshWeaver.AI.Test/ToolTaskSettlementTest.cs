using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the four terminals of <see cref="ToolTask.Bridge{T}"/> — the ONE bridge from a reactive
/// source to the <c>Task&lt;string&gt;</c> an agent tool hands back to <c>AIFunctionFactory</c>.
///
/// <para><b>Why every terminal has to settle (#1956).</b> A tool call runs INSIDE the round's leaf
/// on the bounded <c>IoPoolNames.Ai</c> pool and holds one gate permit for its whole duration. A
/// task that never settles is therefore not a slow tool: it is a permit held through
/// <c>IoPool.Drain()</c>, a Stop button that does nothing, and a teardown that proceeds over live
/// code. Five tools shipped with a hand-rolled <see cref="TaskCompletionSource{TResult}"/> that had
/// at least one terminal missing.</para>
///
/// <para><b>The one everybody missed is the EMPTY completion.</b> A 2-argument
/// <c>Subscribe(onNext, onError)</c> never fires for a source that completes without emitting, and
/// <c>Timeout</c> does not cover it — <c>Timeout</c> passes an empty <c>OnCompleted</c> straight
/// through. A <c>yield break</c> is a silence; the empty answer is an ANSWER.</para>
/// </summary>
public class ToolTaskSettlementTest
{
    private static Task<string> Bridge<T>(IObservable<T> source, CancellationToken token) =>
        ToolTask.Bridge(source, token, v => $"value:{v}", ex => $"error:{ex.Message}", () => "empty");

    /// <summary>
    /// The first emission is the answer, and only the first — a bridge that kept reading would keep
    /// a node stream subscribed after the round moved on.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task FirstEmission_IsTheAnswer()
    {
        var result = await Bridge(Observable.Return("hello"), TestContext.Current.CancellationToken)
            .WaitAsync(3.Seconds(), TestContext.Current.CancellationToken);

        result.Should().Be("value:hello");
    }

    /// <summary>
    /// 🚨 THE terminal the hand-rolled bridges lacked. A source that completes without ever
    /// emitting must produce the caller's empty ANSWER — not silence.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task EmptyCompletion_Answers_RatherThanParkingForever()
    {
        var result = await Bridge(Observable.Empty<string>(), TestContext.Current.CancellationToken)
            .WaitAsync(3.Seconds(), TestContext.Current.CancellationToken);

        result.Should().Be("empty",
            "a source that completes without emitting reaches neither onNext nor onError — the "
            + "bridge owes the caller the empty answer, or the round parks holding an Ai-pool permit");
    }

    /// <summary>
    /// An empty completion behind a <c>Timeout</c> still answers. This is the exact shape that made
    /// <c>SkillTool</c> LOOK guarded: <c>Timeout</c> covers the silent source, never the empty one.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task EmptyCompletion_BehindATimeout_StillAnswers()
    {
        var source = Observable.Empty<string>().Timeout(TimeSpan.FromSeconds(30));

        var result = await Bridge(source, TestContext.Current.CancellationToken)
            .WaitAsync(3.Seconds(), TestContext.Current.CancellationToken);

        result.Should().Be("empty");
    }

    /// <summary>A fault is formatted into the tool's answer — on an agent-facing tool the failure text IS the result.</summary>
    [Fact(Timeout = 10_000)]
    public async Task Error_IsFormattedIntoTheAnswer()
    {
        var result = await Bridge(Observable.Throw<string>(new InvalidOperationException("boom")),
                TestContext.Current.CancellationToken)
            .WaitAsync(3.Seconds(), TestContext.Current.CancellationToken);

        result.Should().Be("error:boom");
    }

    /// <summary>
    /// A formatter that throws must not strand the caller: Rx routes the throw down the error
    /// channel, which settles. (The hand-rolled bridges needed a try/catch per site for this.)
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ThrowingFormatter_StillSettles()
    {
        var result = await ToolTask.Bridge<string>(
                Observable.Return("x"),
                TestContext.Current.CancellationToken,
                _ => throw new InvalidOperationException("formatter blew up"),
                ex => $"error:{ex.Message}",
                () => "empty")
            .WaitAsync(3.Seconds(), TestContext.Current.CancellationToken);

        result.Should().Be("error:formatter blew up");
    }

    /// <summary>
    /// Cancellation must both END the wait and STOP the work. The source here never emits and never
    /// completes — the shape of a read against a hub that is still activating — and records its own
    /// disposal, so this asserts the operation actually stopped rather than merely that the caller
    /// stopped listening.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task Cancellation_UnwindsTheTaskAndDisposesTheSource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var round = new CancellationTokenSource();
        var disposed = false;
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var source = Observable.Create<string>(_ =>
        {
            subscribed.TrySetResult();
            return Disposable.Create(() => disposed = true);
        });

        var call = Bridge(source, round.Token);
        await subscribed.Task.WaitAsync(5.Seconds(), ct);

        // It must genuinely be waiting, or "prompt" below would prove nothing.
        var settledEarly = await Task.WhenAny(call, Task.Delay(250, ct));
        settledEarly.Should().NotBeSameAs(call);
        disposed.Should().BeFalse();

        round.Cancel();

        // WaitAsync's own failure is a TimeoutException, which is NOT an OperationCanceledException
        // — so this cannot be satisfied by the wait giving up.
        var act = async () => await call.WaitAsync(5.Seconds(), ct);
        await act.Should().ThrowAsync<OperationCanceledException>();

        disposed.Should().BeTrue(
            "cancelling must dispose the subscription — otherwise the tool's work runs on "
            + "unobserved against a hub that is being torn down");
    }

    /// <summary>
    /// An already-cancelled token settles immediately and leaves nothing subscribed, so a tool
    /// invoked during teardown does not start work it cannot finish.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task AlreadyCancelledToken_SettlesWithoutLeavingASubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        using var round = new CancellationTokenSource();
        await round.CancelAsync();

        var disposed = false;
        var source = Observable.Create<string>(_ => Disposable.Create(() => disposed = true));

        var act = async () => await Bridge(source, round.Token).WaitAsync(5.Seconds(), ct);
        await act.Should().ThrowAsync<OperationCanceledException>();

        disposed.Should().BeTrue();
    }

    /// <summary>
    /// Once settled, a later emission from a source that has not noticed yet must not throw or
    /// re-settle — the bridge is a one-shot.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task LateEmissionAfterSettlement_IsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = new Subject<string>();
        using var round = new CancellationTokenSource();

        var call = Bridge(subject, round.Token);
        round.Cancel();

        var act = async () => await call.WaitAsync(5.Seconds(), ct);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // The bridge unsubscribed on cancel; pushing more must be a no-op, not an exception.
        subject.OnNext("late");
        subject.OnCompleted();
    }

    /// <summary>
    /// 🚨 ORDER, not merely occurrence: on cancellation the subscription must be DISPOSED before the
    /// caller can observe that the call ended (#2346).
    ///
    /// <para><b>Why the order is the invariant.</b> The bridge's <c>TaskCompletionSource</c> is
    /// created <c>RunContinuationsAsynchronously</c>, so <c>TrySetCanceled</c> completes the task and
    /// schedules the caller's continuation on the pool — which then runs CONCURRENTLY with whatever
    /// the cancellation callback does next. Settling first therefore let the caller act on "the call
    /// ended" while the tool's work was still live. A tool call is a leaf on the bounded Ai pool, and
    /// <c>IoPool.Drain()</c> — the join every teardown performs before disposing the service scope
    /// and unloading collectible node ALCs — cancels the pool token and then re-acquires permits. So
    /// "cancelled but not yet torn down" is teardown proceeding over live code, which is the hazard
    /// this whole file exists for.</para>
    ///
    /// <para><b>How it surfaced.</b> As
    /// <c>AgentToolCancellationTest.GetVersions_ParkedOnAStalledStore_UnwindsWhenTheRoundIsCancelled</c>
    /// failing <i>"Expected 1 to be greater than 1 because cancelling must dispose the version
    /// read"</i> in 0.72 s — the test read the store's disposal counter the instant its task threw,
    /// and on a loaded runner the caller's continuation won the race against the dispose. It fails on
    /// unrelated branches (a docs-only PR among them), so it reads as a flake; it is an ordering
    /// defect with a flaky OBSERVER.</para>
    ///
    /// <para>Deterministic: the source's disposal parks on a gate this test holds, so "the caller
    /// observed the cancellation while teardown was still running" is CONSTRUCTED rather than raced
    /// for. No sleep, no timing assumption.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Cancelling_StopsTheWork_BeforeTheCallerCanObserveIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var disposeEntered = new ManualResetEventSlim(false);
        using var releaseDispose = new ManualResetEventSlim(false);
        var disposals = 0;

        // A source that ACCEPTS the subscription and never answers — the shape of a stalled storage
        // leaf — whose teardown parks until this test lets it finish.
        var parked = Observable.Create<string>(_ => Disposable.Create(() =>
        {
            disposeEntered.Set();
            releaseDispose.Wait(TimeSpan.FromSeconds(20));
            Interlocked.Increment(ref disposals);
        }));

        using var round = new CancellationTokenSource();
        var call = Bridge(parked, round.Token);

        // Cancel off the test's thread: the cancellation callback is where the teardown parks, and
        // the test has to stay free to observe the task while it is parked.
        var cancelling = Task.Run(() => round.Cancel(), ct);

        disposeEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
            "cancelling must reach the source's disposal");

        // THE ASSERTION. The work is provably mid-teardown right now, so the caller must not yet be
        // able to see the call as ended. Pre-fix TrySetCanceled had already run and this is true.
        call.IsCompleted.Should().BeFalse(
            "the caller must not observe the cancellation while the tool's work is still being torn "
            + "down — a teardown that acts on it would be unloading assemblies out from under live code");

        releaseDispose.Set();
        await cancelling;

        var act = async () => await call.WaitAsync(10.Seconds(), ct);
        await act.Should().ThrowAsync<OperationCanceledException>();
        Volatile.Read(ref disposals).Should().Be(1,
            "the subscription is disposed exactly once, and before the task settles");
    }
}
