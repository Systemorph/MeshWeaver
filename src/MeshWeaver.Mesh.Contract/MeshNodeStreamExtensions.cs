using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Reactive helpers for reading <see cref="MeshNode"/> content from workspaces.
/// Canonical replacement for the lagged
/// <c>QueryAsync&lt;MeshNode&gt;($"path:{path}").FirstOrDefaultAsync()</c> pattern.
/// </summary>
public static class MeshNodeStreamExtensions
{
    /// <summary>
    /// Reactive handle to the current hub's own MeshNode. No query index, no await,
    /// no staleness, live updates on content changes. Compose with <c>.Take(1)</c>
    /// for one-shot reads or keep subscribed for live views.
    /// <para>
    /// Wrapped in <see cref="System.Reactive.Linq.Observable.Defer{TResult}(System.Func{System.IObservable{TResult}})"/>
    /// so that hubs without the <see cref="MeshNodeReference"/> reducer (no
    /// <c>MeshDataSource</c> registered, or hub past <c>Started</c> mid-disposal)
    /// surface as <c>OnError</c> on the returned observable rather than throwing
    /// synchronously out of the call site — layout-area handlers can <c>.Catch</c>
    /// /<c>.OnErrorResumeNext</c> instead of crashing the whole render pipeline.
    /// </para>
    /// </summary>
    public static IObservable<MeshNode> GetMeshNodeStream(this IWorkspace workspace)
        => System.Reactive.Linq.Observable.Defer(() =>
        {
            var stream = workspace.GetStream(new MeshNodeReference())
                ?? throw new InvalidOperationException(
                    "MeshNode stream is not available — the workspace has no MeshNodeReference reducer.");
            return stream
                .Where(change => change.Value != null)
                .Select(change => change.Value!);
        });

    /// <summary>
    /// Reactive handle to a MeshNode at <paramref name="path"/>. Two cases only:
    /// <list type="number">
    ///   <item><description><b>Own hub</b> — when <paramref name="path"/> matches the hub's
    ///     address: returns the local <see cref="MeshNodeReference"/> stream.</description></item>
    ///   <item><description><b>Remote</b> — subscribes to the owning per-node hub via
    ///     <c>workspace.GetRemoteStream&lt;TReduced, TReference&gt;</c> +
    ///     <see cref="MeshNodeReference"/>.</description></item>
    /// </list>
    /// The owning hub's <c>MeshDataSource</c> loads its MeshNode at init, so
    /// <c>GetStream(new MeshNodeReference())</c> on the owning side is always
    /// populated. If the node does not exist at <paramref name="path"/>, the
    /// per-node hub never activates and the remote subscription does not emit —
    /// callers should bound with <c>.Take(1).Timeout(...)</c> and treat absence
    /// of an emission as "not found".
    /// </summary>
    public static IObservable<MeshNode> GetMeshNodeStream(this IWorkspace workspace, string path)
    {
        if (string.Equals(workspace.Hub.Address.ToString(), path, StringComparison.Ordinal))
            return workspace.GetMeshNodeStream();

        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());
        return stream
            .Where(change => change.Value != null)
            .Select(change => change.Value!);
    }

    /// <summary>
    /// Updates the OWN MeshNode of <paramref name="workspace"/> by applying
    /// <paramref name="update"/> through the data source's MeshNode partition stream
    /// — the data source persister flushes to storage, subscribers receive the update
    /// via the workspace synchronization protocol, no <see cref="Services.IMeshStorage"/>
    /// calls (persistence belongs in <c>MeshDataSource</c> initialization only).
    ///
    /// <para>
    /// <b>Own-hub only.</b> To update a MeshNode at a remote address, post a
    /// <see cref="DataChangeRequest"/> with the already-built target node:
    /// <code>
    /// hub.Post(new DataChangeRequest { Updates = [updated] },
    ///     o =&gt; o.WithTarget(new Address(updated.Path)));
    /// </code>
    /// The owning hub's data layer (registered by <c>AddData()</c>) handles it
    /// natively — no <c>GetRemoteStream</c> / <c>SubscribeRequest</c> round trip,
    /// works even when no per-node hub has been separately activated.
    /// </para>
    /// </summary>
    /// <summary>
    /// Updates a MeshNode through the canonical write path:
    /// (a) the patch arrives in the sync stream — own (this hub) writes go through
    /// the data source's primary EntityStore stream; remote writes go through the
    /// remote MeshNodeReference stream's <c>.Update</c> which sends a
    /// synchronization message to the owning hub. (b) On the owning hub, the patch
    /// is validated via <c>WorkspaceOperations.Change</c> /
    /// <c>HandleDataChangeRequest</c>'s <c>RunChangeValidators</c>. (c) The
    /// validated patch is applied to the owning MeshDataSource. (d) The owning
    /// hub broadcasts the change to all subscribers through the synchronization
    /// protocol.
    /// <para>
    /// Read-modify-write: <paramref name="update"/> is invoked atop the latest
    /// node from the owning source so concurrent callers don't lose updates.
    /// </para>
    /// </summary>
    public static void UpdateMeshNode(this IWorkspace workspace,
        Func<MeshNode, MeshNode> update,
        string? nodePath = null)
    {
        var hubPath = workspace.Hub.Address.Path;
        if (!string.IsNullOrEmpty(nodePath)
            && !string.Equals(nodePath, hubPath, StringComparison.Ordinal))
        {
            // Cross-address: the synchronization protocol carries the patch to the
            // owning hub. The owning hub's data layer applies validation + writes.
            var remote = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(nodePath), new MeshNodeReference());
            remote.Update(current =>
            {
                if (current is null) return null;
                var updated = update(current);
                return new ChangeItem<MeshNode>(
                    updated, remote.StreamId, remote.StreamId,
                    Data.ChangeType.Patch, remote.Hub.Version,
                    [new EntityUpdate(nameof(MeshNode), updated.Id, updated) { OldValue = current }]);
            }, ex =>
            {
                workspace.Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.Mesh.UpdateMeshNode")
                    ?.LogError(ex, "[UPDATE-MESHNODE-REMOTE-FAIL] hub={HubAddress} target={NodePath}",
                        workspace.Hub.Address, nodePath);
                return Task.FromException(ex);
            });
            return;
        }

        // Own: write directly to the data source's primary EntityStore stream.
        // dsStream.Update reads the current EntityStore, applies the update via
        // ApplyChanges, persists via MeshNodeTypeSource's debounced flush, and
        // broadcasts through the synchronization protocol.
        var dataSource = workspace.DataContext.GetDataSourceForType(typeof(MeshNode));
        if (dataSource == null)
            throw new InvalidOperationException("No data source registered for MeshNode");
        var dsStream = dataSource.GetStreamForPartition(null)
            ?? throw new InvalidOperationException("No stream for MeshNode partition");

        dsStream.Update(state =>
        {
            var store = state ?? new EntityStore();
            var collection = store.Collections.GetValueOrDefault(nameof(MeshNode));
            if (collection is null)
                throw new InvalidOperationException(
                    $"MeshNode collection not found. Available: [{string.Join(", ", store.Collections.Keys)}]");

            var nodeId = nodePath?.Split('/').Last();
            var current = (nodeId is null
                ? collection.Instances.Values.FirstOrDefault()
                : collection.Instances.GetValueOrDefault(nodeId)) as MeshNode;
            if (current == null)
                throw new InvalidOperationException(
                    $"MeshNode '{nodePath}' not found. Available: [{string.Join(", ", collection.Instances.Keys.Select(k => k.ToString()))}]");

            var updated = update(current);
            var newStore = store.Update(nameof(MeshNode), c => c.Update(updated.Id, updated));
            return dsStream.ApplyChanges(new EntityStoreAndUpdates(newStore,
                [new EntityUpdate(nameof(MeshNode), updated.Id, updated) { OldValue = current }],
                dsStream.StreamId));
        }, ex =>
        {
            workspace.Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Mesh.UpdateMeshNode")
                ?.LogError(ex, "[UPDATE-MESHNODE-FAIL] hub={HubAddress} nodePath={NodePath} — propagating",
                    workspace.Hub.Address, nodePath);
            return Task.FromException(ex);
        });
    }

    /// <summary>
    /// One-shot read of the <see cref="MeshNode"/> at <paramref name="path"/> via
    /// the owning per-node hub's <see cref="MeshNodeReference"/> reducer. Posts a
    /// <see cref="GetDataRequest"/> + registers a callback — true request/response,
    /// no <c>SubscribeRequest</c>, no lingering subscription. Use this instead of
    /// <c>workspace.GetMeshNodeStream(path).Take(1)</c> for handlers / helpers /
    /// click actions that just need the current value once.
    ///
    /// <para>
    /// Emits <c>null</c> on timeout, on routing failure (the node does not exist —
    /// routing returns DeliveryFailure with NotFound; routing NEVER falls back to
    /// an ancestor, so a returned non-null node is always the requested path), or
    /// when the response carries no data. Failures during deserialisation also fall
    /// through as <c>null</c>; turn on debug-level logging on this type to see the
    /// underlying exception.
    /// </para>
    ///
    /// <para>
    /// For a <b>live</b> single-node subscription that re-emits on every change,
    /// use <see cref="GetMeshNodeStream(IWorkspace, string)"/> instead — and stay
    /// subscribed (no <c>.Take(1)</c>). See <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </para>
    /// </summary>
    public static IObservable<MeshNode?> GetMeshNode(this IMessageHub hub, string path,
        TimeSpan? timeout = null)
        => Observable.Create<MeshNode?>(observer =>
        {
            var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            var emitted = 0;

            void EmitOnce(MeshNode? node)
            {
                if (Interlocked.Exchange(ref emitted, 1) != 0) return;
                observer.OnNext(node);
                observer.OnCompleted();
            }

            cts.Token.Register(() => EmitOnce(null));

            try
            {
                var delivery = hub.Post(
                    new GetDataRequest(new MeshNodeReference()),
                    o => o.WithTarget(new Address(path)));
                if (delivery == null)
                {
                    EmitOnce(null);
                    return Disposable.Create(() => cts.Dispose());
                }

                hub.Observe(delivery)
                    .Subscribe(
                        d =>
                        {
                            try
                            {
                                if (d.Message is GetDataResponse resp)
                                {
                                    MeshNode? node = resp.Data as MeshNode;
                                    if (node == null && resp.Data is JsonElement je)
                                        node = je.Deserialize<MeshNode>(hub.JsonSerializerOptions);
                                    EmitOnce(node);
                                }
                                else
                                {
                                    // Unexpected response — node not found / no handler.
                                    EmitOnce(null);
                                }
                            }
                            catch (Exception ex)
                            {
                                var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                                    ?.CreateLogger("MeshWeaver.Mesh.GetMeshNode");
                                logger?.LogDebug(ex, "GetMeshNode callback failed for {Path}", path);
                                EmitOnce(null);
                            }
                        },
                        ex =>
                        {
                            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                                ?.CreateLogger("MeshWeaver.Mesh.GetMeshNode");
                            logger?.LogDebug(ex, "GetMeshNode delivery failed for {Path}", path);
                            EmitOnce(null);
                        });
            }
            catch (Exception ex)
            {
                var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.Mesh.GetMeshNode");
                logger?.LogDebug(ex, "GetMeshNode post failed for {Path}", path);
                EmitOnce(null);
            }

            return Disposable.Create(() => cts.Dispose());
        });
}
