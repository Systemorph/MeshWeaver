using System;
using System.Reactive;
using System.Reactive.Linq;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The wait a retention test needs after disposing a mesh: the object becomes COLLECTABLE.
///
/// <para>🚨 <see cref="Messaging.IMessageHub.DisposalCompleted"/> is not that signal. The hub signals
/// it from INSIDE its own ShutDown turn, so at the moment a waiter wakes, the signalling thread is
/// still on a stack that holds the hub — the rest of that turn (its <c>finally</c>, the pump that
/// ran it) and anything else still in flight. On an idle machine that stack unwinds long before the
/// waiter collects; on a loaded one the signalling thread can be pre-empted first, and an immediate
/// <c>GC.Collect</c> then finds the hub alive for a reason that is not the defect under test
/// (<see cref="TheDisposalSignalFiresBeforeTheDisposingTurnLetsGoTest"/> pins that deterministically).</para>
///
/// <para>So a retention test asserts what it means: the object is released within a bound — far
/// below the retention it guards against (an operation budget of minutes) — by collecting until the
/// weak reference clears. The GC verdict has no push signal, so this is the one place a re-check on
/// an interval is the instrument rather than a substitute for one.</para>
/// </summary>
internal static class Collected
{
    /// <summary>Emits once, as soon as a full blocking collection leaves <paramref name="weak"/> dead.</summary>
    /// <param name="weak">The reference to the object that must be released.</param>
    public static IObservable<Unit> Of(WeakReference weak) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ =>
            {
                FullCollection();
                return weak.IsAlive;
            })
            .Where(alive => !alive)
            .Select(_ => Unit.Default)
            .Take(1);

    /// <summary>One full, blocking collection with finalizers run in between.</summary>
    public static void FullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
    }
}
