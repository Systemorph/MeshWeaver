using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Regression tests for the OTHER half of issue #899 — the half that GENERATES the inversion
/// rather than completing it.
///
/// <para><c>PermissionEvaluator.GetEffectivePermissions</c> is an
/// <c>Observable.CombineLatest</c> fold whose sources are cached <c>Replay(1)</c> queries, so
/// on a warm cache it emits <b>synchronously during <c>Subscribe</c>, while holding the
/// CombineLatest gate</b> (one per ancestor scope, nested). A handler written as
/// <c>GetEffectivePermissions(...).Take(1).SelectMany(&lt;whole handler body&gt;)</c> therefore
/// runs its ENTIRE body inside that lock — storage writes, cache invalidation, change-feed
/// publishes and all. That shape is a latent inversion generator at every one of its call
/// sites, not just on the delete path where it was caught.</para>
///
/// <para>These tests pin the cure —
/// <see cref="HubPermissionExtensions.TakeDecisionOutsideGate(IObservable{Permission})"/> —
/// against a fold modelled on the real one: a source that emits synchronously during Subscribe
/// while holding a gate. That is the only property of <c>CombineLatest</c> that matters here,
/// and modelling it explicitly makes the gate observable to the test.</para>
/// </summary>
public class PermissionFoldGateTests
{
    /// <summary>
    /// The evaluator's emission shape: value already buffered upstream ⇒ <c>OnNext</c> fires
    /// on the SUBSCRIBING thread, inside the fold's gate, before <c>Subscribe</c> returns.
    /// </summary>
    private static IObservable<Permission> FoldEmittingInsideGate(object gate, Permission value)
        => Observable.Create<Permission>(observer =>
        {
            lock (gate)
            {
                observer.OnNext(value);
                observer.OnCompleted();
            }
            return Disposable.Empty;
        });

    /// <summary>
    /// Precondition — proves the tests below are not vacuous: the PRE-FIX shape
    /// (<c>.Take(1)</c> and nothing else) really does run the continuation inside the fold's
    /// gate, on the subscribing thread. If this ever stops being true the whole #899 analysis
    /// needs revisiting.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void Take1_alone_runs_the_continuation_INSIDE_the_folds_gate()
    {
        var foldGate = new object();
        var insideGate = false;
        var onSubscriberThread = false;
        var subscriberThread = Environment.CurrentManagedThreadId;

        FoldEmittingInsideGate(foldGate, Permission.All)
            .Take(1)
            .Subscribe(_ =>
            {
                insideGate = Monitor.IsEntered(foldGate);
                onSubscriberThread = Environment.CurrentManagedThreadId == subscriberThread;
            });

        insideGate.Should().BeTrue(
            "the fold emits during Subscribe while holding its CombineLatest gate, so a bare "
            + "Take(1) hands the whole handler body that lock");
        onSubscriberThread.Should().BeTrue();
    }

    /// <summary>
    /// The fix: the decision is still TAKEN inside the fold, but the continuation runs
    /// outside it — on another thread, holding none of the fold's locks.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TakeDecisionOutsideGate_runs_the_continuation_OUTSIDE_the_folds_gate()
    {
        var foldGate = new object();
        var subscriberThread = Environment.CurrentManagedThreadId;
        var observed = new TaskCompletionSource<(bool InsideGate, int Thread, Permission Value)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        FoldEmittingInsideGate(foldGate, Permission.All)
            .TakeDecisionOutsideGate()
            .Subscribe(p => observed.TrySetResult(
                (Monitor.IsEntered(foldGate), Environment.CurrentManagedThreadId, p)));

        var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        result.InsideGate.Should().BeFalse("the continuation must not hold the fold's gate");
        result.Thread.Should().NotBe(subscriberThread,
            "leaving the gate requires leaving the emitting thread");
        result.Value.Should().Be(Permission.All,
            "the decision itself is unchanged — only where the continuation runs moved");
    }

    /// <summary>
    /// The generator, deterministically. Two handlers each gated on their OWN permission fold;
    /// each body then walks a SHARED gate (the process-wide synced-query <c>Replay(1)</c> /
    /// <c>PersistenceService.Changes</c> <c>Merge</c>) and into the OTHER handler's fold —
    /// which is exactly what a change-feed publish from inside a handler body does, because
    /// both folds subscribe the same cached queries.
    ///
    /// <para>With a bare <c>Take(1)</c> both bodies run INSIDE their own fold gate, so they
    /// acquire {own fold gate, shared gate} in opposite orders and deadlock — this test hangs
    /// to its bound and fails. With <c>TakeDecisionOutsideGate</c> the bodies hold no fold
    /// gate, so no cycle can form no matter what they touch.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Two_handlers_gated_on_their_own_fold_cannot_deadlock()
    {
        var foldGate1 = new object();
        var foldGate2 = new object();
        var sharedQueryGate = new object();

        // Both bodies must be running at the same moment — otherwise the inversion cannot
        // form and the test would pass vacuously.
        using var bothInsideTheirBody = new Barrier(2);
        var bound = TimeSpan.FromSeconds(10);

        Task RunGatedHandler(object ownFoldGate, object otherFoldGate)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return Task.Run(() =>
            {
                FoldEmittingInsideGate(ownFoldGate, Permission.All)
                    .TakeDecisionOutsideGate()
                    .Subscribe(_ =>
                    {
                        bothInsideTheirBody.SignalAndWait(bound);
                        lock (sharedQueryGate)
                            lock (otherFoldGate) { }
                        completed.TrySetResult();
                    });
                return completed.Task;
            });
        }

        var bothFinished = Task.WhenAll(
            RunGatedHandler(foldGate1, foldGate2),
            RunGatedHandler(foldGate2, foldGate1));

        // Task.Delay is the DEADLOCK BOUND, not a wait-for-propagation sleep: the only way
        // both handlers fail to finish is a genuine cycle.
        var finished = await Task.WhenAny(bothFinished, Task.Delay(bound));

        finished.Should().BeSameAs(bothFinished,
            "a permission-gated handler must not run its body inside the evaluator's fold "
            + "gate — doing so makes every shared gate it touches half of a lock-order "
            + "inversion (#899)");
    }
}
