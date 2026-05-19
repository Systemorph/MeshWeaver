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
/// IObservable wrapper that detects fire-and-forget callsites at runtime. If an
/// instance is garbage-collected without ever having <c>Subscribe</c> called on
/// it, a warning is logged via <see cref="ILoggerFactory"/> resolved from the
/// supplied <see cref="IServiceProvider"/>. This catches the cold-observable bug
/// where a caller invokes a side-effect-on-subscribe API (e.g.
/// <c>workspace.GetMeshNodeStream().Update(...)</c>) without subscribing â€” the
/// side effect silently never runs and the caller has no compile-time signal.
/// </summary>
internal sealed class RequireSubscribeObservable<T> : IObservable<T>
{
    private readonly IObservable<T> _inner;
    private readonly string _what;
    private readonly IServiceProvider _services;
    private int _subscribed;

    public RequireSubscribeObservable(IObservable<T> inner, string what, IServiceProvider services)
    {
        _inner = inner;
        _what = what;
        _services = services;
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        Interlocked.Exchange(ref _subscribed, 1);
        return _inner.Subscribe(observer);
    }

    ~RequireSubscribeObservable()
    {
        if (_subscribed != 0) return;
        try
        {
            var logger = _services.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Mesh.RequireSubscribe");
            logger?.LogWarning(
                "Fire-and-forget callsite detected: '{What}' returned a cold IObservable that was never subscribed â€” the side effect did NOT run. Add .Subscribe(_ => {{ }}, ex => logger.LogWarning(ex, ...)) at the callsite. See Doc/Architecture/AsynchronousCalls.md â†’ 'Subscribe is mandatory'.",
                _what);
        }
        catch
        {
            // Finalizer must never throw â€” service provider may already be disposed.
        }
    }
}

/// <summary>
/// Reactive handle to a <see cref="MeshNode"/> for both reads and writes. The handle
/// is path-aware: with no path it targets the workspace's own hub MeshNode; with a
/// path matching the workspace's hub address it also targets own; otherwise it
/// targets the remote per-node hub via <c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>.
/// Implements <see cref="IObservable{MeshNode}"/> so existing <c>.Where</c>/<c>.Select</c>
/// read consumers keep working unchanged. Writers call <see cref="Update"/> â€” which
/// returns an <see cref="IObservable{MeshNode}"/> that the caller MUST Subscribe to.
/// The Update side effect runs on Subscribe; errors flow to <c>OnError</c>. No
/// fire-and-forget at any callsite.
/// </summary>
public sealed class MeshNodeStreamHandle : IObservable<MeshNode>
{
    private readonly IWorkspace _workspace;
    private readonly string? _path;

    internal MeshNodeStreamHandle(IWorkspace workspace, string? path = null)
    {
        _workspace = workspace;
        _path = path;
    }

    private bool IsOwn => _path is null
        || string.Equals(_path, _workspace.Hub.Address.Path, StringComparison.Ordinal)
        || string.Equals(_path, _workspace.Hub.Address.ToString(), StringComparison.Ordinal);

    private ISynchronizationStream<MeshNode> GetStream()
    {
        if (IsOwn)
            return _workspace.GetStream(new MeshNodeReference())
                ?? throw new InvalidOperationException(
                    "MeshNode stream is not available â€” the workspace has no MeshNodeReference reducer.");
        return _workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(_path!), new MeshNodeReference());
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(IObserver<MeshNode> observer)
    {
        try
        {
            return GetStream()
                .Where(change => change.Value != null)
                .Select(change => change.Value!)
                .Subscribe(observer);
        }
        catch (Exception ex)
        {
            observer.OnError(ex);
            return Disposable.Empty;
        }
    }

    /// <summary>
    /// Applies <paramref name="update"/> to the targeted MeshNode and returns an
    /// <see cref="IObservable{MeshNode}"/> that emits the post-update node on the first
    /// emission past the pre-update snapshot. <b>Caller MUST Subscribe</b> â€” the cold
    /// observable's side effect runs on Subscribe, errors flow to <c>OnError</c>.
    /// <list type="bullet">
    ///   <item><description><b>Own</b> (no path or path == hub address): writes through
    ///     the data source's primary EntityStore stream so all local subscribers see
    ///     the new value and the type source's persister picks it up for save.</description></item>
    ///   <item><description><b>Remote</b> (path != hub address): calls
    ///     <c>ISynchronizationStream&lt;MeshNode&gt;.Update(...)</c> on the workspace's
    ///     cached remote stream so the patch routes to the owning per-node hub via
    ///     the data sync protocol.</description></item>
    /// </list>
    /// </summary>
    public IObservable<MeshNode> Update(Func<MeshNode, MeshNode> update)
        => new RequireSubscribeObservable<MeshNode>(
            IsOwn ? UpdateOwn(update) : UpdateRemote(update),
            $"MeshNodeStreamHandle.Update(path='{_path ?? "<own>"}')",
            _workspace.Hub.ServiceProvider);

    private IObservable<MeshNode> UpdateOwn(Func<MeshNode, MeshNode> update)
        => Observable.Create<MeshNode>(observer =>
        {
            var refStream = _workspace.GetStream(new MeshNodeReference())
                ?? throw new InvalidOperationException(
                    "MeshNode stream is not available â€” the workspace has no MeshNodeReference reducer.");

            var dataSource = _workspace.DataContext.GetDataSourceForType(typeof(MeshNode));
            if (dataSource == null)
                throw new InvalidOperationException("No data source registered for MeshNode");
            var dsStream = dataSource.GetStreamForPartition(null)
                ?? throw new InvalidOperationException("No stream for MeshNode partition");

            // Subscribe before applying the partition write so the post-update emission
            // is never missed. Baseline = pre-write version; first emission past it wins.
            long? baseline = null;
            var sub = refStream.Subscribe(change =>
            {
                if (baseline is null)
                {
                    baseline = change.Version;
                    return;
                }
                if (change.Version <= baseline.Value) return;
                if (change.Value is { } node)
                {
                    observer.OnNext(node);
                    observer.OnCompleted();
                }
            }, observer.OnError);

            // Resolve the target Path: an explicit _path wins, otherwise default to the
            // workspace's own hub path. The InstanceCollection holds the OWN MeshNode
            // alongside any satellite nodes the data source has loaded (e.g. NodeType
            // hubs accumulate Release/* satellites after each compile). Looking up by
            // terminal-segment Id alone is non-deterministic when multiple instances
            // share the same Id; match on the full Path so the OWN node is always
            // resolved correctly. When neither path is available, fall back to
            // FirstOrDefault â€” only legacy single-instance shapes hit this branch.
            var targetPath = _path ?? _workspace.Hub.Address.Path;

            try
            {
                dsStream.Update(state =>
                {
                    var store = state ?? new EntityStore();
                    var collection = store.Collections.GetValueOrDefault(nameof(MeshNode));
                    if (collection is null)
                        throw new InvalidOperationException(
                            $"MeshNode collection not found. Available: [{string.Join(", ", store.Collections.Keys)}]");

                    var current = string.IsNullOrEmpty(targetPath)
                        ? collection.Instances.Values.OfType<MeshNode>().FirstOrDefault()
                        : collection.Instances.Values.OfType<MeshNode>()
                            .FirstOrDefault(n => string.Equals(n.Path, targetPath, StringComparison.OrdinalIgnoreCase));
                    if (current == null)
                        throw new InvalidOperationException(
                            $"MeshNode '{targetPath ?? "<own>"}' not found. Available: [{string.Join(", ", collection.Instances.Keys.Select(k => k.ToString()))}]");

                    var updated = update(current);
                    // Framework-driven Version: stamp the owning hub's monotonic
                    // logical clock so subscribers can distinguish a real change
                    // from a routing-stream echo. Lambdas no longer have to
                    // remember to bump â€” the framework owns the clock.
                    updated = updated with { Version = _workspace.Hub.Version };
                    var newStore = store.Update(nameof(MeshNode), c => c.Update(updated.Id, updated));
                    return dsStream.ApplyChanges(new EntityStoreAndUpdates(newStore,
                        [new EntityUpdate(nameof(MeshNode), updated.Id, updated) { OldValue = current }],
                        dsStream.StreamId));
                }, observer.OnError);
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }

            return sub;
        });

    /// <summary>
    /// Remote write — eventual-consistency path. Snapshots the local mirror's
    /// view, applies the user lambda, computes a recursive JSON-merge-patch
    /// (RFC 7396) DIFF between the snapshot and the result, then posts that
    /// diff via <see cref="PatchDataRequest"/> to the owning per-node hub.
    /// The owner merges the diff against its CURRENT authoritative state,
    /// preserving fields touched by concurrent writers from other mirrors —
    /// no <c>ChangeType.Full</c> overwrite, no "stale-mirror clobber".
    /// <para>The returned observable emits the post-merge MeshNode once the
    /// owner's response arrives, then completes.</para>
    /// </summary>
    private IObservable<MeshNode> UpdateRemote(Func<MeshNode, MeshNode> update)
        => Observable.Create<MeshNode>(observer =>
        {
            var diagLogger = _workspace.Hub.ServiceProvider
                .GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Mesh.MeshNodeStreamHandle");
            diagLogger?.LogDebug(
                "[UpdateRemote] BEGIN hub={Hub} target={Path}",
                _workspace.Hub.Address, _path);

            var remoteStream = _workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(_path!), new MeshNodeReference());

            var composite = new CompositeDisposable();

            // Wait for the per-node hub's initial SubscribeResponse before
            // running the user lambda — the lambda needs a non-null current
            // to diff against. A 30 s outer timeout bounds the wait so a
            // missing per-node hub surfaces with a precise TimeoutException.
            var initialSub = remoteStream
                .Timeout(TimeSpan.FromSeconds(30))
                .Where(change => change.Value is not null)
                .Take(1)
                .Subscribe(
                    change =>
                    {
                        var current = change.Value!;
                        try
                        {
                            var updated = update(current);
                            if (ReferenceEquals(updated, current) || Equals(updated, current))
                            {
                                diagLogger?.LogDebug(
                                    "[UpdateRemote] NO-OP hub={Hub} target={Path} — lambda returned unchanged",
                                    _workspace.Hub.Address, _path);
                                observer.OnNext(current);
                                observer.OnCompleted();
                                return;
                            }

                            var jsonOpts = _workspace.Hub.JsonSerializerOptions;
                            var currentNode = System.Text.Json.JsonSerializer
                                .SerializeToNode(current, jsonOpts) as System.Text.Json.Nodes.JsonObject
                                ?? new System.Text.Json.Nodes.JsonObject();
                            var updatedNode = System.Text.Json.JsonSerializer
                                .SerializeToNode(updated, jsonOpts) as System.Text.Json.Nodes.JsonObject
                                ?? new System.Text.Json.Nodes.JsonObject();
                            var patch = ComputeMergePatchDiff(currentNode, updatedNode);

                            if (patch.Count == 0)
                            {
                                diagLogger?.LogDebug(
                                    "[UpdateRemote] NO-OP hub={Hub} target={Path} — diff empty after serialisation",
                                    _workspace.Hub.Address, _path);
                                observer.OnNext(current);
                                observer.OnCompleted();
                                return;
                            }

                            var patchJson = patch.ToJsonString(jsonOpts);
                            diagLogger?.LogDebug(
                                "[UpdateRemote] POST-PATCH hub={Hub} target={Path} keys={Keys}",
                                _workspace.Hub.Address, _path, patch.Count);

                            // Post PatchDataRequest to the OWNER. The owner reads its
                            // OWN current state, recursively merges the diff (RFC 7396),
                            // and commits — leaving any fields not in the diff intact.
                            //
                            // CRITICAL: stamp the caller's AccessContext on the
                            // outgoing delivery. Without this, Orleans routing
                            // delivers the request with accessContext=null, the
                            // owner's RLS denies it, and the patch silently drops
                            // → mirror never sees an echo → caller hangs on the
                            // 10s post-update timeout. The PostPipeline warning
                            // "<msg> posted with no AccessContext" surfaces this.
                            var accessService = _workspace.Hub.ServiceProvider
                                .GetService<MeshWeaver.Messaging.AccessService>();
                            var capturedContext = accessService?.Context
                                ?? accessService?.CircuitContext;
                            var delivery = _workspace.Hub.Post(
                                new PatchDataRequest(new MeshNodeReference(), new RawJson(patchJson)),
                                o =>
                                {
                                    o = o.WithTarget(new Address(_path!));
                                    return capturedContext is null
                                        ? o
                                        : o.WithAccessContext(capturedContext);
                                });
                            if (delivery == null)
                            {
                                observer.OnError(new InvalidOperationException(
                                    $"Post of PatchDataRequest returned null for {_path}"));
                                return;
                            }

                            // Wait for the next non-null emission from the remote stream
                            // — that's the owner's echo of the merged state. Complete then.
                            var postSub = remoteStream
                                .Skip(1)
                                .Where(c => c.Value is not null)
                                .Take(1)
                                .Timeout(TimeSpan.FromSeconds(10))
                                .Subscribe(
                                    c =>
                                    {
                                        observer.OnNext(c.Value!);
                                        observer.OnCompleted();
                                    },
                                    observer.OnError);
                            composite.Add(postSub);
                        }
                        catch (Exception ex)
                        {
                            observer.OnError(ex);
                        }
                    },
                    ex =>
                    {
                        diagLogger?.LogWarning(ex,
                            "[UpdateRemote] ERROR hub={Hub} target={Path} type={ExType}",
                            _workspace.Hub.Address, _path, ex.GetType().Name);
                        if (ex is TimeoutException)
                        {
                            observer.OnError(new TimeoutException(
                                $"Update aborted: no initial state arrived for '{_path}' within 30s. " +
                                "Likely causes — (1) RLS silently rejected the prior CreateNode, " +
                                "(2) the path is misspelled / points at a namespace no NodeType claims, " +
                                "(3) the node was deleted between create and update, or (4) the per-node " +
                                "hub activated but its MeshDataSource didn't load the node from persistence."));
                        }
                        else
                        {
                            observer.OnError(ex);
                        }
                    });
            composite.Add(initialSub);
            return composite;
        });

    /// <summary>
    /// Recursive JSON-merge-patch (RFC 7396) diff between two equally-shaped
    /// JsonObjects. The result, when applied to <paramref name="current"/>
    /// (e.g. via <c>HandlePatchDataRequest</c>'s recursive merge), reproduces
    /// <paramref name="updated"/>. Keys in current and missing in updated
    /// emit <c>null</c> (RFC 7396 remove). Equal values emit nothing.
    /// </summary>
    private static System.Text.Json.Nodes.JsonObject ComputeMergePatchDiff(
        System.Text.Json.Nodes.JsonObject current,
        System.Text.Json.Nodes.JsonObject updated)
    {
        var patch = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, updatedValue) in updated)
        {
            var currentValue = current[key];
            if (currentValue is System.Text.Json.Nodes.JsonObject co
                && updatedValue is System.Text.Json.Nodes.JsonObject uo)
            {
                var sub = ComputeMergePatchDiff(co, uo);
                if (sub.Count > 0)
                    patch[key] = sub;
                continue;
            }
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(currentValue, updatedValue))
                patch[key] = updatedValue?.DeepClone();
        }
        // Keys removed in updated → emit null per RFC 7396.
        foreach (var (key, _) in current)
        {
            if (!updated.ContainsKey(key))
                patch[key] = null;
        }
        return patch;
    }
}

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
    /// The returned <see cref="MeshNodeStreamHandle"/> implements
    /// <see cref="IObservable{MeshNode}"/> so all existing read consumers (Where/Select
    /// chains) keep working. Writers call <c>.Update(update)</c> on the same handle â€”
    /// returns <c>IObservable&lt;MeshNode&gt;</c> that callers MUST Subscribe to. No
    /// fire-and-forget; subscribe with <c>(_ =&gt; â€¦, ex =&gt; logger.LogWarning(ex, â€¦))</c>.
    /// </para>
    /// </summary>
    public static MeshNodeStreamHandle GetMeshNodeStream(this IWorkspace workspace)
        => new(workspace);

    /// <summary>
    /// Reactive handle to a MeshNode at <paramref name="path"/>. Path-aware:
    /// <list type="number">
    ///   <item><description><b>Own hub</b> â€” when <paramref name="path"/> matches the
    ///     workspace's hub address: handle reads/writes via the local
    ///     <see cref="MeshNodeReference"/> reducer + data source primary stream.</description></item>
    ///   <item><description><b>Remote</b> â€” subscribes to and writes through the owning
    ///     per-node hub via <c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>.</description></item>
    /// </list>
    /// Callers Subscribe (read) or call <c>.Update(update).Subscribe(...)</c> (write).
    /// If the node does not exist at <paramref name="path"/>, the per-node hub never
    /// activates and the remote subscription does not emit â€” bound reads with
    /// <c>.Take(1).Timeout(...)</c> and treat absence as "not found".
    /// </summary>
    public static MeshNodeStreamHandle GetMeshNodeStream(this IWorkspace workspace, string path)
        => new(workspace, path);

    /// <summary>
    /// Forwarder that delegates to <see cref="MeshNodeStreamHandle.Update"/>. Returns
    /// <see cref="IObservable{MeshNode}"/>; CALLERS MUST SUBSCRIBE â€” the cold observable's
    /// side effect runs on Subscribe, errors flow to <c>OnError</c>.
    /// <para>
    /// Prefer <c>workspace.GetMeshNodeStream().Update(update)</c> at new callsites â€” uniform
    /// read/write API on a single handle. This forwarder is kept so the existing 30+
    /// callsites can migrate incrementally.
    /// </para>
    /// </summary>
    [Obsolete("Use workspace.GetMeshNodeStream(path?).Update(update).Subscribe(...) â€” uniform read/write API; callers must subscribe so writes can't be silently dropped.")]
    public static IObservable<MeshNode> UpdateMeshNode(this IWorkspace workspace,
        Func<MeshNode, MeshNode> update,
        string? nodePath = null)
        => (nodePath is null
            ? workspace.GetMeshNodeStream()
            : workspace.GetMeshNodeStream(nodePath)).Update(update);

    /// <summary>
    /// One-shot read of the <see cref="MeshNode"/> at <paramref name="path"/> via
    /// the owning per-node hub's <see cref="MeshNodeReference"/> reducer. Posts a
    /// <see cref="GetDataRequest"/> + registers a callback â€” true request/response,
    /// no <c>SubscribeRequest</c>, no lingering subscription. Use this instead of
    /// <c>workspace.GetMeshNodeStream(path).Take(1)</c> for handlers / helpers /
    /// click actions that just need the current value once.
    ///
    /// <para>
    /// Emits <c>null</c> on timeout, on routing failure (the node does not exist â€”
    /// routing returns DeliveryFailure with NotFound; routing NEVER falls back to
    /// an ancestor, so a returned non-null node is always the requested path), or
    /// when the response carries no data. Failures during deserialisation also fall
    /// through as <c>null</c>; turn on debug-level logging on this type to see the
    /// underlying exception.
    /// </para>
    ///
    /// <para>
    /// For a <b>live</b> single-node subscription that re-emits on every change,
    /// use <see cref="GetMeshNodeStream(IWorkspace, string)"/> instead â€” and stay
    /// subscribed (no <c>.Take(1)</c>). See <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </para>
    /// </summary>
    public static IObservable<MeshNode?> GetMeshNode(this IMessageHub hub, string path,
        TimeSpan? timeout = null)
        => Observable.Create<MeshNode?>(observer =>
        {
            var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            var emitted = 0;
            // Inner hub.Observe subscription tracker. Captured so the returned
            // disposable can tear it down â€” without this, the outer CTS-timeout
            // path emits null and the outer observer disposes, but the inner
            // Subscribe keeps the hub-level callback registered, surfacing as
            // a "pending callback at dispose" Quiescing-watchdog failure.
            IDisposable? innerSubscription = null;

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

                innerSubscription = hub.Observe(delivery)
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
                                    // Unexpected response â€” node not found / no handler.
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

            return Disposable.Create(() =>
            {
                innerSubscription?.Dispose();
                cts.Dispose();
            });
        });
}
