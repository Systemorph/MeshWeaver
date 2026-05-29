using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Mesh.Services;
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
    private readonly IMeshNodeStreamCache? _cache;
    private readonly JsonSerializerOptions _jsonOptions;

    internal MeshNodeStreamHandle(IWorkspace workspace, string? path = null,
        IMeshNodeStreamCache? cache = null)
    {
        _workspace = workspace;
        _path = path;
        _cache = cache;
        _jsonOptions = workspace.Hub.JsonSerializerOptions;
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
        // 🚨 Open the remote MeshNode subscription under the system identity.
        // Reading MeshNode content is infrastructure (routing, path resolution,
        // permission probing, NodeType activation, satellite enumeration). The
        // user-rights gate lives at the APPLICATION layer where the value is
        // consumed (handlers, layout areas) — not at the sync-stream seam.
        // Without this, the SubscribeRequest is stamped with whatever ambient
        // identity happens to be on the thread (often `sync/<streamId>` for
        // workspace emission threads, or null), and the owner's
        // AccessControlPipeline denies because no AccessAssignment exists for
        // sync hub addresses — symptom: "user 'sync/…' lacks Read permission".
        // Matches MeshNodeStreamCache and PathResolutionService, both of which
        // also open MeshNode reads under ImpersonateAsSystem.
        var accessService = _workspace.Hub.ServiceProvider.GetService<AccessService>();
        using (accessService?.ImpersonateAsSystem())
        {
            return _workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(_path!), new MeshNodeReference());
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 🚨 Every emission passes through <see cref="EnsureTypedContent"/> so the
    /// subscriber always sees a typed <see cref="MeshNode.Content"/> — never a
    /// raw <see cref="JsonElement"/>. Different data sources store Content in
    /// different shapes (InMemory keeps typed instances, file-system / Postgres
    /// round-trip through JSON serialization and land as JsonElement). Without
    /// the boundary conversion, every callsite that pattern-matches
    /// <c>node.Content is MyType t</c> would have to remember to re-deserialise,
    /// and the silent <c>?? new MyType()</c> fallback (writing a default-valued
    /// content over a real one) is the bug class behind the CheckInbox /
    /// AppendUserInput silent-Status-reset failure mode. Round-trip is no-op
    /// when Content is already typed.
    /// </remarks>
    public IDisposable Subscribe(IObserver<MeshNode> observer)
    {
        try
        {
            var typedObserver = new TypedContentObserver(observer, _jsonOptions);
            // 🚨 Cross-hub reads route through IMeshNodeStreamCache (when one is
            // registered): one shared process-wide upstream subscription per
            // path. The cache holds the upstream alive; ad-hoc GetRemoteStream
            // here would open a separate handle, multiplying subscriptions and
            // making writes invisible to readers of the cached stream. See
            // Doc/GUI/ItemTemplateMeshNodeStreamBinding.
            if (_cache is not null && !IsOwn && _path is not null)
                return _cache.GetStream(_path)
                    .Where(n => n is not null)
                    .Subscribe(typedObserver);

            return GetStream()
                .Where(change => change.Value != null)
                .Select(change => change.Value!)
                .Subscribe(typedObserver);
        }
        catch (Exception ex)
        {
            observer.OnError(ex);
            return Disposable.Empty;
        }
    }

    /// <summary>
    /// Observer that round-trips <see cref="MeshNode.Content"/> through the
    /// workspace's <see cref="JsonSerializerOptions"/> when it arrives as a
    /// raw <see cref="JsonElement"/>. No-op when Content is already typed.
    /// Applied at the <see cref="MeshNodeStreamHandle"/> boundary so every
    /// subscriber sees the same typed shape regardless of how the underlying
    /// data source stores the value.
    /// <para>
    /// When <see cref="EnsureTypedContent"/> throws (deserialization /
    /// missing TypeRegistry entry), the exception is routed to
    /// <see cref="IObserver{T}.OnError"/> so subscribers see the typed
    /// <see cref="MeshNodeStreamException"/> in their error handler — never
    /// up the producer stack where it would tear down unrelated streams.
    /// </para>
    /// </summary>
    private sealed class TypedContentObserver(IObserver<MeshNode> inner, JsonSerializerOptions jsonOptions) : IObserver<MeshNode>
    {
        public void OnNext(MeshNode value)
        {
            MeshNode typed;
            try
            {
                typed = EnsureTypedContent(value, jsonOptions);
            }
            catch (System.Exception ex)
            {
                inner.OnError(ex);
                return;
            }
            inner.OnNext(typed);
        }
        public void OnError(Exception error) => inner.OnError(error);
        public void OnCompleted() => inner.OnCompleted();
    }

    /// <summary>
    /// Deserialises <paramref name="node"/>'s Content if it arrived as a
    /// raw <see cref="JsonElement"/>. Pass-through when Content is null or
    /// already typed. Uses <see cref="JsonSerializerOptions"/>'s polymorphic
    /// <c>$type</c> discriminator to land on the concrete domain type
    /// (e.g. <c>MeshThread</c>, <c>NodeTypeDefinition</c>).
    /// <para>
    /// 🚨 Throws <see cref="MeshNodeStreamException"/> with
    /// <see cref="MeshNodeErrorCode.Deserialization"/> when deserialization
    /// fails — the diagnostic carries the (truncated) raw JSON and the
    /// discriminator value so callers can pinpoint the missing TypeRegistry
    /// entry. The previous swallow-and-return-untyped behaviour silently
    /// fed JsonElement back to subscribers, which then fell back to
    /// <c>node.Content as MyType ?? new MyType()</c> and overwrote every
    /// other field on the next stream.Update — the silent-corruption bug
    /// class behind CheckInbox / AppendUserInput / ThreadStreamingIdentity
    /// flakes. Loud failure here is the contract: subscribers get OnError
    /// with the typed exception; the GUI layout-area boundary renders a
    /// typed error card; tests can assert on <c>Error.Code</c>.
    /// </para>
    /// </summary>
    internal static MeshNode EnsureTypedContent(MeshNode node, JsonSerializerOptions jsonOptions)
    {
        if (node.Content is JsonElement je)
        {
            try
            {
                return node with { Content = je.Deserialize<object>(jsonOptions) };
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new MeshNodeStreamException(BuildDeserializationError(node, je, ex), ex);
            }
            catch (System.NotSupportedException ex)
            {
                // JsonSerializer raises NotSupportedException when the
                // polymorphic discriminator names a type that isn't
                // registered in the consumer hub's options — the recurring
                // "type 'X' is not registered in this hub's TypeRegistry"
                // footgun. Same translation: surface loudly with the raw
                // JSON snippet so the caller can see which discriminator
                // value is missing.
                throw new MeshNodeStreamException(BuildDeserializationError(node, je, ex), ex);
            }
        }
        return node;
    }

    private static MeshNodeError BuildDeserializationError(
        MeshNode node, JsonElement je, System.Exception ex)
    {
        // Truncate the raw JSON snippet to keep diagnostics readable while
        // still preserving the discriminator + first few fields — usually
        // enough to identify which TypeRegistry entry is missing.
        const int maxJsonChars = 400;
        var raw = je.GetRawText();
        var snippet = raw.Length <= maxJsonChars ? raw : raw[..maxJsonChars] + "…";
        var discriminator = je.ValueKind == JsonValueKind.Object && je.TryGetProperty("$type", out var t)
            ? t.GetString() ?? "<null>"
            : "<no $type>";
        return new MeshNodeError(
            MeshNodeErrorCode.Deserialization,
            node.Path ?? "<unknown>",
            $"Failed to deserialize MeshNode.Content (discriminator $type='{discriminator}'): {ex.Message}",
            snippet);
    }

    /// <summary>
    /// Serialises <paramref name="node"/>'s Content to a <see cref="JsonElement"/>
    /// using the workspace's <see cref="JsonSerializerOptions"/>. Pass-through
    /// when Content is null or already a JsonElement. Used on the outbound
    /// path of <see cref="Update"/> so the patch the framework computes on the
    /// wire is self-describing (the <c>$type</c> discriminator is written by
    /// the caller's TypeRegistry-aware options, which the cache hub may not
    /// have).
    /// </summary>
    internal static MeshNode EnsureSerialisedContent(MeshNode node, JsonSerializerOptions jsonOptions)
    {
        if (node.Content is null or JsonElement)
            return node;
        return node with
        {
            Content = JsonSerializer.SerializeToElement(node.Content, jsonOptions)
        };
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
    {
        // 🚨 AccessContext capture for the LAMBDA invocation. The user's
        // `update` lambda runs on whatever thread the underlying writer fires
        // it on — for UpdateRemote that's the remote stream's emission thread
        // (workspace emission scheduler, AsyncLocal NOT flowed); for UpdateOwn
        // that's the data source's action block (a dedicated worker thread,
        // also no AsyncLocal flow). Without re-stamping, the lambda sees a
        // null AccessContext.Context and any downstream framework call that
        // reads `Context ?? CircuitContext` to attribute writes (e.g. inner
        // satellite-node Updates inside the lambda, IDataChangeNotifier
        // emissions, audit logs) sees null → owner-side RLS denies → silent
        // failure (chat hangs, delegations never stamp, inboxes stay empty).
        // The diagnostic for this exact shape is
        // TypedErrorPropagationTest.AccessContext_PreservedAcrossSubscribeAndUpdateHops.
        //
        // Capture order: prefer the per-delivery Context (set by the message
        // hub before invoking handlers), fall back to CircuitContext (set by
        // long-lived test fixtures / Blazor circuits). Matches what
        // UpdateRemote already captures eagerly for the outbound
        // PatchDataRequest's WithAccessContext.
        var accessService = _workspace.Hub.ServiceProvider
            .GetService<MeshWeaver.Messaging.AccessService>();
        var capturedForLambda = accessService?.Context ?? accessService?.CircuitContext;

        // 🚨 Typed-Content wrap (read direction): the lambda sees Content
        // already deserialised to its registered domain type (e.g. MeshThread,
        // NodeTypeDefinition). Without this, lambdas that pattern-match
        // `node.Content as MyType` silently fall back to the
        // `?? new MyType()` default whenever the underlying data source
        // happens to store Content as a raw JsonElement (file-system /
        // Postgres / Cosmos all round-trip through JSON serialisation;
        // InMemory keeps typed). The default-valued fallback then overwrites
        // every other field on the next stream.Update — see
        // ThreadInput.AppendUserInput + the CheckInbox flake (test sets
        // Status=Executing, AppendUserInput's `node.Content as MeshThread ??
        // new MeshThread()` quietly resets it to Idle when Content arrives
        // as JsonElement, the SubmissionWatcher then sees Idle+pending and
        // dispatches a round the test was trying to prevent).
        //
        // No outbound serialisation: the cold pipeline downstream (UpdateOwn
        // writes typed into the data source's collection; UpdateRemote /
        // cache.Update run JsonSerializer.SerializeToNode on the typed
        // updated node before computing the patch) handles either typed or
        // JsonElement equivalently. Forcing JsonElement on the output broke
        // OWN-path equality checks (data source dedup compares by
        // reference / structural equality; serialise-deserialise breaks
        // reference and can perturb structural).
        Func<MeshNode, MeshNode> wrappedUpdate = node =>
        {
            // Re-stamp AsyncLocal so the lambda body sees the caller's
            // identity, no matter what thread invoked it. No-op when no
            // identity was set (background flows that genuinely have no
            // user — these should ImpersonateAsSystem explicitly).
            using (capturedForLambda is null || accessService is null
                ? null
                : accessService.SwitchAccessContext(capturedForLambda))
            {
                return update(EnsureTypedContent(node, _jsonOptions));
            }
        };

        return new RequireSubscribeObservable<MeshNode>(
            // 🚨 CarryAccessContext is the cross-cutting "AccessContext survives
            // Subscribe()" wrap. Capture happens here — synchronously — on the
            // caller's thread where MessageHub has already set AsyncLocal from
            // delivery.AccessContext. The captured value rides as a closure on
            // the returned cold pipeline and re-stamps AsyncLocal on every
            // emission, so a Subscribe callback that lands on a different thread
            // still observes the caller's user. See
            // AccessContextCaptureExtensions / AccessContextPropagation.md.
            //
            // Cross-hub writes route through IMeshNodeStreamCache (when one is
            // registered): the cache's shared handle is what every reader is
            // subscribed to, so the patch is observed in order. Own writes and
            // cache-less writes fall back to the direct paths.
            (IsOwn
                ? UpdateOwn(wrappedUpdate)
                : _cache is not null && _path is not null
                    ? _cache.Update(_path, wrappedUpdate)
                    : UpdateRemote(wrappedUpdate))
                .CarryAccessContext(_workspace.Hub.ServiceProvider)
                // The post-update emission also goes through the typed
                // converter — callers chaining `.Select(node => node.Content as MyType)`
                // off the Update's returned observable get the same typed
                // shape as Subscribe.
                .Select(node => EnsureTypedContent(node, _jsonOptions)),
            $"MeshNodeStreamHandle.Update(path='{_path ?? "<own>"}')",
            _workspace.Hub.ServiceProvider);
    }

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

            // 🚨 Capture AccessContext SYNCHRONOUSLY here, NOT inside the
            // deferred initialSub.Subscribe callback below. The outer
            // CarryAccessContext wrap (in Update) restores AsyncLocal on
            // every emission of the OUTER observable, but it doesn't reach
            // the inner Subscribe callback below — that callback fires when
            // the remote stream's initial state arrives, often on a different
            // thread (workspace emission scheduler) where AsyncLocal is null.
            // Without this eager capture, the inner read at PatchDataRequest
            // post time sees null Context and the patch goes out unattributed
            // → "Access denied: user 'sync/...' lacks Update permission" with
            // the hub's own address as the failing principal. Capture once
            // here (Subscribe time = caller's thread, AsyncLocal valid because
            // the outer CarryAccessContext just restored it) and close over
            // it for the deferred callback.
            var accessServiceAtEntry = _workspace.Hub.ServiceProvider
                .GetService<MeshWeaver.Messaging.AccessService>();
            var capturedContextAtEntry = accessServiceAtEntry?.Context
                ?? accessServiceAtEntry?.CircuitContext;

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
                                // 🚨 Lambda returned same instance — usually means
                                // a typed pattern match (`curr.Content is MeshThread t`)
                                // failed because Content is still JsonElement
                                // (framework didn't deserialize to the registered
                                // type). Log at Warning so the silent no-op is
                                // visible without enabling Debug — otherwise a
                                // stream.Update() that "succeeds" with no
                                // observable effect (see CancelStream test failure
                                // mode where RequestedCancellationAt stayed null).
                                diagLogger?.LogWarning(
                                    "[UpdateRemote] NO-OP hub={Hub} target={Path} contentType={ContentType} — lambda returned unchanged; check the lambda's content-type pattern match",
                                    _workspace.Hub.Address, _path,
                                    current.Content?.GetType().Name ?? "<null>");
                                observer.OnNext(current);
                                observer.OnCompleted();
                                return;
                            }

                            // Auto-stamp LastModified when the lambda left it
                            // untouched. The OWN-stream's DataChangedEvent fan-out
                            // (which propagates a patch to remote subscribers)
                            // includes LastModified in the diff, so consumers that
                            // want a content-change tick can read it directly off
                            // their MeshNode emission — no separate
                            // IDataChangeNotifier round-trip needed.
                            if (updated.LastModified == current.LastModified)
                            {
                                updated = updated with { LastModified = DateTimeOffset.UtcNow };
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
                            //
                            // Use the eagerly-captured context from the Observable.Create
                            // entry above — the AsyncLocal at THIS callback's
                            // thread is unreliable (the initialSub callback can land
                            // on the workspace emission scheduler with no context
                            // flow). The captured value reflects the caller's
                            // identity at the moment Update was invoked, which is
                            // what we want stamped on the outbound patch.
                            var capturedContext = capturedContextAtEntry;
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
                                observer.OnError(new MeshNodeStreamException(new MeshNodeError(
                                    MeshNodeErrorCode.OwnerUnreachable,
                                    _path!,
                                    "Post of PatchDataRequest returned null — owner address could not be resolved")));
                                return;
                            }

                            // 🚨 Emit OnNext OPTIMISTICALLY with the locally-
                            // computed `updated` snapshot — DO NOT block
                            // OnCompleted waiting for the owner's
                            // PatchDataResponse. The wait-for-response shape
                            // worked in Monolith (~10ms response round-trip)
                            // but broke Orleans (cross-grain routing + cold-
                            // start grain activation can exceed 30s, and any
                            // caller bridging `await ... .FirstAsync()` on a
                            // hub action block deadlocks — the response
                            // needs the same action block to dispatch).
                            //
                            // Trade-off: structured owner-side errors
                            // (AccessDenied, Validation, Deserialization)
                            // do NOT propagate on the Rx OnError stream.
                            // The patch is RFC 7396 deterministic, so the
                            // optimistic snapshot matches what the owner
                            // commits on success. Owner-side failures land
                            // in the diagnostic log channel via the fire-
                            // and-forget response sub below — observable to
                            // operators, but not on the Rx pipeline.
                            //
                            // For strict consistency (rare), callers re-read
                            // via GetMeshNodeStream(path).Take(1) after the
                            // patch — that DOES go to the owner.
                            observer.OnNext(updated);
                            observer.OnCompleted();

                            // Fire-and-forget response check. Errors logged to
                            // the diag channel; observable readers care about
                            // the optimistic OnNext only. The Subscribe IS
                            // captured in `composite` so the hub-level
                            // callback is disposed when the outer chain
                            // disposes (no leaked Observe callback).
                            var responseSub = _workspace.Hub.Observe(delivery)
                                .Timeout(TimeSpan.FromSeconds(30))
                                .Take(1)
                                .Subscribe(
                                    d =>
                                    {
                                        if (d.Message is PatchDataResponse resp && resp.NodeError is { } err)
                                        {
                                            diagLogger?.LogWarning(
                                                "[UpdateRemote] OWNER_REJECTED hub={Hub} target={Path} code={Code} msg={Msg}",
                                                _workspace.Hub.Address, _path, err.Code, err.Message);
                                        }
                                        else if (d.Message is PatchDataResponse fail && !fail.Success)
                                        {
                                            diagLogger?.LogWarning(
                                                "[UpdateRemote] OWNER_FAILED hub={Hub} target={Path} err={Err}",
                                                _workspace.Hub.Address, _path, fail.Error ?? "<unknown>");
                                        }
                                    },
                                    ex => diagLogger?.LogDebug(ex,
                                        "[UpdateRemote] response wait errored hub={Hub} target={Path}",
                                        _workspace.Hub.Address, _path));
                            composite.Add(responseSub);
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
    ///   <item><description><b>Cross-hub via <see cref="IMeshNodeStreamCache"/></b> â€” when
    ///     a cache is registered on the workspace's hub: routes reads through
    ///     <c>cache.GetStream(path)</c> and writes through <c>cache.Update(path, fn)</c>.
    ///     One shared upstream subscription process-wide; writes are observed
    ///     by every reader on the same path.</description></item>
    ///   <item><description><b>Remote (fallback)</b> â€” when no cache is registered:
    ///     subscribes to and writes through the owning per-node hub via
    ///     <c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>.</description></item>
    /// </list>
    /// Callers Subscribe (read) or call <c>.Update(update).Subscribe(...)</c> (write).
    /// If the node does not exist at <paramref name="path"/>, the per-node hub never
    /// activates and the remote subscription does not emit â€” bound reads with
    /// <c>.Take(1).Timeout(...)</c> and treat absence as "not found".
    /// </summary>
    public static MeshNodeStreamHandle GetMeshNodeStream(this IWorkspace workspace, string path)
    {
        // Own-hub path: no cache redirect (same data source; cache wouldn't help).
        // Cross-hub: prefer the cache when one is registered so we share the
        // process-wide upstream + write-coherence with every other reader/writer
        // on the same path. The cache itself MUST NOT call this — it would
        // recurse forever. The cache uses GetMeshNodeStreamBypassCache.
        var ownPath = workspace.Hub.Address.Path;
        if (string.Equals(path, ownPath, StringComparison.Ordinal)
            || string.Equals(path, workspace.Hub.Address.ToString(), StringComparison.Ordinal))
            return new MeshNodeStreamHandle(workspace);

        var cache = workspace.Hub.ServiceProvider.GetService(typeof(IMeshNodeStreamCache))
            as IMeshNodeStreamCache;
        return new MeshNodeStreamHandle(workspace, path, cache);
    }

    /// <summary>
    /// Like <see cref="GetMeshNodeStream(IWorkspace, string)"/> but bypasses the
    /// <see cref="IMeshNodeStreamCache"/>. Used by the cache itself to open its
    /// upstream subscription without recursing back into the cache.
    /// </summary>
    public static MeshNodeStreamHandle GetMeshNodeStreamBypassCache(this IWorkspace workspace, string path)
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
