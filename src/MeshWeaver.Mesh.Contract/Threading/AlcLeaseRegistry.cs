using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// Per-ALC in-flight accounting: who is currently executing code out of a collectible
/// <see cref="AssemblyLoadContext"/>, and a reactive signal for when nobody is.
///
/// <para><b>Why this exists.</b> Unloading a collectible context while a thread can still enter its
/// code is the native use-after-unload SIGSEGV — the same class
/// <see cref="MeshTeardownSignal"/> guards at mesh teardown, but that signal is MESH-WIDE and fires
/// at silo stop. It says nothing about a single context being retired on a LIVE silo: a NodeType
/// recompile evicting its predecessor, or one grain deactivating on idle collection. Those are the
/// unloads that had no gate at all, and <c>MessageHubGrain</c> has carried a "🚨 KNOWN GAP (per-ALC
/// accounting)" comment saying exactly that.</para>
///
/// <para><b>Read the crash before changing this.</b> CI run 32713409169 (2026-08-24) caught it in a
/// core dump: crashing thread entered at <c>CPalThread::ThreadEntry → KickOffThread →
/// ManagedThreadBase::KickOff</c> — a dedicated thread, on its very FIRST managed call — which took
/// the prestub into <c>UnsafeJitFunction</c> and faulted in
/// <c>LCGMethodResolver::GetCodeInfo+0x1f7</c>, at <c>movl 0x4(%rax), %esi</c> where
/// <c>rax = 0x0074007300200022</c>: UTF-16 text (<c>" st</c>) sitting where a pointer belonged.
/// <c>si_code = SI_KERNEL</c> with <c>si_addr = 0</c> is the non-canonical-pointer (#GP) signature,
/// not a null dereference. The allocator backing that dynamic method was gone while a thread was
/// still arriving at it.</para>
///
/// <para><b>The rule this type enforces: an unload happens on a POSITIVE quiescence signal or it
/// does not happen at all.</b> Never on a timer expiry. A retained context costs memory until the
/// process exits; an unload with a live user costs the process. <see cref="UnloadWhenQuiesced"/>
/// therefore reports <c>false</c> and leaves the context loaded rather than unloading on timeout —
/// which is the inversion of what the grain used to do (wait 5 s, log "moving on", unload anyway).
/// </para>
///
/// <para>Instance state only — held by whoever owns the contexts, dying with them (NoStaticState).
/// </para>
/// </summary>
public sealed class AlcLeaseRegistry
{
    /// <summary>
    /// One context's lease count. The count is published under the same lock that mutates it, so a
    /// subscriber can never observe a 0 that a concurrent <see cref="Enter"/> has already
    /// superseded — the reordering a bare <c>Interlocked</c> + <c>OnNext</c> pair would allow, and
    /// the one that would hand out a false "safe to unload".
    /// </summary>
    private sealed class Entry
    {
        private readonly object gate = new();
        private int inFlight;

        public BehaviorSubject<int> Count { get; } = new(0);

        public void Enter()
        {
            lock (gate)
                Count.OnNext(++inFlight);
        }

        public void Leave()
        {
            lock (gate)
                Count.OnNext(--inFlight);
        }

        public int Current
        {
            get { lock (gate) return inFlight; }
        }
    }

    private readonly ConcurrentDictionary<AssemblyLoadContext, Entry> entries = new();

    /// <summary>
    /// Marks the caller as executing code from <paramref name="context"/> until the returned handle
    /// is disposed. Hold it across the WHOLE call — including any await — because the window that
    /// matters is "a thread can still enter this code", not "a call has been issued".
    /// </summary>
    public IDisposable Enter(AssemblyLoadContext context)
    {
        var entry = entries.GetOrAdd(context, _ => new Entry());
        entry.Enter();
        // Idempotent: a double-dispose must not decrement twice and fake a quiesce.
        var released = 0;
        return Disposable.Create(() =>
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                entry.Leave();
        });
    }

    /// <summary>Current lease count — 0 when nobody is inside the context. Diagnostics and tests;
    /// never gate an unload on reading this, gate on <see cref="Quiesced"/>.</summary>
    public int InFlight(AssemblyLoadContext context) =>
        entries.TryGetValue(context, out var entry) ? entry.Current : 0;

    /// <summary>
    /// Emits once the context has no leases, then completes. Already-quiesced (or never-leased)
    /// contexts emit immediately.
    ///
    /// <para>Observed on the task pool deliberately: the count is published while holding the
    /// entry's lock, and the whole point of subscribing here is to then UNLOAD — which runs
    /// arbitrary module and finalizer code. Doing that inline would run it under our lock.</para>
    /// </summary>
    public IObservable<Unit> Quiesced(AssemblyLoadContext context) =>
        (entries.TryGetValue(context, out var entry)
            ? entry.Count.Where(count => count == 0).Take(1).Select(_ => Unit.Default)
            : Observable.Return(Unit.Default))
        // On BOTH paths, including the never-leased one. A context nobody ever entered is the
        // common case (retiring a duplicate compilation), and without the hop there it would run
        // Unload — module and finalizer code — inline on whichever thread happened to subscribe.
        .ObserveOn(TaskPoolScheduler.Default);

    /// <summary>
    /// Unloads <paramref name="context"/> once it is quiesced, and NOT AT ALL if it fails to
    /// quiesce within <paramref name="budget"/>. Cold — the caller must subscribe.
    /// </summary>
    /// <returns><c>true</c> if the context was unloaded; <c>false</c> if it was left loaded
    /// because it never went quiet (or the unload itself threw). A <c>false</c> is a real finding:
    /// something outlived the thing that owns it, and it is worth chasing — but it is a memory
    /// leak, which is the outcome we are deliberately choosing over a crash.</returns>
    public IObservable<bool> UnloadWhenQuiesced(
        AssemblyLoadContext context,
        TimeSpan budget,
        ILogger? logger = null,
        string? what = null)
        => Quiesced(context)
            .Take(1)
            .Timeout(budget)
            .Select(quiesced =>
            {
                // Unload FIRST, drop the tracking only once it succeeded. Removing the entry up
                // front meant a throwing Unload (a context that turns out not to be collectible)
                // landed in the Catch below reporting InFlight = 0 — a diagnostic that says
                // "nothing was using it", about a context we just failed to reclaim and are now
                // no longer tracking.
                context.Unload();
                entries.TryRemove(context, out _);
                logger?.LogDebug("Unloaded collectible context {What}", what ?? context.Name);
                return true;
            })
            .Catch((Exception exception) =>
            {
                logger?.LogError(
                    exception,
                    "NOT unloading collectible context {What}: it still has {InFlight} in-flight "
                    + "lease(s) after {Budget}. Leaving it loaded — unloading a context somebody "
                    + "can still enter is the use-after-unload SIGSEGV.",
                    what ?? context.Name, InFlight(context), budget);
                return Observable.Return(false);
            });
}
