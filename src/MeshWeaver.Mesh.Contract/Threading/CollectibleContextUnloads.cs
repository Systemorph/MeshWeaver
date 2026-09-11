using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The mesh's record of collectible load contexts it has RETIRED (asked to unload) whose unload has
/// not yet REALLY finished — and the reactive signal for when every one of them has.
///
/// <para><b>Why "really finished" is a separate signal (Plugins#1605).</b>
/// <c>AssemblyLoadContext.Unload()</c> only <i>starts</i> an unload: the runtime keeps the context
/// alive through a strong handle, and frees it over the following collections, on the finalizer
/// thread, once nothing references its types. <see cref="MeshTeardownSignal"/> fires when teardown
/// has <i>requested</i> those unloads, so a mesh that starts on it starts while the previous mesh's
/// contexts are still being torn down. Every readable FutuRe crash dump (#12–#16) caught exactly
/// that state — 3 to 7 contexts with <c>_state = Unloading</c> while the next instance was being
/// built or run. <see cref="AllCollected"/> is the point after that: it completes only when every
/// context retired so far has been collected.</para>
///
/// <para><b>How "collected" is observed.</b> A retired context holds a finalizable sentinel that
/// nothing else references; the runtime releases the context's strong handle when it destroys the
/// LoaderAllocator (unloadability phase two), so the sentinel is finalized only after that. Its
/// finalizer calls <see cref="Retirement.Collected"/>, which stops the entry counting as pending
/// synchronously and completes the signal on the thread pool — never on the finalizer thread, which
/// destroys every other LoaderAllocator in the process and must not run subscribers.</para>
///
/// <para><b>It always terminates for a context that can terminate.</b> An unload that is abandoned
/// — the drain signal faulted and the context was deliberately KEPT, or <c>Unload()</c> threw —
/// ERRORS the signal (<see cref="Retirement.Faulted"/>), and the entry stays so a waiter that
/// subscribes after the fault still receives it. A context that is merely still referenced never
/// "arrives": that is a retention, and a caller that drives collections detects it as a full
/// collection that frees nothing (<see cref="Pending"/> does not shrink), not by waiting.</para>
///
/// <para>Mesh-scoped instance state, never static (NoStaticState). It holds each context's signal
/// and name, never the context — a map of contexts would root the very generations it waits for.
/// </para>
/// </summary>
public sealed class CollectibleContextUnloads
{
    private readonly ConcurrentDictionary<long, Retirement> pending = new();
    private long sequence;

    /// <summary>
    /// Records that the context named <paramref name="contextName"/> has been asked to unload, and
    /// returns the handle its sentinel reports through.
    /// </summary>
    public Retirement Retire(string contextName)
    {
        var retirement = new Retirement(this, Interlocked.Increment(ref sequence), contextName);
        pending[retirement.Id] = retirement;
        return retirement;
    }

    /// <summary>
    /// Retired contexts not yet collected, including those whose unload faulted. Exact the moment
    /// finalizers have run — a collected context stops counting inside its sentinel's finalizer,
    /// before its signal is delivered — so a caller driving collections can measure progress.
    /// </summary>
    public int Pending => pending.Values.Count(r => !r.IsCollected);

    /// <summary>Names of the retired contexts not yet collected — for a retention report.</summary>
    public IReadOnlyList<string> PendingContextNames =>
        pending.Values.Where(r => !r.IsCollected).Select(r => r.ContextName).ToArray();

    /// <summary>Faults of retired contexts whose unload was abandoned; empty when none was.</summary>
    public IReadOnlyList<Exception> Faults =>
        pending.Values.Select(r => r.Fault).OfType<Exception>().ToArray();

    /// <summary>
    /// Emits once and completes when every context retired BEFORE the subscription has been
    /// collected; errors if any of them faulted. Emits synchronously on subscribe when nothing is
    /// outstanding, so a start with nothing to wait for is not delayed.
    ///
    /// <para>The contract is ordering against the COLLECTION, not between subscribers: no subscriber
    /// is released while a context it waits for is alive. Two independent subscribers may be
    /// released in either order — an <see cref="AsyncSubject{T}"/> replays at once to a subscriber
    /// that arrives while it is still notifying the earlier ones — so a caller that must start
    /// "after the unload" sequences on its OWN subscription, never on someone else's.</para>
    /// </summary>
    public IObservable<Unit> AllCollected => Observable.Defer(() =>
    {
        var snapshot = pending.Values.Select(r => r.Signal).ToArray();
        return snapshot.Length == 0
            ? Observable.Return(Unit.Default)
            : snapshot.Merge().IgnoreElements().Concat(Observable.Return(Unit.Default));
    });

    /// <summary>One retired context's completion.</summary>
    public sealed class Retirement
    {
        private readonly CollectibleContextUnloads owner;
        private readonly AsyncSubject<Unit> signal = new();
        private Exception? fault;
        // ONE terminal transition: 0 pending → Collected (1) or Faulted (2), whichever comes first.
        // A kept (faulted) context can still be collected later if everyone drops it; that must not
        // turn the abandoned unload into a success or erase the fault (Copilot review, #4042).
        private int terminal;
        private const int Pending0 = 0, CollectedState = 1, FaultedState = 2;

        internal Retirement(CollectibleContextUnloads owner, long id, string contextName)
        {
            this.owner = owner;
            Id = id;
            ContextName = contextName;
        }

        internal long Id { get; }

        /// <summary>The retired context's name.</summary>
        public string ContextName { get; }

        internal IObservable<Unit> Signal => signal.AsObservable();

        internal Exception? Fault => Volatile.Read(ref terminal) == FaultedState ? Volatile.Read(ref fault) : null;

        internal bool IsCollected => Volatile.Read(ref terminal) == CollectedState;

        /// <summary>
        /// The context has really been collected. Called from the sentinel's FINALIZER: it stops
        /// counting as <see cref="Pending"/> synchronously (so a collection driver sees progress the
        /// moment finalizers have run); its subscribers are released on the thread pool, never on
        /// the finalizer thread; and only then does the entry leave the map.
        /// </summary>
        public void Collected()
        {
            if (Interlocked.CompareExchange(ref terminal, CollectedState, Pending0) != Pending0)
                return;
            ThreadPool.UnsafeQueueUserWorkItem(static r =>
            {
                r.signal.OnNext(Unit.Default);
                r.signal.OnCompleted();
                r.owner.pending.TryRemove(r.Id, out _);
            }, this, preferLocal: false);
        }

        /// <summary>
        /// The unload was abandoned and the context is being KEPT. Errors the signal; the entry
        /// stays, so a waiter subscribing afterwards is released with the same fault. The FIRST
        /// fault wins and is the only one signalled — two hops racing on the pool would otherwise
        /// decide which exception a waiter sees.
        /// </summary>
        public void Faulted(Exception exception)
        {
            // The first fault is the one recorded; the terminal transition then decides whether it
            // counts — a context already collected stays collected (the fault is then moot).
            if (Interlocked.CompareExchange(ref fault, exception, null) is not null)
                return;
            if (Interlocked.CompareExchange(ref terminal, FaultedState, Pending0) != Pending0)
                return;
            ThreadPool.UnsafeQueueUserWorkItem(static s => s.subject.OnError(s.exception),
                (subject: signal, exception), preferLocal: false);
        }
    }
}
