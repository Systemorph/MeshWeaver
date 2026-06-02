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

    /// <summary>
    /// Copies a node (and optionally its subtree and satellites) to a new path.
    /// By default copies descendants but NOT satellites. Set
    /// <see cref="CopyNodeRequest.IncludeSatellites"/> on a custom request to also copy satellites.
    /// Routes through <see cref="CopyNodeRequest"/>; emits the root node at the new target path.
    /// </summary>
    IObservable<MeshNode> CopyNode(string sourcePath, string targetPath,
        bool includeDescendants = true, bool includeSatellites = false);

    // === Query (IObservable only — no async surface) ===

    /// <summary>
    /// Creates an observable query that monitors data sources for changes and emits updates.
    /// The first emission contains the full initial result set (ChangeType = Initial).
    /// Subsequent emissions contain incremental changes (Added, Updated, Removed).
    /// </summary>
    IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request);

    /// <summary>
    /// Unified query surface — each registered <see cref="IMeshQueryProvider"/>
    /// emits a LIVE snapshot of <see cref="QueryResult"/> rows; the aggregator combines
    /// via <see cref="System.Reactive.Linq.Observable.CombineLatest{TSource}(IEnumerable{IObservable{TSource}})"/>,
    /// dedupes by <see cref="QueryResult.Path"/>, and orders by score. Each emission is the
    /// full current result set (updates until disposed). Providers run their I/O leaf inside
    /// the IIoPool — the call here never blocks the mesh hub's action block.
    /// </summary>
    IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request);

    /// <summary>
    /// Unified autocomplete surface — same shape as <see cref="Query"/>
    /// but each provider's stream is wrapped with <c>.StartWith(empty)</c> so
    /// partial autocomplete results render immediately without waiting for slow
    /// providers. Live snapshot set; updates until disposed.
    /// </summary>
    IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
        int limit = 10,
        string? contextPath = null,
        string? context = null);

    /// <summary>
    /// Selects a single property value from a node at the given path (single-emission
    /// observable). Efficient way to get one property without loading the full Content blob.
    /// </summary>
    IObservable<T?> Select<T>(string path, string property);

    /// <summary>
    /// Looks up a MeshNode at the exact path and emits its PreRenderedHtml.
    /// Used during Blazor prerender for instant display. Returns an observable
    /// of the latest <see cref="MeshNode.PreRenderedHtml"/> (or <c>null</c> when
    /// no node exists or no HTML is cached). Subscribers compose with
    /// <c>Select</c>/<c>Subscribe</c> — no await, no <see cref="Task"/> bridge.
    /// </summary>
    IObservable<string?> GetPreRenderedHtml(string path);

}
