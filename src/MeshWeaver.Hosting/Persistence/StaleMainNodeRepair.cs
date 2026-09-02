using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// The BACKWARD half of Systemorph/MeshWeaver#2939 — a detection-driven repair for mesh nodes whose
/// <see cref="MeshNode.MainNode"/> is a self-default frozen in a partition the node no longer
/// occupies, which drops them out of <c>is:main</c> (SQL <c>n.main_node = n.path</c>) and therefore
/// out of every listing, while <c>get</c> keeps returning them Active and fully formed.
///
/// <para><b>Why a repair is needed at all when the mint sites are fixed.</b> #2939 and
/// MeshWeaver.Plugins#1053 corrected the writers (<c>MeshNode.WithPath</c> and the create/upsert
/// paths now run <c>RepairStaleSelfDefaultMainNode</c> on everything they store), so no NEW row can
/// acquire the shape. Neither fix touches a row already persisted: the defect moved out of the code
/// and into the DATA, and only a pass over the data closes it. The same distinction
/// <c>SelfTypedDeclarationDurableRepair</c> draws for #2425/#2506.</para>
///
/// <para><b>🚨 The corruption is SELF-PROTECTING, which dictates both halves of the design
/// (#2970).</b> Authorization resolves against <see cref="MeshNode.MainNode"/>, not
/// <see cref="MeshNode.Path"/>, so a node pointing into another partition has its permission check
/// answered by THAT partition's scope: the field that is wrong is the field that decides who may
/// correct it, and the obvious re-stamp is refused —
/// <c>patch @Hosting/Skill/deployment {"mainNode":"Hosting/Skill/deployment"}</c> →
/// <i>Access denied: user 'rbuergi' lacks Update permission on 'Hosting/Skill/deployment'</i>.
/// Hence the write runs under <see cref="ImpersonationScopeExtensions.RunAsSystem{T}"/> — the
/// sanctioned "legitimate infrastructure" case, and the ONE shape allowed here: never
/// <c>Observable.Using(access.ImpersonateAsSystem, …)</c>, whose store/restore land on different
/// threads and latch System onto the subscriber (#1790, and a ratchet guard that may only shrink).
/// </para>
///
/// <para><b>🚨 And it dictates that detection CANNOT use a query.</b> The condition's own definition
/// is "invisible to the index": <c>is:main</c> keeps exactly <c>main_node = path</c>, and Postgres'
/// <c>search_across_schemas</c> hard-filters every union branch on that predicate
/// <i>unconditionally</i> — not only when the caller asks for <c>is:main</c>. A query-driven
/// detector would therefore pass its tests against a permissive in-memory provider and find ZERO
/// rows on the deployment that has the corruption. Enumeration runs on
/// <see cref="IStorageAdapter.ListChildPaths"/> / <see cref="IStorageAdapter.ListDescendantPaths"/>
/// instead — the authoritative path-routed tree walk the recursive-delete planner is built on
/// (#839), which routes by PATH and so cannot be hidden from by a wrong <c>MainNode</c>.</para>
///
/// <para><b>One predicate, shared with the forward fix.</b> A candidate is anything
/// <see cref="MeshExtensions.IsStaleSelfDefaultMainNode"/> accepts — the very method the create and
/// upsert paths apply to every node they write. Two predicates would drift, and the drift would be
/// invisible in exactly the way this defect is.</para>
///
/// <para><b>Both shapes, and neither assumes the other end exists.</b> The issue described a mutual
/// cycle (<c>Hosting/Skill/deployment</c> ↔ <c>Skill/deployment</c>), and a repair written only for
/// cycles would silently skip the rest — two of the seven measured on memex on 2026-09-01 are
/// DANGLING, and an eighth node (<c>Skill/email</c>) shares the signature and is absent from the
/// issue's list. Because the predicate is a per-node SHAPE test rather than a pair test, all of them
/// are found by construction, a partner is never required to exist, and repairing one end never
/// depends on the other. The pointed-at node is read only to CLASSIFY the finding for the report
/// (<see cref="StaleMainNodeShape"/>) — never to decide whether to repair. This is also why the
/// hardcoded list from the issue is deliberately not used anywhere in this file.</para>
///
/// <para><b>Idempotent, and safe on a healthy mesh.</b> The repair writes
/// <c>MainNode = Path</c>, after which <see cref="MeshNode.HasExplicitMainNode"/> reads
/// <c>false</c> and the predicate can no longer match — so a second run reads the same rows and
/// writes nothing, and a mesh with zero affected nodes performs zero writes and reports an empty
/// finding list. Repairing one end of a cycle does not disturb the other end, which is found and
/// repaired on its own merits in the same pass.</para>
///
/// <para><b>Reports per node, not a count.</b> Every finding carries the path, the stale pointer,
/// the classified shape and the outcome, so a run against a live portal produces evidence a
/// maintainer can act on — including for the question this repair deliberately does NOT answer.
/// Classification of the WHOLE candidate set completes before any write, so every finding describes
/// the state as FOUND rather than as the sweep's own writes left it (see <c>Process</c>).</para>
///
/// <para><b>🚨 What this deliberately does NOT do: delete anything.</b> A cycle is two Active
/// copies of one node with identical content, and whether the platform-partition copy should be
/// deleted as a duplicate is a maintainer's decision about CONTENT, not a mechanical one. Both ends
/// are re-stamped to point at themselves and both copies are left in place. See
/// <c>Doc/Architecture/StaleMainNodeRepair</c> → "The unresolved duplicate-copy question".</para>
///
/// <para><b>🚨 Nothing here self-arms.</b> This is a static one-shot pipeline, not an
/// <c>IHostedService</c>, and nothing in the composition root calls it — deploying an image
/// containing this file runs no repair. That is deliberate: running it against a live portal is a
/// separate decision, and <see cref="Detect"/> exists so the decision can be taken on measured
/// evidence (it writes nothing and is safe on production).</para>
/// </summary>
public static class StaleMainNodeRepair
{
    /// <summary>
    /// How many paths one <see cref="IStorageAdapter.ReadMany"/> asks for. Batching keeps a
    /// whole-mesh sweep's working set and its per-round-trip cost bounded — Postgres turns one batch
    /// into a single <c>WHERE (namespace, id) IN (…)</c>, and an unbatched sweep of a large mesh
    /// would build one query per node or one query of every node, both of which are worse.
    /// </summary>
    private const int ReadBatchSize = 250;

    /// <summary>
    /// Finds every node carrying the stale-self-default shape and reports them WITHOUT writing
    /// anything — the measurement pass. Safe to run against a live portal: it reads the storage tree
    /// and nothing else.
    /// </summary>
    /// <param name="hub">Hub supplying the storage adapter, serializer options and logger.</param>
    /// <param name="roots">
    /// Partitions / subtrees to sweep. Null or empty sweeps the whole mesh, starting from the
    /// storage adapter's root listing.
    /// </param>
    /// <returns>A cold observable emitting one report and completing. <b>Subscribe to run it.</b></returns>
    public static IObservable<StaleMainNodeRepairReport> Detect(
        IMessageHub hub, IReadOnlyCollection<string>? roots = null)
        => Sweep(hub, roots, write: false);

    /// <summary>
    /// Finds every node carrying the stale-self-default shape and re-stamps
    /// <see cref="MeshNode.MainNode"/> to the node's own <see cref="MeshNode.Path"/>, restoring it to
    /// <c>is:main</c>.
    ///
    /// <para>The write goes through <c>GetMeshNodeStream(path).Update(…)</c> — the one mutation API,
    /// and the only route that can express this correction at all: a full-instance upsert cannot
    /// move a MainNode back ONTO the node's own path, because that intent is indistinguishable from
    /// the untouched default (see <see cref="MeshNode.HasExplicitMainNode"/>). It runs under
    /// <c>RunAsSystem</c> because the field being repaired is the field the permission check reads.
    /// </para>
    /// </summary>
    /// <param name="hub">Hub supplying the storage adapter, access service, serializer options and logger.</param>
    /// <param name="roots">
    /// Partitions / subtrees to sweep. Null or empty sweeps the whole mesh, starting from the
    /// storage adapter's root listing.
    /// </param>
    /// <returns>A cold observable emitting one report and completing. <b>Subscribe to run it.</b></returns>
    public static IObservable<StaleMainNodeRepairReport> Repair(
        IMessageHub hub, IReadOnlyCollection<string>? roots = null)
        => Sweep(hub, roots, write: true);

    private static IObservable<StaleMainNodeRepairReport> Sweep(
        IMessageHub hub, IReadOnlyCollection<string>? roots, bool write)
        => Observable.Defer(() =>
        {
            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(StaleMainNodeRepair));
            var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
            if (storage is null)
            {
                // A mesh with no storage adapter has no durable rows to sweep. Reported as an
                // explicit zero rather than an absent line: "the sweep never considered a row" and
                // "the sweep found nothing" must not look the same.
                logger?.LogInformation(
                    "[StaleMainNodeRepair] no storage adapter on this hub — nothing to sweep");
                return Observable.Return(StaleMainNodeRepairReport.Empty(write));
            }

            var options = hub.JsonSerializerOptions;
            return EnumeratePaths(storage, roots, logger)
                .SelectMany(paths => ReadCandidates(storage, paths, options)
                    .SelectMany(read => Process(hub, storage, read.Candidates, options, write, logger)
                        .Select(findings => new StaleMainNodeRepairReport(
                            findings, paths.Count, read.NodesRead, write))))
                .Do(report => logger?.LogInformation(
                    "[StaleMainNodeRepair] sweep completed ({Mode}): {Scanned} path(s) enumerated, "
                    + "{Read} node(s) read, {Found} stale MainNode(s) found, {Repaired} repaired, "
                    + "{Failed} failed [{Paths}]",
                    write ? "repair" : "detect-only",
                    report.PathsScanned, report.NodesRead, report.Findings.Count,
                    report.RepairedCount, report.FailedCount,
                    string.Join(", ", report.Findings.Select(f => $"{f.Path}→{f.StaleMainNode}"))));
        });

    /// <summary>
    /// Every node path in scope, enumerated by PATH through the storage tree — never through the
    /// query index, which by the definition of this defect cannot see the rows being looked for.
    /// A null/empty <paramref name="roots"/> starts from the adapter's root listing, whose DIRECTORY
    /// paths are recursed into as well as its node paths (a node-less intermediate level still
    /// anchors real descendants).
    /// </summary>
    private static IObservable<ImmutableList<string>> EnumeratePaths(
        IStorageAdapter storage, IReadOnlyCollection<string>? roots, ILogger? logger)
        => (roots is { Count: > 0 }
            ? Observable.Return((
                Roots: (IEnumerable<string>)roots,
                Seed: ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase)
                    .Union(roots)))
            : storage.ListChildPaths(null).Take(1).Select(level =>
            {
                var nodes = (level.NodePaths ?? []).Where(p => !string.IsNullOrEmpty(p)).ToArray();
                var directories = (level.DirectoryPaths ?? []).Where(p => !string.IsNullOrEmpty(p));
                return (
                    Roots: nodes.Concat(directories).Distinct(StringComparer.OrdinalIgnoreCase),
                    Seed: ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, nodes));
            }))
        .SelectMany(start => start.Roots
            // Sequential, not merged: a whole-mesh walk must not open one storage round-trip per
            // partition at once. Ordering also keeps the log line reproducible.
            .Select(root => storage.ListDescendantPaths(root)
                // Per-root tolerance: a partition that cannot be listed (an absent schema, a
                // backend hiccup) ends THIS root, not the sweep. Logged, never swallowed silently —
                // a root that answered nothing must be distinguishable from a root that is clean.
                .Catch<IReadOnlyCollection<string>, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[StaleMainNodeRepair] enumerating descendants of '{Root}' failed; any stale "
                        + "MainNode under it is NOT covered by this sweep", root);
                    return Observable.Return<IReadOnlyCollection<string>>([]);
                }))
            .Concat()
            .Aggregate(start.Seed, (acc, descendants) => acc.Union(descendants)))
        .Select(set => set.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToImmutableList());

    /// <summary>
    /// Reads the enumerated paths in batches and keeps the ones the SHARED predicate accepts. Paths
    /// that no longer resolve to a node are simply absent from <see cref="IStorageAdapter.ReadMany"/>'s
    /// output, so a tree that moved under the sweep costs nothing.
    ///
    /// <para>The row count is carried out alongside the candidates because it is the sweep's proof
    /// of work: candidates alone cannot distinguish "read 900 rows, none of them wrong" from "read
    /// nothing at all", and those are the two outcomes a clean report must never conflate.</para>
    /// </summary>
    private static IObservable<(ImmutableList<MeshNode> Candidates, int NodesRead)> ReadCandidates(
        IStorageAdapter storage, ImmutableList<string> paths, JsonSerializerOptions options)
        => paths.Count == 0
            ? Observable.Return((ImmutableList<MeshNode>.Empty, 0))
            : paths.Chunk(ReadBatchSize)
                .Select(batch => storage.ReadMany(batch, options).ToList())
                // Concat, not Merge: one batch in flight at a time.
                .Concat()
                .Aggregate(
                    (Candidates: ImmutableList<MeshNode>.Empty, NodesRead: 0),
                    (acc, batch) => (
                        acc.Candidates.AddRange(batch.Where(n => n.IsStaleSelfDefaultMainNode())),
                        acc.NodesRead + batch.Count));

    /// <summary>
    /// Classifies every candidate, and only THEN repairs them.
    ///
    /// <para>🚨 <b>The two phases must not interleave, and a test caught it doing so.</b> Both ends
    /// of a cycle are candidates, and repairing is what makes an end stop pointing back — so a pass
    /// that classified each node just before writing it saw the FIRST end already repaired by the
    /// time it classified the SECOND, and reported the pair as one <see cref="StaleMainNodeShape.Cycle"/>
    /// plus one <see cref="StaleMainNodeShape.DanglingUnrelatedTarget"/>. The repair was correct
    /// either way; the EVIDENCE was not, and the evidence is what the maintainer reads to answer the
    /// duplicate-copy question — a half-reported cycle understates exactly the pairs that question is
    /// about. Classifying the whole set first makes every finding describe the state as FOUND, which
    /// is the only thing a report should ever describe.</para>
    /// </summary>
    private static IObservable<ImmutableList<StaleMainNodeFinding>> Process(
        IMessageHub hub,
        IStorageAdapter storage,
        ImmutableList<MeshNode> candidates,
        JsonSerializerOptions options,
        bool write,
        ILogger? logger)
        => candidates.Count == 0
            ? Observable.Return(ImmutableList<StaleMainNodeFinding>.Empty)
            // Phase 1 — reads only. Sequential, and complete before any write is composed.
            : candidates
                .Select(node => Classify(storage, node, options)
                    .Select(shape => (Node: node, Shape: shape)))
                .Concat()
                .ToList()
                .SelectMany(classified => write
                    // Phase 2 — the writes, also sequential: a whole-mesh sweep must not fan
                    // per-node-hub writes out all at once.
                    ? classified
                        .Select(c => RepairOne(hub, c.Node, c.Shape, logger))
                        .Concat()
                        .Aggregate(
                            ImmutableList<StaleMainNodeFinding>.Empty,
                            (acc, finding) => acc.Add(finding))
                    : Observable.Return(classified
                        .Select(c => new StaleMainNodeFinding(
                            c.Node.Path, c.Node.MainNode, c.Shape, Repaired: false, Error: null))
                        .ToImmutableList()));

    /// <summary>
    /// What the stale pointer points AT — reported, never used to decide whether to repair.
    ///
    /// <para>🚨 Read through <see cref="IStorageAdapter.Read"/>, which answers <c>null</c> for an
    /// absent path, and NOT through <c>GetMeshNodeStream(target)</c>: a point stream read of a node
    /// that does not exist is a framework defect, not merely a miss — the owner answers a routing
    /// NotFound that terminates the stream AND opens the storm-breaker on that path, and the breaker
    /// fast-fails WRITES too. On a DANGLING finding — where the target is absent by definition —
    /// classifying through the stream would suppress the very repair this pass is here to perform.
    /// </para>
    /// </summary>
    private static IObservable<StaleMainNodeShape> Classify(
        IStorageAdapter storage, MeshNode node, JsonSerializerOptions options)
        => storage.Read(node.MainNode, options)
            .Take(1)
            .Select(target => target is null
                ? StaleMainNodeShape.DanglingMissingTarget
                : string.Equals(target.MainNode, node.Path, StringComparison.OrdinalIgnoreCase)
                    ? StaleMainNodeShape.Cycle
                    : StaleMainNodeShape.DanglingUnrelatedTarget)
            // Classification is REPORTING. A target that cannot be read must never cost the repair,
            // so an unreadable target is reported as unclassified and the node is repaired anyway.
            .Catch<StaleMainNodeShape, Exception>(_ =>
                Observable.Return(StaleMainNodeShape.Unclassified));

    /// <summary>
    /// The one write. <c>MainNode = Path</c> through the mesh-node stream, under a System scope
    /// sealed at Subscribe.
    /// </summary>
    private static IObservable<StaleMainNodeFinding> RepairOne(
        IMessageHub hub, MeshNode node, StaleMainNodeShape shape, ILogger? logger)
    {
        var path = node.Path;
        var stale = node.MainNode;
        var access = hub.ServiceProvider.GetService<AccessService>();
        // 🚨 RunAsSystem, never Observable.Using(access.ImpersonateAsSystem, …) (#1790): Rx runs a
        // Using factory on the subscribing thread and disposes it on the terminating one, latching
        // System onto whatever the subscriber does next. RunAsSystem opens the scope across this
        // cold Update's Subscribe — which is where the write primitive eager-captures the
        // AccessContext — and leaves it on the way out of that same Subscribe.
        return access.RunAsSystem(() => hub.GetMeshNodeStream(path)
                .Update(current => current with { MainNode = current.Path }))
            .Take(1)
            .Select(_ =>
            {
                logger?.LogInformation(
                    "[StaleMainNodeRepair] repaired '{Path}': mainNode '{Stale}' → '{Restored}' "
                    + "({Shape}) — it was Active and absent from is:main (#2939/#2970)",
                    path, stale, path, shape);
                return new StaleMainNodeFinding(path, stale, shape, Repaired: true, Error: null);
            })
            // Per-node tolerance, and the reason the report carries an Error per finding rather than
            // a bare count: one path that cannot be written must not stop the remaining nodes from
            // being repaired, and the failure must come back as EVIDENCE naming the node, not as a
            // swallowed exception. Re-running is safe — an unrepaired node still matches the
            // predicate, a repaired one no longer does.
            .Catch<StaleMainNodeFinding, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[StaleMainNodeRepair] repairing '{Path}' failed; it keeps mainNode '{Stale}' "
                    + "and stays absent from is:main until the next run", path, stale);
                return Observable.Return(
                    new StaleMainNodeFinding(path, stale, shape, Repaired: false, Error: ex.Message));
            });
    }
}

/// <summary>
/// What a stale <see cref="MeshNode.MainNode"/> points at. Reported for evidence; never used to
/// decide whether a node is repaired.
/// </summary>
public enum StaleMainNodeShape
{
    /// <summary>The pointed-at node exists and points back at this one — a mutual cycle.</summary>
    Cycle,

    /// <summary>The pointed-at node does not exist. The pointer dangles.</summary>
    DanglingMissingTarget,

    /// <summary>
    /// The pointed-at node exists but does not point back — it names some third node, or itself.
    /// </summary>
    DanglingUnrelatedTarget,

    /// <summary>The pointed-at node could not be read, so the shape is unknown. Repaired anyway.</summary>
    Unclassified,
}

/// <summary>
/// One node found carrying a stale self-default <see cref="MeshNode.MainNode"/>, and what the sweep
/// did about it.
/// </summary>
/// <param name="Path">The node's own path — what <see cref="MeshNode.MainNode"/> should equal.</param>
/// <param name="StaleMainNode">The pointer as found.</param>
/// <param name="Shape">What that pointer pointed at.</param>
/// <param name="Repaired">Whether this run re-stamped the node.</param>
/// <param name="Error">Why the repair failed, when it did; null otherwise.</param>
public sealed record StaleMainNodeFinding(
    string Path,
    string StaleMainNode,
    StaleMainNodeShape Shape,
    bool Repaired,
    string? Error);

/// <summary>
/// The outcome of one sweep — per node, so a run against a live portal produces evidence rather than
/// a count.
/// </summary>
/// <param name="Findings">Every node found with the stale shape, in path order.</param>
/// <param name="PathsScanned">How many paths the storage-tree enumeration produced.</param>
/// <param name="NodesRead">
/// How many rows storage actually returned for those paths. Carried separately from
/// <paramref name="Findings"/> because it is the sweep's proof of work: without it, "read 900 rows,
/// none of them wrong" and "read nothing at all" are the same empty report.
/// </param>
/// <param name="Wrote">Whether this was a repair pass (<c>true</c>) or detect-only (<c>false</c>).</param>
public sealed record StaleMainNodeRepairReport(
    ImmutableList<StaleMainNodeFinding> Findings,
    int PathsScanned,
    int NodesRead,
    bool Wrote)
{
    /// <summary>A sweep that had nothing to look at.</summary>
    /// <param name="wrote">Whether the caller asked for a repair pass.</param>
    public static StaleMainNodeRepairReport Empty(bool wrote) =>
        new(ImmutableList<StaleMainNodeFinding>.Empty, PathsScanned: 0, NodesRead: 0, wrote);

    /// <summary>How many findings this run successfully re-stamped.</summary>
    public int RepairedCount => Findings.Count(f => f.Repaired);

    /// <summary>How many findings this run tried and failed to re-stamp.</summary>
    public int FailedCount => Findings.Count(f => f.Error is not null);
}
