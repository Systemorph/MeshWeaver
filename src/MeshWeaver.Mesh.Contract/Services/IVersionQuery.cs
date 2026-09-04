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
}
