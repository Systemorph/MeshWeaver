using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Which NodeTypes have a PREBUILT ADOPTION in flight right now — the interlock that stops the
/// first-build kickoff from racing (and throwing away) an adoption that is already under way.
///
/// <para><b>The race this closes, measured 2026-08-22.</b> Adopting a prebuilt assembly requires
/// writing the NodeType's node, and that write goes through the type's OWN hub — so
/// <see cref="PrebuiltAssemblySeeder.Seed(MeshWeaver.Messaging.IMessageHub, string, byte[], byte[], string, Microsoft.Extensions.Logging.ILogger, System.Collections.Generic.IReadOnlyDictionary{string, string}, string)"/> ACTIVATES the hub it is about to stamp. Activation is
/// exactly what arms the first-build kickoff (<c>CompilationStatus is null</c> + no usable build ⇒
/// flip Pending), so the seeder's own probe started the very Roslyn compile the adoption exists to
/// avoid:</para>
/// <code>
/// 54.709  MeshNodeStreamCache: opening shared stream for Widget/Thing   ← the seeder
/// 54.728  First-build kickoff: … no usable build — flipping CompilationStatus=Pending
/// 54.7xx  Prebuilt assembly ADOPTED for Widget/Thing … no compile needed
/// 54.7xx  [ReleaseRequestWatcher] … satisfied by the existing current build — no compile dispatched
/// 54.8xx  Compiling assembly for Widget_Thing (disk, 0 NuGet refs)      ← the kickoff's compile,
///                                                                        overwriting the adoption
/// </code>
/// <para>Every visible signal said the adoption worked — it did — and the type was recompiled and
/// re-stamped anyway, milliseconds later, by a compile that had been dispatched before anyone could
/// answer "is there already a build for this?". The release request was correctly SATISFIED; the
/// kickoff simply never asked. So install-time consumption (#1707 slice 3) saved nothing, and a
/// gate that consumes a bake (#1763) ended up judging its own bytes rather than the ones that
/// ship.</para>
///
/// <para><b>Why a reservation and not a re-check.</b> Re-checking the node before running Roslyn
/// narrows the window but cannot close it — the adoption may still land during the check. The
/// reservation is taken BEFORE the seeder touches the node stream, i.e. before the activation that
/// arms the kickoff, so in the seed-activates-the-hub path (the only one where the two can collide)
/// the ordering is a fact rather than a hope.</para>
///
/// <para><b>It DELAYS a kickoff; it never cancels one.</b> The kickoff waits for the reservation to
/// clear and then re-evaluates, so a DECLINED adoption still compiles — no skip-trapdoor. The wait
/// is bounded (<see cref="ReservationWaitBudget"/>): a leaked reservation costs a delay, never an
/// unbuilt type.</para>
///
/// <para>Mesh-scoped singleton (registered in <c>AddGraph</c>), instance maps only — NO static
/// state, exactly like <see cref="NodeTypeCompileParkRegistry"/> beside it.</para>
/// </summary>
public sealed class NodeTypeAdoptionRegistry
{
    /// <summary>
    /// How long a first-build kickoff waits for an in-flight adoption before proceeding anyway.
    /// Generous against a real seed (a bundle read plus one store upload) and short against the
    /// alternative: a reservation that leaked would otherwise strand the type forever, which is a
    /// strictly worse failure than one redundant compile.
    /// </summary>
    public static readonly TimeSpan ReservationWaitBudget = TimeSpan.FromSeconds(30);

    // Reference-counted: two bundles can legitimately carry the same node path, and one of them
    // finishing must not tell the kickoff that the other is done.
    private readonly ConcurrentDictionary<string, int> reserved =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Subject<string> released = new();

    /// <summary>
    /// Marks <paramref name="nodeTypePath"/> as having an adoption in flight until the returned
    /// handle is disposed. Take it BEFORE opening the type's node stream — after the activation the
    /// kickoff has already been armed and there is nothing left to interlock.
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's mesh path.</param>
    public IDisposable Reserve(string nodeTypePath)
    {
        reserved.AddOrUpdate(nodeTypePath, 1, static (_, count) => count + 1);
        return new Reservation(this, nodeTypePath);
    }

    /// <summary>Whether an adoption is in flight for this path right now.</summary>
    /// <param name="nodeTypePath">The NodeType's mesh path.</param>
    public bool IsReserved(string nodeTypePath) =>
        reserved.TryGetValue(nodeTypePath, out var count) && count > 0;

    /// <summary>
    /// Emits once no adoption is in flight for <paramref name="nodeTypePath"/> — immediately when
    /// none is. Cold.
    ///
    /// <para>Race-free in both directions: the subject is subscribed BEFORE the reservation is
    /// re-checked, so a release landing between the two is not lost.</para>
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's mesh path.</param>
    public IObservable<Unit> WhenClear(string nodeTypePath) =>
        Observable.Defer(() => Observable
            // 🚨 SUBSCRIBE FIRST, THEN RE-CHECK — never the other way round. Testing IsReserved and
            // only then subscribing loses a release that lands in between, and the caller pays the
            // FULL wait budget for a reservation that is already gone: a 30-second stall per type,
            // which in a tree of hundreds is the whole job. Merge subscribes its sources in order,
            // so the deferred re-check runs after the subject subscription is live and one of the
            // two legs is guaranteed to answer.
            .Merge(
                released
                    .Where(path => string.Equals(path, nodeTypePath, StringComparison.OrdinalIgnoreCase))
                    .Where(_ => !IsReserved(nodeTypePath))
                    .Select(_ => Unit.Default),
                Observable.Defer(() => IsReserved(nodeTypePath)
                    ? Observable.Empty<Unit>()
                    : Observable.Return(Unit.Default)))
            .Take(1)
            // The release fires on the seeding pipeline's thread and the kickoff's continuation
            // writes to a hub — running that inline on the releasing thread is the "work inside a
            // Subscribe callback on the emission thread" shape the compile watcher is explicitly
            // built to avoid.
            .ObserveOn(TaskPoolScheduler.Default));

    private void Release(string nodeTypePath)
    {
        // Remove at zero rather than leaving a 0 entry behind: IsReserved reads the count, and a
        // path that is never adopted again would otherwise sit in the map for the process's life.
        reserved.AddOrUpdate(nodeTypePath, 0, static (_, count) => count - 1);
        if (reserved.TryGetValue(nodeTypePath, out var remaining) && remaining <= 0)
            reserved.TryRemove(new KeyValuePair<string, int>(nodeTypePath, remaining));
        released.OnNext(nodeTypePath);
    }

    private sealed class Reservation(NodeTypeAdoptionRegistry owner, string nodeTypePath) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.Release(nodeTypePath);
        }
    }
}
