using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Wires <see cref="IMeshQueryCore.ObserveQuery{T}"/> into a hub's data
/// context as a synced collection of <see cref="MeshNode"/>s. Two pieces are
/// registered together:
///
/// <list type="bullet">
///   <item>A <see cref="VirtualDataSource"/> hosting a
///     <see cref="SyncedQueryMeshNodes"/> typesource (the read side — a
///     path-keyed dictionary fold of every query's
///     <see cref="QueryResultChange{T}"/> deltas, gated on per-query Initial,
///     re-emitted as <c>IEnumerable&lt;MeshNode&gt;</c> snapshots).</item>
///   <item>A workspace-level reducer that resolves
///     <c>MeshNodeReference(path)</c> to the workspace's cached per-(addr, ref)
///     remote stream when <paramref name="path"/> is in this source's live
///     path set. <c>.Update(...)</c> on the resulting stream propagates
///     through the synchronization protocol to the owning per-node hub. The
///     reducer returns null for paths outside the source's set so a sibling
///     synced source (e.g. a different query) gets a chance — first match
///     wins.</item>
/// </list>
/// </summary>
public static class SyncedQueryDataSourceExtensions
{
    /// <summary>
    /// Per-workspace lazy registry of named synced mesh-queries.
    /// Auto-initialised on first <c>GetQuery(...)</c> call for the workspace
    /// — no DI registration required. Garbage-collected with the workspace.
    /// </summary>
    private static readonly ConditionalWeakTable<IWorkspace, SyncedQueryRegistry> _registries = new();

    /// <summary>
    /// Resolves the per-workspace synced-query registry — used internally by
    /// <see cref="GetQuery(IWorkspace, object)"/> /
    /// <see cref="WithMeshQuery"/> and by the framework's
    /// <c>HandleDeleteNodeRequest</c> to walk every registered synced query
    /// and route a synchronous <see cref="SyncedQueryMeshNodes.NotifyDeleted"/>
    /// for each path the query owns.
    /// </summary>
    internal static SyncedQueryRegistry RegistryFor(IWorkspace workspace) =>
        _registries.GetValue(workspace, _ => new SyncedQueryRegistry());


    /// <summary>
    /// Registers a synced <see cref="MeshNode"/> collection on this data context.
    /// </summary>
    /// <param name="data">The data context.</param>
    /// <param name="id">Unique data-source id (used as the persistence partition).</param>
    /// <param name="query">Mesh query string (see Query Syntax docs); must select <see cref="MeshNode"/>s.</param>
    /// <param name="collectionName">Workspace collection name; defaults to <c>nameof(MeshNode)</c>.</param>
    public static DataContext AddSyncedQuery(
        this DataContext data,
        object id,
        string query,
        string? collectionName = null)
    {
        // Capture the collection name we'll register under so the reducer can
        // look up THIS specific synced source (multiple synced sources on the
        // same hub coexist as separate workspace collections — e.g., "Red" /
        // "Green" — even though they share the MeshNode CLR type).
        var sourceCollection = collectionName ?? nameof(MeshNode);

        return data
            .WithVirtualDataSource(id, vs =>
            {
                var typeSource = new SyncedQueryMeshNodes(vs.Workspace, vs.Id, query, collectionName);
                return vs.WithTypeSource(typeof(MeshNode), typeSource);
            })
            .Configure(rm => rm.AddWorkspaceReferenceStream<MeshNode>((workspace, reference, configCb) =>
            {
                if (reference is not MeshNodeReference { Path: { } path }
                    || string.IsNullOrEmpty(path))
                    return null;

                // Resolve THIS source by its registered collection name; the
                // reducer is registered per-source so each source's reducer
                // only fires for its own paths.
                if (workspace.DataContext.GetTypeSource(sourceCollection) is not SyncedQueryMeshNodes typeSource)
                    return null;
                if (!typeSource.Owns(path))
                    return null;

                // The workspace's per-(addr, ref) cache de-duplicates this
                // stream with any other writer in the hub asking for the same
                // (path, MeshNodeReference) — every Update goes through one
                // upstream subscription per node, regardless of how many
                // synced collections include the same path.
                return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                    new Address(path), new MeshNodeReference());
            }));
    }

    /// <summary>
    /// Convenience overload: chain on a <see cref="VirtualDataSource"/> the way
    /// older callers expect. The reducer registration happens inside
    /// <see cref="AddSyncedQuery"/>; this overload preserves the
    /// <c>WithVirtualDataSource(... vs => vs.WithMeshQuery(...))</c> pattern by
    /// simply adding the synced typesource to <paramref name="ds"/>. Callers
    /// that need the reducer registered too should prefer
    /// <see cref="AddSyncedQuery"/>.
    /// </summary>
    public static VirtualDataSource WithMeshQuery(
        this VirtualDataSource ds,
        string query,
        string? collectionName = null)
    {
        var typeSource = new SyncedQueryMeshNodes(ds.Workspace, ds.Id, query, collectionName);
        // Register in the workspace's lazy per-workspace registry so callers
        // can look it up later via workspace.GetQuery(id) — O(1) name-keyed
        // lookup, no TypeSources iteration. Registering both the typesource
        // and its cached stream lets the framework's delete handler walk
        // every synced query and push direct NotifyDeleted events.
        RegistryFor(ds.Workspace).Register(ds.Id, typeSource, typeSource.StreamUpdates());
        return ds.WithTypeSource(typeof(MeshNode), typeSource);
    }

    /// <summary>
    /// Retrieves the live <see cref="MeshNode"/> collection observable for the
    /// synced query registered under <paramref name="id"/> on this workspace.
    /// Returns <c>null</c> when no synced query has been registered with that
    /// id. Subscribers receive the current collection on subscribe (replayed)
    /// and every subsequent change as the underlying mesh-query result set
    /// evolves.
    ///
    /// <para>O(1) name-keyed lookup against the per-workspace registry —
    /// no <c>TypeSources</c> iteration.</para>
    /// </summary>
    public static IObservable<IEnumerable<MeshNode>>? GetQuery(
        this IWorkspace workspace, object id)
        => RegistryFor(workspace).Get(id);

    /// <summary>
    /// Get-or-create overload: returns the cached observable for
    /// <paramref name="id"/> if one is already registered; otherwise spins up
    /// a new <see cref="SyncedQueryMeshNodes"/> on the workspace using the
    /// supplied <paramref name="queries"/> (one or more — the synced
    /// collection is the <em>union</em> of every query's result set),
    /// caches its observable in the registry under <paramref name="id"/>,
    /// and returns it.
    ///
    /// <para>The returned observable shares its upstream
    /// <see cref="IMeshQueryProvider.ObserveQuery"/> subscriptions (one per
    /// query) via <c>Replay(1).RefCount()</c>: it starts syncing on the
    /// first subscriber and pauses when none remain. The registry entry
    /// persists for the lifetime of the workspace so subsequent
    /// <c>GetQuery(id)</c> calls hit the cache even when no live
    /// subscribers exist between calls.</para>
    ///
    /// <para>🚨 <b>Per-user RLS:</b> the synced query is cached by
    /// <c>(id, userId)</c>. Each user gets their own
    /// <see cref="SyncedQueryMeshNodes"/> instance which opens its upstream
    /// <see cref="IMeshQueryProvider.ObserveQuery"/> under the caller's
    /// identity — the secured surface of <see cref="MeshQuery"/> then
    /// applies per-result RLS validators at the source, so the cached
    /// snapshot only ever contains nodes that user has Read on.
    /// Two users sharing the same <paramref name="id"/> with different
    /// permissions get TWO independent caches; one user can never
    /// inherit another user's view by passing the same id. Background /
    /// system tasks (no AsyncLocal identity) share a single System-loaded
    /// cache keyed under the well-known <c>system-security</c> user.</para>
    /// </summary>
    public static IObservable<IEnumerable<MeshNode>> GetQuery(
        this IWorkspace workspace, object id, params string[] queries)
    {
        if (queries is null || queries.Length == 0)
            throw new ArgumentException("At least one query string is required.", nameof(queries));

        // Resolve the caller's identity at call time. ObjectId is the canonical
        // partition key + per-user cache key; if no AsyncLocal AccessContext is
        // set we treat the read as System infrastructure (background activations,
        // post-deploy seeds — the synced query mirror these need works the
        // same for everyone).
        var accessService = workspace.Hub.ServiceProvider.GetService<AccessService>();
        var userIdentity = accessService?.Context?.ObjectId
                           ?? accessService?.CircuitContext?.ObjectId
                           ?? WellKnownUsers.System;
        var userScopedKey = new SyncedQueryKey(id, userIdentity);

        var registry = RegistryFor(workspace);
        var existing = registry.Get(userScopedKey);
        if (existing is not null)
            return existing;

        var typeSource = new SyncedQueryMeshNodes(workspace, userScopedKey, queries, userIdentity: userIdentity);
        var observable = typeSource.StreamUpdates();
        registry.Register(userScopedKey, typeSource, observable);
        return observable;
    }
}

/// <summary>
/// Composite cache key for the per-user synced query registry. Both legs
/// participate in <c>GetHashCode</c> / <c>Equals</c>; two users with the
/// same logical query id (e.g. <c>"agents"</c>) get separate cache entries
/// so one user's RLS-filtered snapshot never leaks into another's view.
/// Keep this in the <c>Graph</c> namespace alongside the registry so the
/// registry's dictionary type doesn't have to be re-keyed.
/// </summary>
public readonly record struct SyncedQueryKey(object Id, string UserId)
{
    public override string ToString() => $"{Id}@{UserId}";
}
