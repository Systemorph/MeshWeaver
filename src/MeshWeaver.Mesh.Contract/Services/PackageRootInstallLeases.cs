using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 <b>Which package roots an install is writing under RIGHT NOW — so nothing recycles a root
/// out from under the writer that owns it (Systemorph/MeshWeaver#3510).</b>
///
/// <para><b>The defect.</b> A package root's hub was torn down while that package's own install was
/// in flight. The root's per-node children went down with it, the writes those children owed acks
/// for were stranded (<c>[UpdateQueue] ADVANCE_WITHOUT_HANDOFF … the owner never acknowledged this
/// write</c>), the <c>nodeops</c> handler that owed its reply to one of those acks never replied,
/// and the install sat until the gate's own ten-minute bound reported
/// <c>[FAIL] Hosting — install: TimeoutException</c> against a package that had finished writing
/// its 145 files eight minutes earlier. Six occurrences, four lost bake seals.</para>
///
/// <para><b>The rule this registry states.</b> <i>The install is the writer that should own the
/// root's lifetime.</i> While an install holds a root, a recycle aimed at that root <b>waits</b> —
/// it is never dropped, never retried on a timer, and no bound anywhere is widened. The wait ends
/// on the lease's release, which is the state this type exists to make total.</para>
///
/// <para>🚨 <b>Why the release always arrives.</b> A lease is taken with
/// <see cref="HoldDuring{T}"/>, i.e. through <c>Observable.Using</c>, so the handle is disposed on
/// <b>OnCompleted</b>, on <b>OnError</b>, and on <b>unsubscribe</b> — the complete set of ways an
/// Rx subscription can end. An install that faults releases; an install whose caller gives up
/// releases; a mesh that goes down takes this instance with it. There is deliberately no timer that
/// force-releases a lease: "defer" here means <i>wait for a state that always arrives</i>, not
/// <i>wait a while</i>, and a stopgap that released a lease on a clock would hand the recycle back
/// exactly the race it was built to remove.</para>
///
/// <para>🚨 <b>And a held lease is never silent.</b> A recycle that defers logs once when it defers
/// and once when it proceeds, naming the holder — so a lease that somehow outlived its install is
/// visible in the log rather than showing up as a recycle that mysteriously never happened. A
/// control nobody can read is not a control.</para>
///
/// <para><b>Scope: the ROOT PATH, exactly — never the subtree.</b> The key is the whole path a
/// holder names, and a recycle defers only when its target IS that path. This is not a
/// conservatism: an install WAITS on work under its own root (a NodeType's rebuild is what
/// <c>PackageInstaller.MayPublishIntoRoot</c> holds for), and the recyclers that serve those
/// rebuilds — <c>NodeTypeEnrichmentHelpers</c>' stale-build convergence and overlay self-heal —
/// live on the per-type hubs BENEATH the root. Deferring those against the install that is waiting
/// for them is a deadlock, so they are deliberately out of scope. The root's own hub is the one
/// nothing beneath the install depends on recycling mid-flight.</para>
///
/// <para>Mesh-scoped singleton (registered in <c>MeshBuilder</c>), instance maps only — <b>no
/// static state</b>, so its lifetime IS the mesh's and nothing bleeds across tests or partitions.
/// Modelled on <c>NodeTypeAdoptionRegistry</c>, whose reservation/<c>WhenClear</c> pair solves the
/// same shape of race one layer down.</para>
/// </summary>
public sealed class PackageRootInstallLeases
{
    /// <summary>
    /// Root path → the descriptions of the holders currently on it, newest last. The LIST is the
    /// reference count (two installs can legitimately target one partition through
    /// <c>targetPartition</c>, and one finishing must not tell a recycle the other is done) and it
    /// is also what the deferral log prints, so "held" and "held by whom" can never disagree.
    /// </summary>
    private readonly ConcurrentDictionary<string, ImmutableList<string>> held =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ISubject<string> released = Subject.Synchronize(new Subject<string>());

    /// <summary>
    /// Marks <paramref name="rootPath"/> as being written by an install until the returned handle
    /// is disposed. Prefer <see cref="HoldDuring{T}"/>, which ties the handle to a subscription's
    /// lifetime so the release cannot be forgotten.
    /// </summary>
    /// <param name="rootPath">The package root being installed, e.g. <c>Hosting</c>.</param>
    /// <param name="holder">One short phrase naming who holds it and why — printed verbatim by the
    /// recycle that defers. Never blank: an unnamed holder reads to the next person as no holder at
    /// all, the <c>ReasonNotStated</c> rule this repository already applies to
    /// <c>DisposeRequest</c>.</param>
    public IDisposable Hold(string rootPath, string holder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        held.AddOrUpdate(
            rootPath,
            _ => ImmutableList.Create(holder),
            (_, holders) => holders.Add(holder));
        return new Lease(this, rootPath, holder);
    }

    /// <summary>
    /// Runs <paramref name="work"/> with <paramref name="rootPath"/> held for exactly the lifetime
    /// of the subscription — released on completion, on fault, and on unsubscribe. A null or blank
    /// root is a no-op pass-through (an install with no root of its own holds nothing).
    /// </summary>
    /// <param name="rootPath">The package root being installed, or null when there is none.</param>
    /// <param name="holder">One short phrase naming who holds it and why.</param>
    /// <param name="work">The install, as a cold observable.</param>
    public IObservable<T> HoldDuring<T>(string? rootPath, string holder, IObservable<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return string.IsNullOrWhiteSpace(rootPath)
            ? work
            : Observable.Using(() => Hold(rootPath!, holder), _ => work);
    }

    /// <summary>Whether an install is writing under this root right now.</summary>
    /// <param name="rootPath">The root path to test.</param>
    public bool IsHeld(string? rootPath) =>
        !string.IsNullOrWhiteSpace(rootPath)
        && held.TryGetValue(rootPath!, out var holders)
        && !holders.IsEmpty;

    /// <summary>
    /// Who holds <paramref name="rootPath"/> right now, as one printable phrase — or
    /// <c>null</c> when nobody does.
    /// </summary>
    /// <param name="rootPath">The root path to describe.</param>
    public string? HeldBy(string? rootPath) =>
        !string.IsNullOrWhiteSpace(rootPath)
        && held.TryGetValue(rootPath!, out var holders)
        && !holders.IsEmpty
            ? string.Join("; ", holders)
            : null;

    /// <summary>
    /// Emits exactly once, as soon as no install holds <paramref name="rootPath"/> — immediately
    /// when none does. Cold, and it completes.
    ///
    /// <para>🚨 <b>Subscribe first, re-check second</b> — never the other way round. Testing
    /// <see cref="IsHeld"/> and only then subscribing loses a release that lands in between, and
    /// the caller then waits for a lease that is already gone. <c>Merge</c> subscribes its sources
    /// in order, so the deferred re-check runs with the subject subscription already live and one
    /// of the two legs is guaranteed to answer. Same construction, same reason, as
    /// <c>NodeTypeAdoptionRegistry.WhenClear</c>.</para>
    /// </summary>
    /// <param name="rootPath">The root path to wait on.</param>
    public IObservable<Unit> WhenReleased(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return Observable.Return(Unit.Default);
        return Observable.Defer(() => Observable
            .Merge(
                released
                    .Where(path => string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase))
                    .Where(_ => !IsHeld(rootPath))
                    .Select(_ => Unit.Default),
                Observable.Defer(() => IsHeld(rootPath)
                    ? Observable.Empty<Unit>()
                    : Observable.Return(Unit.Default)))
            .Take(1)
            // The release fires on the INSTALL's own thread, and what the waiter does next is post
            // a DisposeRequest and then read the address back. Running that inline on the
            // releasing thread would put a teardown inside the install's terminal — the "work in a
            // Subscribe callback on the emission thread" shape this codebase removes everywhere
            // else. Hopping costs one scheduled continuation per deferred recycle.
            .ObserveOn(TaskPoolScheduler.Default));
    }

    private void Release(string rootPath, string holder)
    {
        // Remove the key at zero rather than leaving an empty list behind: IsHeld reads the map,
        // and a root that is never installed again would otherwise sit in it for the mesh's life.
        var remaining = held.AddOrUpdate(
            rootPath,
            _ => ImmutableList<string>.Empty,
            (_, holders) => holders.Remove(holder));
        if (remaining.IsEmpty)
            held.TryRemove(new KeyValuePair<string, ImmutableList<string>>(rootPath, remaining));
        released.OnNext(rootPath);
    }

    private sealed class Lease(PackageRootInstallLeases owner, string rootPath, string holder)
        : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.Release(rootPath, holder);
        }
    }
}
