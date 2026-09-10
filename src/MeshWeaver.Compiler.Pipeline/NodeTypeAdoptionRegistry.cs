using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// What THIS process stamped on a NodeType when it adopted prebuilt bytes for it — the record the
/// adoption WROTE, plus the node version it was written over.
/// </summary>
/// <param name="NodeTypePath">The NodeType's mesh path.</param>
/// <param name="ObservedVersion">The <see cref="MeshNode.Version"/> the adoption READ before its
/// own write. The write bumps the node, so any snapshot at or below this version provably PREDATES
/// the adoption, and any snapshot above it provably contains it (or something later still).</param>
/// <param name="Definition">The definition the adoption stamped: live framework, the producer's
/// validated dependency record, and the <c>LastCompiledVersion</c> the bytes were uploaded under.</param>
/// <param name="At">When the stamp was written, for the log line that reports it.</param>
public sealed record AdoptedStamp(
    string NodeTypePath,
    long ObservedVersion,
    NodeTypeDefinition Definition,
    DateTimeOffset At);

/// <summary>
/// The result of laying this process's own adoption stamps over an enumeration snapshot —
/// see <see cref="NodeTypeAdoptionRegistry.OverlayOnto"/>.
/// </summary>
/// <param name="Definitions">The definitions to classify from.</param>
/// <param name="Applied">The paths whose definition came from this process's own write because the
/// snapshot provably predated it. 🚨 This is the count that makes an adoption report and a bake
/// report commensurable — see <c>Doc/Architecture/AdoptionAndTheSweepCountDifferentThings</c>.</param>
public sealed record AdoptionOverlay(
    ImmutableDictionary<string, NodeTypeDefinition?> Definitions,
    ImmutableList<string> Applied);

/// <summary>
/// What this process's PREBUILT ADOPTIONS are doing, in both tenses: which NodeTypes have one in
/// flight RIGHT NOW (the interlock that stops the first-build kickoff from racing an adoption that
/// is already under way), and which ones this process has already STAMPED (the ledger that stops
/// the bake sweep from judging an adopted type off a projection that predates the stamp — #3703,
/// <see cref="RecordAdopted"/>).
///
/// <para>Both halves close the same shape of race: a reader is about to decide something about a
/// NodeType from information that is older than an adoption this process has made. The reservation
/// closes it for the KICKOFF, which reads BEFORE the write; the ledger closes it for the SWEEP,
/// which reads after the write but from a source that has not caught up.</para>
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

    // ---- The adoption LEDGER: what this process actually stamped -----------------------------

    // Instance map on a mesh-scoped singleton, exactly like `reserved` above — never static, so its
    // lifetime IS the mesh's and nothing bleeds across tests or partitions.
    private readonly ConcurrentDictionary<string, AdoptedStamp> stamped =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Record that this process stamped <paramref name="definition"/> on
    /// <paramref name="nodeTypePath"/>, over the node as it stood at
    /// <paramref name="observedVersion"/>.
    ///
    /// <para>🚨 <b>Why the process has to remember this at all.</b> The bake sweep decides what to
    /// compile from ONE mesh-wide enumeration (<c>Query&lt;MeshNode&gt;(…).Take(1)</c>), which is a
    /// PROJECTION — eventually consistent, and therefore able to answer with records that predate
    /// writes this same process has already made. On memex's 2026-09-08 00:31 cold boot the seeding
    /// pass adopted 78 assemblies and the sweep ten seconds later classified 201 types
    /// <c>FrameworkStale</c> from a snapshot that still carried the PREVIOUS framework's records
    /// (#3703): 197 compiles instead of ~20, and the reachability that let a short source-discovery
    /// pass turn four adopted <c>Doc/**</c> types into false regressions (#3663).</para>
    ///
    /// <para>Nothing in the classification can see that, because a stale projection and a genuinely
    /// stale record are the SAME bytes. The process's own write is the one fact that distinguishes
    /// them, and it was being thrown away — <c>SeedAll</c> emitted a count and nothing else.</para>
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's mesh path.</param>
    /// <param name="observedVersion">The node version the adoption read BEFORE its own write.</param>
    /// <param name="definition">The definition the adoption stamped.</param>
    public void RecordAdopted(string nodeTypePath, long observedVersion, NodeTypeDefinition definition)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeTypePath);
        ArgumentNullException.ThrowIfNull(definition);
        var stamp = new AdoptedStamp(
            nodeTypePath, observedVersion, definition, DateTimeOffset.UtcNow);
        // Last writer wins on purpose: two bundles can carry the same node path, and the LATER
        // adoption is the one whose bytes the store key now names.
        stamped.AddOrUpdate(nodeTypePath, stamp, (_, _) => stamp);
    }

    /// <summary>Every stamp this process has written, as a snapshot.</summary>
    public ImmutableDictionary<string, AdoptedStamp> AdoptedStamps =>
        stamped.ToImmutableDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The stamp this process wrote for <paramref name="nodeTypePath"/> when a snapshot taken at
    /// <paramref name="snapshotVersion"/> provably PREDATES it — otherwise null.
    ///
    /// <para>🚨 The ordering is a FACT, not a heuristic, and it is what keeps this from becoming a
    /// stale serve. The adoption stamps the version it read and its own write BUMPS the node, so
    /// <c>snapshotVersion &lt;= ObservedVersion</c> means the snapshot cannot contain that write,
    /// while <c>snapshotVersion &gt; ObservedVersion</c> means it contains that write or something
    /// LATER — an owner refusal (#2813), a recompile, a source change. In the second case the
    /// snapshot is the newer evidence and it wins, so nothing here can outrank a record that has
    /// moved on.</para>
    /// </summary>
    /// <param name="nodeTypePath">The NodeType's mesh path.</param>
    /// <param name="snapshotVersion">The <see cref="MeshNode.Version"/> the enumeration observed.</param>
    public AdoptedStamp? StampSuperseding(string nodeTypePath, long snapshotVersion) =>
        stamped.TryGetValue(nodeTypePath, out var stamp) && snapshotVersion <= stamp.ObservedVersion
            ? stamp
            : null;

    /// <summary>
    /// Lay this process's own adoption stamps over an enumeration snapshot, so the bake sweep
    /// classifies each type from the NEWER of the two — the projection, or the write this process
    /// made and knows the projection has not seen.
    ///
    /// <para>Pure over its inputs plus the ledger: no hub, no mesh, no I/O, for the same reason
    /// <see cref="NodeTypeBakeStatus.Classify"/> is — every case is unit-testable without a
    /// fixture.</para>
    ///
    /// <para>A path absent from <paramref name="nodes"/> keeps its snapshot definition: with no
    /// version to compare, the overlay cannot establish that the snapshot is older, and an overlay
    /// that applied on faith would be exactly the unconditional trust this ordering rule exists to
    /// refuse.</para>
    /// </summary>
    /// <param name="definitions">The enumeration snapshot's definitions, keyed by path.</param>
    /// <param name="nodes">The same snapshot's nodes — the source of each observed version.</param>
    public AdoptionOverlay OverlayOnto(
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IReadOnlyDictionary<string, MeshNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(nodes);

        var applied = ImmutableList.CreateBuilder<string>();
        var result = ImmutableDictionary.CreateBuilder<string, NodeTypeDefinition?>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (path, definition) in definitions)
        {
            if (nodes.TryGetValue(path, out var node)
                && StampSuperseding(path, node.Version) is { } stamp)
            {
                result[path] = stamp.Definition;
                applied.Add(path);
            }
            else
            {
                result[path] = definition;
            }
        }

        // Ordered, so the line that reports the overlay is stable across boots and diffable.
        return new AdoptionOverlay(
            result.ToImmutable(),
            [.. applied.Order(StringComparer.OrdinalIgnoreCase)]);
    }

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
