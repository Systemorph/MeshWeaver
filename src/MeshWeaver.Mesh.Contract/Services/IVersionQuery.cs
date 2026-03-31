using System.Runtime.CompilerServices;
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
/// Queries historical versions of MeshNodes.
/// Implementations: PostgreSqlVersionQuery, FileSystemVersionQuery.
/// </summary>
public interface IVersionQuery
{
    /// <summary>
    /// Gets all version summaries for a node, ordered by version descending (newest first).
    /// </summary>
    IAsyncEnumerable<MeshNodeVersion> GetVersionsAsync(
        string path, CancellationToken ct = default);

    /// <summary>
    /// Gets the full MeshNode at a specific version.
    /// </summary>
    Task<MeshNode?> GetVersionAsync(
        string path, long version, JsonSerializerOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the latest version of a node strictly before the given version number.
    /// Used by undo to find the pre-change state.
    /// </summary>
    Task<MeshNode?> GetVersionBeforeAsync(
        string path, long beforeVersion, JsonSerializerOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Writes a versioned snapshot of a node for history tracking.
    /// Called by create/update handlers after persisting.
    /// Default implementation is no-op; overridden by FileSystemVersionStore and PostgreSqlVersionQuery.
    /// </summary>
    Task WriteVersionAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        => Task.CompletedTask;
}
