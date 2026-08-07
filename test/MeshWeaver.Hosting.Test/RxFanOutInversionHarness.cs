using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The reusable probe for the issue-#899 bug CLASS — an Rx <b>lock-order inversion</b> between
/// two permission-gated handlers.
///
/// <para>The cycle needs TWO ingredients at once:</para>
/// <list type="number">
///   <item><b>A fan-out point that delivers synchronously on the publisher's thread.</b> That is
///   a deliberate, load-bearing contract here — <c>PathResolutionService</c>'s resolution cache,
///   <c>MeshNodeStreamCache</c>'s failure-state reset and <c>Workspace</c>'s remote-stream
///   eviction are all invalidated ONLY by the change feed and each documents that the
///   invalidation lands before the writing call returns (read-your-own-writes). This half is
///   NOT the bug and must not be "fixed".</item>
///   <item><b>A publisher that holds an Rx gate while it publishes.</b>
///   <c>PermissionEvaluator.GetEffectivePermissions</c> emits synchronously during
///   <c>Subscribe</c> from inside its <c>CombineLatest</c>/<c>Concat</c> gate on a warm cache, so
///   a handler written as <c>…Take(1).SelectMany(&lt;whole body&gt;)</c> runs its ENTIRE body —
///   storage write, cache invalidation, change-feed publish — while holding that lock. THIS half
///   is the bug, and <c>HubPermissionExtensions.TakeDecisionOutsideGate</c> removes it.</item>
/// </list>
///
/// <para>The harness reconstructs the real graph exactly: each handler is reached through a fold
/// that emits while holding its OWN gate; a <see cref="Barrier"/> guarantees both bodies are
/// running at the same moment (without it the inversion cannot form and the probe would pass
/// vacuously); and the subscriber chain walks a SHARED gate (<c>PersistenceService.Changes</c>'s
/// <c>Merge</c>, the process-wide <c>Replay(1)</c> in <c>IMeshNodeStreamCache</c>) and from there
/// into the OTHER handler's fold — because both folds subscribe the same cached queries.</para>
///
/// <para>Point it at any fan-out point by supplying how to subscribe and how to publish; supply
/// the composition under test as <c>gatedDecision</c> (a bare <c>Take(1)</c> reproduces the wedge,
/// <c>TakeDecisionOutsideGate()</c> makes it impossible).</para>
/// </summary>
internal static class RxFanOutInversionHarness
{
    /// <summary>Bound on the whole probe. A genuine cycle is the only way to exceed it.</summary>
    internal static readonly TimeSpan DeadlockBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A fold modelled on <c>PermissionEvaluator.GetEffectivePermissions</c>'s emission shape:
    /// the value is already buffered upstream (warm <c>Replay(1)</c> sources), so <c>OnNext</c>
    /// fires on the SUBSCRIBING thread, inside the fold's gate, before <c>Subscribe</c> returns.
    /// That single property is the whole reason the inversion exists; modelling it explicitly
    /// makes the gate observable to the test.
    /// </summary>
    internal static IObservable<Permission> FoldEmittingInsideGate(object gate, Permission value)
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
    /// Runs two concurrent permission-gated handler bodies against <paramref name="publish"/> and
    /// reports whether BOTH finished within <see cref="DeadlockBound"/>.
    /// </summary>
    /// <param name="gatedDecision">
    /// Given a handler's own fold gate, returns the decision stream the handler subscribes —
    /// i.e. <c>FoldEmittingInsideGate(gate, …)</c> composed with the operator under test.
    /// </param>
    /// <param name="subscribe">
    /// Attaches a handler to the fan-out point under test. The handler receives the tag its
    /// publisher passed, so it can walk into the OTHER publisher's fold gate.
    /// </param>
    /// <param name="publish">Publishes one event carrying the given tag, synchronously.</param>
    internal static async Task<bool> BothGatedHandlersComplete(
        Func<object, IObservable<Permission>> gatedDecision,
        Func<Action<string>, IDisposable> subscribe,
        Action<string> publish)
    {
        const string tag1 = "p1";
        const string tag2 = "p2";

        var foldGateOfHandler1 = new object();   // handler 1's CombineLatest fold gate
        var foldGateOfHandler2 = new object();   // handler 2's CombineLatest fold gate
        var sharedQueryGate = new object();      // the shared synced-query / Merge gate

        using var subscription = subscribe(tag =>
        {
            var otherFoldGate = tag == tag1 ? foldGateOfHandler2 : foldGateOfHandler1;
            lock (sharedQueryGate)
                lock (otherFoldGate) { }
        });

        using var bothInsideTheirBody = new Barrier(2);

        Task RunGatedHandler(object ownFoldGate, string tag)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => gatedDecision(ownFoldGate).Subscribe(
                _ =>
                {
                    try
                    {
                        // Both bodies must be live at the same instant, or no cycle can form.
                        bothInsideTheirBody.SignalAndWait(DeadlockBound);
                        publish(tag);
                        completed.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        completed.TrySetException(ex);
                    }
                },
                ex => completed.TrySetException(ex)));
            return completed.Task;
        }

        var bothFinished = Task.WhenAll(
            RunGatedHandler(foldGateOfHandler1, tag1),
            RunGatedHandler(foldGateOfHandler2, tag2));

        // Task.Delay is the DEADLOCK BOUND here, not a wait-for-propagation sleep: the only way
        // both handlers fail to finish is a genuine cycle.
        var finished = await Task.WhenAny(bothFinished, Task.Delay(DeadlockBound));
        return ReferenceEquals(finished, bothFinished) && bothFinished.IsCompletedSuccessfully;
    }
}
