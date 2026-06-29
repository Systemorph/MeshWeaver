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
/// <para>Implementations: <c>FileSystemVersionStore</c>, <c>PostgreSqlVersionQuery</c>,
/// <c>RoutingVersionQuery</c>, <c>NoOpVersionQuery</c>.</para>
/// </summary>
public interface IVersionQuery
{
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
    /// the input + completion); overridden by <c>FileSystemVersionStore</c>
    /// and <c>PostgreSqlVersionQuery</c>.</para>
    /// </summary>
    IObservable<MeshNode> WriteVersion(MeshNode node, JsonSerializerOptions options)
        => System.Reactive.Linq.Observable.Return(node);
}
