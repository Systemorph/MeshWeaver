using System.Reactive.Linq;
using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Summary of a single version of a MeshNode.
/// </summary>
public record MeshNodeVersion(
    string Path,
    long Version,
    DateTimeOffset LastModified,
    string? ChangedBy,
    string? Name,
    string? NodeType
);

/// <summary>
/// Queries historical versions of MeshNodes. All operations are
/// <see cref="IObservable{T}"/>-shaped — no <c>Task</c> / <c>IAsyncEnumerable</c>
/// surface, so callers compose with <c>.SelectMany</c> / <c>.Subscribe</c>
/// inside hub-reachable code without bridging to a Task. See
/// <c>Doc/Architecture/AsynchronousCalls.md</c> ("Return type MUST be IObservable&lt;T&gt;").
///
/// <para>Implementations in THIS repository: <c>FileSystemVersionStore</c>,
/// <c>RoutingVersionQuery</c>, <c>NoOpVersionQuery</c> — the last of which is registered
/// unconditionally (<c>TryAddSingleton</c>), so this service is never null and a null check can
/// never stand in for "is there history here". Ask <see cref="RetainsHistory"/> instead.</para>
///
/// <para>🚨 <b>"There is no database implementation" is FALSE, and this doc said it.</b> It was
/// written when MeshWeaver#3264 searched core alone — but the storage backends had already left
/// for MeshWeaver.Plugins, and every one of them implements this interface AND overrides
/// <see cref="WriteVersion"/>: <c>PostgreSqlVersionQuery</c> and
/// <c>PostgreSqlPartitionedVersionQuery</c> (both registered by <c>PostgreSqlExtensions</c> with a
/// plain <c>AddSingleton</c> that deliberately precedes — and therefore beats — the no-op's
/// <c>TryAdd</c>), <c>CosmosVersionQuery</c> and <c>SnowflakeVersionQuery</c>. Measured 2026-09-04
/// against memex.meshweaver.cloud, a Postgres deployment: 32 recorded versions on one node. A
/// grep that stops at this repository's boundary answers a question about core, never about a
/// deployment (MeshWeaver#3288).</para>
/// </summary>
public interface IVersionQuery
{
    /// <summary>
    /// Whether this deployment RETAINS history — i.e. whether an empty answer from
    /// <see cref="GetVersions"/> means "this node has none" or "nothing is ever recorded here".
    ///
    /// <para>🚨 Those are different facts and callers were unable to tell them apart. The
    /// surfaces built on history — Versions, VersionDiff, RestoreVersion, RestoreFromPointInTime —
    /// each carry an honest "version history not available" branch, and every one of them was
    /// UNREACHABLE: they guard on <c>GetService&lt;IVersionQuery&gt;() == null</c>, but
    /// <c>NoOpVersionQuery</c> is registered unconditionally (<c>TryAddSingleton</c>), so the
    /// service is never null and the no-op's empty answer was reported as a data-shaped miss
    /// (MeshWeaver#3264). Ask this instead of null-checking the service.</para>
    ///
    /// <para>Defaults to <c>true</c>: an implementation that stores versions need not opt in,
    /// and a third-party implementation is not silently declared history-less.</para>
    /// </summary>
    bool RetainsHistory => true;

    /// <summary>
    /// Streams every version summary for a node, ordered by version descending
    /// (newest first). Cold observable — completes after the last version is
    /// emitted.
    /// </summary>
    IObservable<MeshNodeVersion> GetVersions(string path);

    /// <summary>
    /// Emits the full <see cref="MeshNode"/> at a specific version, then completes.
    /// Emits <c>null</c> + completes if the version doesn't exist.
    /// </summary>
    IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options);

    /// <summary>
    /// Emits the latest version of a node strictly before the given version
    /// number, then completes. Emits <c>null</c> + completes if no earlier
    /// version exists. Used by undo / rollback to find the pre-change state.
    /// </summary>
    IObservable<MeshNode?> GetVersionBefore(string path, long beforeVersion, JsonSerializerOptions options);

    /// <summary>
    /// Writes a versioned snapshot of a node for history tracking. Called by
    /// create / update handlers AFTER the storage layer has assigned the new
    /// monotonic <see cref="MeshNode.Version"/> — the caller MUST chain this
    /// off the persistence emission so the post-save Version is used (a
    /// pre-save Version writes the new content into an OLDER version's
    /// snapshot, overwriting history).
    /// <para>Default implementation is a no-op observable (single emission of
    /// the input + completion); overridden by <c>FileSystemVersionStore</c> here, and by every
    /// database backend in MeshWeaver.Plugins (Postgres, Cosmos, Snowflake) — the claim that none
    /// of them did was the same core-only grep corrected in the interface remarks above.</para>
    /// </summary>
    IObservable<MeshNode> WriteVersion(MeshNode node, JsonSerializerOptions options)
        => System.Reactive.Linq.Observable.Return(node);

    /// <summary>
    /// Removes EVERY recorded version of the node at <paramref name="path"/>, then emits once and
    /// completes. Called by the storage layer's version writer for a node whose NodeType declares
    /// <c>NodeTypeDefinition.KeepsHistory = false</c> after the node is DELETED (each WRITE calls the
    /// bounded <see cref="DiscardNewestVersions"/> instead). See
    /// <c>Doc/Architecture/MeshNodeVersioning</c> → "Types That Keep No History".
    ///
    /// <para>🚨 The emitted value is a STATEMENT, never a guess: <c>true</c> means this store holds
    /// no history for the path any more (it purged it, or it never retains any); <c>false</c> means
    /// this store CANNOT purge, so whatever it recorded is still there. The default answers
    /// <c>true</c> only for a store that does not retain history at all
    /// (<see cref="RetainsHistory"/> = <c>false</c>) and <c>false</c> otherwise — a retaining
    /// implementation that has not implemented the purge must never be reported as having
    /// purged.</para>
    /// </summary>
    /// <param name="path">The node path whose history is removed.</param>
    /// <returns>Whether no history remains in this store for <paramref name="path"/>.</returns>
    IObservable<bool> PurgeVersions(string path)
        => System.Reactive.Linq.Observable.Return(!RetainsHistory);

    /// <summary>
    /// Removes at most <paramref name="maxRows"/> of the recorded versions of the node at
    /// <paramref name="path"/>, NEWEST first, then emits once and completes. Cold: the delete runs on
    /// Subscribe. Called by the storage layer's version writer after EACH write of a node whose
    /// NodeType declares <c>NodeTypeDefinition.KeepsHistory = false</c>: the newest rows are the
    /// snapshot the store recorded on its own for this write (the Postgres
    /// <c>mesh_node_copy_to_history</c> trigger) and any recorded during a cross-replica opt-out
    /// window. The bound is what keeps a write cheap on a node that accumulated a large history
    /// before its type opted out: that backlog is drained by the data-retention sweep
    /// (<see cref="DrainHistory"/>), never inside a write.
    ///
    /// <para>🚨 The emitted value is a STATEMENT, as on <see cref="PurgeVersions"/>: <c>true</c> means
    /// this store removed them (or retains no history at all), <c>false</c> that it did not. The default
    /// never deletes more than <paramref name="maxRows"/>: when the node holds at most that many recorded
    /// versions it calls <see cref="PurgeVersions"/> (what the version writer called before this member
    /// existed, so a type with a short history — a mail copy — still leaves none), and when it holds more
    /// it DECLINES with <c>false</c>, leaving the rows to the retention sweep. A store that implemented
    /// only the unbounded purge can therefore never be driven into a mass delete inside a write.</para>
    /// </summary>
    /// <param name="path">The node path whose newest recorded versions are removed.</param>
    /// <param name="maxRows">The most rows removed.</param>
    /// <returns>Whether the store removed them (or holds no history).</returns>
    IObservable<bool> DiscardNewestVersions(string path, int maxRows)
        => GetVersions(path)
            .Take(maxRows + 1)
            .Count()
            .SelectMany(count => count <= maxRows ? PurgeVersions(path) : Observable.Return(false));

    /// <summary>
    /// Removes the recorded versions of <paramref name="partition"/> that <paramref name="drain"/>
    /// selects — for each node whose newest version carries a declared node type, every OTHER version
    /// written before that type's cutoff (see <see cref="VersionHistoryDrain"/>) — at most
    /// <see cref="VersionHistoryDrain.MaxRows"/> of them, then emits once and completes. Cold. Called by
    /// the data-retention sweep for the NodeTypes that declare a short history window
    /// (<c>NodeTypeDefinition.HistoryMaxAge</c>) or none at all (<c>KeepsHistory = false</c>)
    /// (<c>Doc/Architecture/DataRetention</c> → "Short-lived history").
    ///
    /// <para>A separate member from <see cref="PruneHistory"/> on purpose: a store that does not know
    /// this rule must never be handed it in a shape it would read as an ordinary prune. The default
    /// answers <see cref="VersionHistoryPruneOutcome.NotSupported"/> for a store that retains history
    /// and "pruned, nothing removed" for one that retains none, so adding the member obliges no
    /// implementer and a retaining store is never reported as drained when it was not.</para>
    /// </summary>
    /// <param name="partition">The partition (first path segment) whose history is drained.</param>
    /// <param name="drain">The node types and the bound.</param>
    /// <returns>Whether the store could drain, and how many rows it removed.</returns>
    IObservable<VersionHistoryPruneOutcome> DrainHistory(string partition, VersionHistoryDrain drain)
        => System.Reactive.Linq.Observable.Return(
            RetainsHistory ? VersionHistoryPruneOutcome.NotSupported : VersionHistoryPruneOutcome.Pruned(0));

    /// <summary>
    /// Removes the recorded versions of <paramref name="partition"/> that <paramref name="prune"/>
    /// selects (see <see cref="VersionHistoryPrune"/> for the rule: never the current version,
    /// never a version inside the window, never the state at the window's opening), at most
    /// <see cref="VersionHistoryPrune.MaxRows"/> of them, then emits once and completes. Cold: the
    /// delete runs on Subscribe. Called by the data-retention sweep
    /// (<c>Doc/Architecture/DataRetention</c>).
    ///
    /// <para>The default answers <see cref="VersionHistoryPruneOutcome.NotSupported"/> for a store that
    /// retains history and has not implemented the prune, and "pruned, nothing removed" for one that
    /// retains none. A retaining store is never reported as having pruned when it did not. Because
    /// the member has a default, adding it obliges no implementer.</para>
    /// </summary>
    /// <param name="partition">The partition (first path segment) whose history is pruned.</param>
    /// <param name="prune">The rule and its bounds.</param>
    /// <returns>Whether the store could prune, and how many rows it removed.</returns>
    IObservable<VersionHistoryPruneOutcome> PruneHistory(string partition, VersionHistoryPrune prune)
        => System.Reactive.Linq.Observable.Return(
            RetainsHistory ? VersionHistoryPruneOutcome.NotSupported : VersionHistoryPruneOutcome.Pruned(0));
}
