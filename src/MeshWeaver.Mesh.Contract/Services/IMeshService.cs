using System.Runtime.CompilerServices;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Unified public service for all mesh node operations: CRUD and queries.
/// Scoped per hub — each hub gets its own instance with the correct IMessageHub injected.
/// Write operations route through the message bus for proper security enforcement via validators.
/// For moves, use <see cref="MoveNodeRequest"/> via hub.Post().
/// </summary>
public interface IMeshService
{
    // === Node CRUD (Observable) ===
    // Returns cold IObservable that captures AccessContext at call time.
    // Emits a single item then completes, or errors on failure.
    // For await-based callers, use the extension methods in MeshServiceExtensions
    // (CreateNodeAsync, UpdateNodeAsync, DeleteNodeAsync, CreateTransientAsync).

    /// <summary>
    /// Creates a new node with validation.
    /// Routes through CreateNodeRequest for proper security enforcement.
    /// Identity is captured eagerly from AccessService at call time.
    /// </summary>
    IObservable<MeshNode> CreateNode(MeshNode node);

    /// <summary>
    /// Updates an existing node with validation.
    /// Routes through UpdateNodeRequest for proper security enforcement.
    /// </summary>
    IObservable<MeshNode> UpdateNode(MeshNode node);

    /// <summary>
    /// Deletes a node and all its descendants (bottom to top).
    /// Routes through DeleteNodeRequest for proper security enforcement.
    /// </summary>
    IObservable<bool> DeleteNode(string path);

    /// <summary>
    /// Creates a transient node for UI creation flows.
    /// The node is persisted in Transient state but NOT confirmed.
    /// Call DeleteNode to cancel or CreateNode to confirm.
    /// </summary>
    IObservable<MeshNode> CreateTransient(MeshNode node);

    // === Query ===

    /// <summary>
    /// Query nodes and partition objects with full-text search, filtering, and scoping.
    /// Uses GitHub-style query syntax (e.g., "nodeType:Story status:Open laptop").
    /// </summary>
    IAsyncEnumerable<object> QueryAsync(MeshQueryRequest request, CancellationToken ct = default);

    /// <summary>
    /// Autocomplete query - given a namespace, find best matching subnodes.
    /// Returns suggestions ordered by path length first (for path-based autocomplete).
    /// </summary>
    IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        int limit = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Autocomplete query with specified ordering mode.
    /// </summary>
    IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null,
        CancellationToken ct = default);

    /// <summary>
    /// Creates an observable query that monitors data sources for changes and emits updates.
    /// The first emission contains the full initial result set (ChangeType = Initial).
    /// Subsequent emissions contain incremental changes (Added, Updated, Removed).
    /// </summary>
    IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request);

    /// <summary>
    /// Selects a single property value from a node at the given path.
    /// Efficient way to get one property without loading the full Content blob.
    /// </summary>
    Task<T?> SelectAsync<T>(string path, string property, CancellationToken ct = default);

    /// <summary>
    /// Tries to look up a MeshNode at the exact path and return its PreRenderedHtml.
    /// Used during Blazor prerender for instant display without full path resolution.
    /// Returns null if no node exists or no pre-rendered HTML is available.
    /// </summary>
    Task<string?> GetPreRenderedHtmlAsync(string path, CancellationToken ct = default);

}
