using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Persistence.PartitionStorage;

/// <summary>
/// Standard configuration for a partition-storage hub — same shape for every
/// backend (Postgres, Embedded, FileSystem, …). The router spawns one of these
/// hubs per <c>(schema, table)</c> with a pre-built <see cref="IStorageAdapter"/>
/// bound to that table; the hub's actor scheduler serialises every message,
/// which lets the adapter hold one bounded connection (e.g.
/// <c>NpgsqlDataSource(MaxPoolSize=1)</c>).
///
/// <para>See <c>Doc/Architecture/PartitionStorageHubs.md</c> for the full
/// design.</para>
///
/// <para>Handlers follow the "no async in hub handlers" rule — work is
/// composed as <see cref="IObservable{T}"/> and a single <c>.Subscribe(...)</c>
/// at the end posts the response via <c>hub.Post(...)</c>. No <c>await</c>,
/// no <c>.ToTask()</c>.</para>
/// </summary>
public static class PartitionStorageHubExtensions
{
    /// <summary>
    /// Registers the standard partition-storage handlers and stores the
    /// table-bound <paramref name="adapter"/> on the hub's service container
    /// so handlers can resolve it.
    /// </summary>
    public static MessageHubConfiguration AddPartitionStorageHandlers(
        this MessageHubConfiguration config,
        IStorageAdapter adapter)
    {
        return config
            .WithServices(s => s.AddSingleton(adapter))
            .WithHandler<WriteBatchRequest>(HandleWriteBatch)
            .WithHandler<DeleteBatchRequest>(HandleDeleteBatch)
            .WithHandler<ReadNodeRequest>(HandleReadNode)
            .WithHandler<ExistsRequest>(HandleExists)
            .WithHandler<ListChildPathsRequest>(HandleListChildPaths);
    }

    // ── Handlers ─────────────────────────────────────────────────────────

    private static IMessageDelivery HandleWriteBatch(
        IMessageHub hub, IMessageDelivery<WriteBatchRequest> request)
    {
        var adapter = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();

        // TODO transaction: today's IStorageAdapter only has per-node Write.
        // Once IStorageAdapter exposes WriteBatch(...) the loop collapses into
        // one transactional call and validation / activity-logging slots in
        // around it. For the migration we keep per-node writes; the actor
        // scheduler still serialises so concurrent writers can't interleave.
        request.Message.Nodes
            .ToObservable()
            .SelectMany(node => adapter.Write(node, request.Message.Options))
            .ToList()
            .Subscribe(
                written => hub.Post(
                    new WriteBatchResponse(written.ToImmutableList()),
                    o => o.ResponseFor(request)),
                ex => hub.Post(
                    new WriteBatchResponse(ImmutableList<MeshNode>.Empty, Error: ex.Message),
                    o => o.ResponseFor(request)));

        return request.Processed();
    }

    private static IMessageDelivery HandleDeleteBatch(
        IMessageHub hub, IMessageDelivery<DeleteBatchRequest> request)
    {
        var adapter = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();

        request.Message.Paths
            .ToObservable()
            .SelectMany(path => adapter.Delete(path))
            .ToList()
            .Subscribe(
                deleted => hub.Post(
                    new DeleteBatchResponse(deleted.ToImmutableList()),
                    o => o.ResponseFor(request)),
                ex => hub.Post(
                    new DeleteBatchResponse(ImmutableList<string>.Empty, Error: ex.Message),
                    o => o.ResponseFor(request)));

        return request.Processed();
    }

    private static IMessageDelivery HandleReadNode(
        IMessageHub hub, IMessageDelivery<ReadNodeRequest> request)
    {
        var adapter = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();

        adapter.Read(request.Message.Path, request.Message.Options)
            .Subscribe(
                node => hub.Post(new ReadNodeResponse(node), o => o.ResponseFor(request)),
                _ => hub.Post(new ReadNodeResponse(null), o => o.ResponseFor(request)));

        return request.Processed();
    }

    private static IMessageDelivery HandleExists(
        IMessageHub hub, IMessageDelivery<ExistsRequest> request)
    {
        var adapter = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();

        adapter.Exists(request.Message.Path)
            .Subscribe(
                exists => hub.Post(new ExistsResponse(exists), o => o.ResponseFor(request)),
                _ => hub.Post(new ExistsResponse(false), o => o.ResponseFor(request)));

        return request.Processed();
    }

    private static IMessageDelivery HandleListChildPaths(
        IMessageHub hub, IMessageDelivery<ListChildPathsRequest> request)
    {
        var adapter = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();

        adapter.ListChildPaths(request.Message.ParentPath)
            .Subscribe(
                result => hub.Post(
                    new ListChildPathsResponse(
                        result.NodePaths.ToImmutableList(),
                        result.DirectoryPaths.ToImmutableList()),
                    o => o.ResponseFor(request)),
                _ => hub.Post(
                    new ListChildPathsResponse(ImmutableList<string>.Empty, ImmutableList<string>.Empty),
                    o => o.ResponseFor(request)));

        return request.Processed();
    }
}
