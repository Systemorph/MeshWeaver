using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Scoped IMeshService implementation.
/// Writes go through hub messaging (<c>hub.Observe(request, …)</c>) — no direct persistence dependency.
/// Reads go through MeshQuery (aggregated query providers).
/// Identity is captured from AccessService and stamped on each delivery.
///
/// The CRUD observables use <c>Observable.Defer(...)</c> over <c>hub.Observe(request, …)</c>,
/// composing outcomes with <c>.SelectMany</c> and surfacing handler rejection / routing
/// <see cref="DeliveryFailure"/> as <c>Observable.Throw</c>.
/// <b>Never <see cref="Observable.FromAsync(System.Func{System.Threading.Tasks.Task})"/></b>
/// (forbidden outside <c>IoPool</c>: it runs the prologue on the subscribing thread and bounds
/// nothing), and <b>never Task return types</b> on the public surface.
/// See <c>Doc/Architecture/AsynchronousCalls</c>.
///
/// Each call is bounded by <see cref="MeshOperationOptions.Timeout"/> so a lost/slow response
/// surfaces as <see cref="TimeoutException"/> within a few seconds — never a hang.
/// </summary>
internal sealed class MeshService(
    IEnumerable<IMeshQueryProvider> providers,
    IMessageHub hub)
    : IMeshService
{
    private readonly MeshQuery _query = new(providers, hub);

    /// <summary>
    /// The mesh hub address where CRUD handlers (CreateNode, UpdateNode, DeleteNode) are registered.
    /// MUST walk up to the root mesh hub via <see cref="MeshExtensions.GetMeshHub"/> — the previous
    /// `hub.Address` shortcut assumed MeshService was only ever instantiated on the mesh hub itself,
    /// but the Scoped DI registration (PersistenceExtensions.AddCoreAndWrapperServices) gives every
    /// child hub (Blazor circuit, MCP child hub, kernel hub, …) its own scoped instance with that
    /// child's `IMessageHub`. From a child hub, `hub.Address` returns the child — UpdateNodeRequest
    /// then targets the child, which has no handler → "No handler found for message type
    /// UpdateNodeRequest" (prod 2026-05-23 broke every MCP write). Walking ParentHub up to the mesh
    /// root is the documented contract — see <see cref="MeshExtensions.GetMeshHub"/>'s comment.
    ///
    /// Cached on first access: the parent chain is stable for the lifetime of this scoped service,
    /// and GetMeshHub walks ParentHub on every call, so caching avoids the walk per CRUD request.
    /// </summary>
    private Address? _nodeOperationTarget;
    private Address NodeOperationTarget => _nodeOperationTarget ??= hub.NodeOperationTarget();

    /// <summary>
    /// The hub the node-operation request is POSTED FROM (and whose action block observes the
    /// response). Normally the caller's own hub — but when that hub is the ROOT MESH HUB it is the
    /// mesh's ROUTER, and the router must be neither end of a delivery: a request posted there
    /// reaches its handler stamped <c>Sender = mesh/{id}</c>, which is exactly what the
    /// <c>ROUTER_TRAFFIC</c> detector reports. Issuing on the dedicated node-operation execution
    /// hub instead keeps request AND response entirely off the router (both ends are that hub, so
    /// the post is local and never routed at all).
    ///
    /// <para>Identity is unaffected: <see cref="ConfigurePost"/> stamps the caller's captured
    /// <c>AccessContext</c> on the delivery explicitly, and the <c>CreatedBy</c>/<c>DeletedBy</c>/
    /// <c>RequestedBy</c> fields carry it in the request itself — neither depends on which hub the
    /// post originates from.</para>
    ///
    /// <para>Cached alongside <see cref="_nodeOperationTarget"/>: the parent chain is stable for
    /// this scoped service's lifetime.</para>
    /// </summary>
    private IMessageHub? _issuingHub;
    private IMessageHub IssuingHub => _issuingHub ??= hub.NodeOperationIssuingHub();

    /// <summary>
    /// Per-call timeout ceiling. Every CRUD observable is bounded by this so a lost response
    /// (routing failure, deleted hub, stuck handler) surfaces as TimeoutException within
    /// a few seconds instead of hanging forever. Default 30s, configurable via
    /// <c>WithMeshOperationTimeout</c>.
    /// </summary>
    private TimeSpan OpTimeout =>
        (hub.ServiceProvider.GetService<MeshOperationOptions>() ?? new MeshOperationOptions()).Timeout;

    private AccessContext? CaptureContext()
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context ?? accessService?.CircuitContext;
    }

    private PostOptions ConfigurePost(PostOptions o, AccessContext? captured)
    {
        o = o.WithTarget(NodeOperationTarget);
        return captured != null ? o.WithAccessContext(captured) : o;
    }

    /// <summary>
    /// Targets the node's own hub directly (not the mesh hub). The handler runs there with
    /// the node's OWN workspace available for reactive reads via <c>GetStream&lt;MeshNode&gt;</c> —
    /// no persistence fallback, no <c>Observable.FromAsync</c> wrapping of blocking async calls.
    /// </summary>
    private PostOptions ConfigurePostToNode(PostOptions o, string path, AccessContext? captured)
    {
        o = o.WithTarget(new Address(path));
        return captured != null ? o.WithAccessContext(captured) : o;
    }

    // === Node CRUD via messaging ===

    // Public CRUD observables are wrapped in Observable.Defer so the underlying
    // hub.Observe(request, options) — which posts immediately on call — only fires
    // when the consumer subscribes. Without this, a chain like
    //   GetMeshNode(target).SelectMany(existing => existing != null
    //                                   ? Observable.Return(0)
    //                                   : nodeFactory.CreateNode(node).Select(_ => 1))
    // would post the create request the moment the observable is *constructed*,
    // racing the existence check and corrupting the conditional logic.
    public IObservable<MeshNode> CreateNode(MeshNode node)
    {
        // 🚨 Capture the caller's identity EAGERLY — at the call site, where the
        // ImpersonateAsSystem / user AsyncLocal is still correct — and pin it onto the
        // request as CreatedBy. A request FIELD survives the cross-hub post AND a Subscribe
        // that lands on an emission thread (PG/remote-stream) where the AsyncLocal is gone;
        // the ambient context does not (CaptureContext inside the Defer below reads it at
        // Subscribe, which is exactly when it's lost). The owner's RlsNodeValidator reads
        // CreatedBy first, so a System write authorises against a read-only-_Policy partition.
        // (prod 2026-06-18: System compile/import writes posted as Anonymous → the Doc/_Policy
        // Create=false cap denied them → activities never landed → phantom-path NotFound storm.)
        var captured = CaptureContext();
        return Observable.Defer(() =>
        {
            var request = new CreateNodeRequest(node);
            if (string.IsNullOrEmpty(request.CreatedBy)
                && captured?.ObjectId is { Length: > 0 } callerId)
                request = request with { CreatedBy = callerId };
            return IssuingHub.Observe(request, o => ConfigurePost(o, captured))
                .SelectMany(d =>
                {
                    var r = d.Message;
                    if (r.Success && r.Node != null)
                        return Observable.Return(r.Node);
                    return Observable.Throw<MeshNode>(r.RejectionReason switch
                    {
                        NodeCreationRejectionReason.ValidationFailed =>
                            new UnauthorizedAccessException(r.Error ?? "Access denied"),
                        NodeCreationRejectionReason.NodeAlreadyExists =>
                            new InvalidOperationException($"Node already exists: {node.Path}"),
                        _ => new InvalidOperationException(r.Error ?? "Node creation failed")
                    });
                });
        }).CarryAccessContext(hub.ServiceProvider);
    }

    public IObservable<CreateNodesResponse> CreateNodes(IReadOnlyCollection<MeshNode> nodes)
    {
        // Same eager identity capture as CreateNode — see the 🚨 note there: the request FIELD
        // survives the cross-hub post and an emission-thread Subscribe; the ambient context does not.
        var captured = CaptureContext();
        return Observable.Defer(() =>
        {
            var request = new CreateNodesRequest(nodes as ImmutableList<MeshNode> ?? nodes.ToImmutableList());
            if (string.IsNullOrEmpty(request.CreatedBy)
                && captured?.ObjectId is { Length: > 0 } callerId)
                request = request with { CreatedBy = callerId };
            return IssuingHub.Observe(request, o => ConfigurePost(o, captured))
                .SelectMany(d =>
                {
                    var r = d.Message;
                    if (r.Success)
                        return Observable.Return(r);
                    return Observable.Throw<CreateNodesResponse>(r.RejectionReason switch
                    {
                        NodeCreationRejectionReason.ValidationFailed =>
                            new UnauthorizedAccessException(r.Error ?? "Access denied"),
                        _ => new InvalidOperationException(r.Error ?? "Bulk node creation failed"),
                    });
                });
        }).CarryAccessContext(hub.ServiceProvider);
    }

    public IObservable<MeshNode> UpdateNode(MeshNode node)
        // Canonical write via the mesh-node stream (UpdateNodeRequest retired). The
        // NodeUpdatePipeline restores the deleted handler's client-side pre-checks —
        // existence (→ InvalidOperationException "Node not found") and app-integrity
        // INodeValidators (→ UnauthorizedAccessException) — then issues stream.Update.
        // RLS on the patch is enforced authoritatively by the owning hub's
        // [RequiresPermission(Update)] pipeline and surfaced by UpdateRemote as
        // UnauthorizedAccessException; the owner re-stamps auditing and persists durably
        // (the PatchDataResponse acks off the storage flush, so a subsequent read sees the
        // write). Observable.Defer keeps the write cold so it fires on Subscribe.
        => Observable.Defer(() => NodeUpdatePipeline.UpdateWithValidation(hub, node))
            .CarryAccessContext(hub.ServiceProvider);

    public IObservable<MeshNode> CreateOrUpdateNode(MeshNode node)
    {
        // Eager-capture the caller's identity (same reasoning as CreateNode): pin it onto the request as
        // RequestedBy so a System/user write authorises even after the cross-hub post + a Subscribe that
        // lands on an emission thread where the AsyncLocal is gone. The owner's CreateOrUpdate handler
        // checks Create (absent) or Update (present) dynamically — race-free, unlike a client-side
        // CreateNode/UpdateNode split.
        var captured = CaptureContext();
        return Observable.Defer(() =>
        {
            var request = new CreateOrUpdateNodeRequest(node);
            if (string.IsNullOrEmpty(request.RequestedBy)
                && captured?.ObjectId is { Length: > 0 } callerId)
                request = request with { RequestedBy = callerId };
            return IssuingHub.Observe(request, o => ConfigurePost(o, captured))
                .SelectMany(d =>
                {
                    var r = d.Message;
                    if (r.Success && r.Node != null)
                        return Observable.Return(r.Node);
                    return Observable.Throw<MeshNode>(r.RejectionReason switch
                    {
                        NodeUpsertRejectionReason.Unauthorized or NodeUpsertRejectionReason.ValidationFailed =>
                            new UnauthorizedAccessException(r.Error ?? "Access denied"),
                        _ => new InvalidOperationException(r.Error ?? "Node upsert failed")
                    });
                });
        }).CarryAccessContext(hub.ServiceProvider);
    }

    public IObservable<bool> DeleteNode(string path)
    {
        // Same eager-capture as CreateNode: pin the caller's identity as DeletedBy at the call
        // site so it survives the cross-hub post / emission-thread Subscribe (RlsNodeValidator
        // reads DeletedBy first → System deletes authorise against read-only-_Policy partitions).
        var captured = CaptureContext();
        return Observable.Defer(() =>
        {
            var request = new DeleteNodeRequest(path) { Recursive = true };
            if (string.IsNullOrEmpty(request.DeletedBy)
                && captured?.ObjectId is { Length: > 0 } callerId)
                request = request with { DeletedBy = callerId };
            return IssuingHub.Observe(request, o => ConfigurePost(o, captured))
                .SelectMany(d =>
                {
                    var r = d.Message;
                    if (r.Success)
                        return Observable.Return(true);
                    return Observable.Throw<bool>(r.RejectionReason switch
                    {
                        NodeDeletionRejectionReason.ValidationFailed =>
                            new UnauthorizedAccessException(r.Error ?? "Access denied"),
                        NodeDeletionRejectionReason.Unauthorized =>
                            new UnauthorizedAccessException(r.Error ?? "Access denied"),
                        NodeDeletionRejectionReason.NodeNotFound =>
                            new InvalidOperationException($"Node not found: {path}"),
                        _ => new InvalidOperationException(r.Error ?? "Node deletion failed")
                    });
                });
        }).CarryAccessContext(hub.ServiceProvider);
    }

    public IObservable<MeshNode> CopyNode(string sourcePath, string targetPath,
        bool includeDescendants = true, bool includeSatellites = false)
        => Observable.Defer(() =>
        {
            var captured = CaptureContext();
            var req = new CopyNodeRequest(sourcePath, targetPath)
            {
                IncludeDescendants = includeDescendants,
                IncludeSatellites = includeSatellites
            };
            return IssuingHub.Observe(req, o => ConfigurePost(o, captured))
                .SelectMany(d =>
                {
                    var r = d.Message;
                    if (r.Success && r.Node != null)
                        return Observable.Return(r.Node);
                    return Observable.Throw<MeshNode>(r.RejectionReason switch
                    {
                        NodeCopyRejectionReason.TargetAlreadyExists =>
                            new InvalidOperationException(r.Error ?? "Target already exists"),
                        NodeCopyRejectionReason.SourceNotFound =>
                            new InvalidOperationException($"Source node not found: {sourcePath}"),
                        NodeCopyRejectionReason.Unauthorized =>
                            new UnauthorizedAccessException(r.Error ?? "Access denied"),
                        _ => new InvalidOperationException(r.Error ?? "Node copy failed")
                    });
                });
        }).CarryAccessContext(hub.ServiceProvider);

    // === Query (delegated to MeshQuery — IObservable only) ===

    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request)
        => _query.Query<T>(StampViewer(request));

    public IObservable<T?> Select<T>(string path, string property)
        => _query.Select<T>(path, property);

    public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request)
        => _query.Query(StampViewer(request));

    /// <summary>
    /// 🚨 THE identity boundary for every secured read. Resolves the viewer ONCE, here, and stamps
    /// it on the request so nothing downstream has to guess.
    ///
    /// <para><b>Why here and not in the providers.</b> The five storage providers used to each
    /// resolve identity themselves, at SUBSCRIBE time, off the ROOT singleton
    /// <see cref="AccessService"/>. By then the read has typically hopped a scheduler, an
    /// <c>IIoPool</c> or a change feed, so whether the caller's ambient <c>AsyncLocal</c> was still
    /// theirs depended on Rx plumbing rather than on anything the author wrote — the same code
    /// returned the caller's rows in one context and the Anonymous view in another.
    /// <see cref="MeshService"/> is the SCOPED service: its hub's provider sees the circuit's
    /// <see cref="AccessService"/>, and this method runs synchronously on the caller's own thread,
    /// which is the last moment the ambient context is reliably theirs. See
    /// <c>Doc/Architecture/QueryIdentity</c>.</para>
    ///
    /// <para><b>Why an unresolved viewer is not silent.</b> Falling back to
    /// <see cref="WellKnownUsers.Anonymous"/> never widens anything — but for a read aimed into a
    /// named partition it returns NOTHING, and an empty result set is indistinguishable from "the
    /// record does not exist". Every HTTP, circuit, SignalR and gRPC entry point in this codebase
    /// stamps an explicit Anonymous context for a logged-out caller, so reaching this branch means
    /// the read is running somewhere no entry point established identity at all — a hub action
    /// block, an Rx continuation, a background service. That is worth a warning that names the
    /// query.</para>
    /// </summary>
    /// <param name="request">The read to stamp.</param>
    /// <returns>The request, carrying the caller's viewer when one could be resolved here.</returns>
    private MeshQueryRequest StampViewer(MeshQueryRequest request)
    {
        // 🚨 An explicitly-stamped UserId always wins — including the EMPTY string, which is the
        // "evaluate as the anonymous visitor" marker tests and public surfaces pass deliberately.
        // Stamping over it with the ambient (DevLogin admin) context made an anonymous-view
        // assertion observe the admin's view instead (2026-05-22 trace).
        var identity = QueryIdentityResolver.Resolve(request, CaptureContext()?.ObjectId);

        // 🚨 Stamp ONLY what we actually resolved. Pinning the Anonymous FALLBACK here would make
        // this boundary the last word, and it is not: a caller whose ambient context is empty at
        // CALL time can still have one at SUBSCRIBE time — the plugin installer constructs its
        // queries outside the ImpersonateAsSystem scope it subscribes them in. An unconditional
        // stamp froze those reads as Anonymous and the Store package installed 0 nodes (caught by
        // the cross-repo plugins gate, which is the only thing that compiles and RUNS the node
        // repos). Leaving UserId null preserves the provider's late resolution byte-for-byte; the
        // provider is also where the unresolved-viewer diagnostic fires, because that is where the
        // answer becomes final. See Doc/Architecture/QueryIdentity.
        return identity.IsUnresolved ? request : request with { UserId = identity.UserId };
    }

    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
        int limit = 10,
        string? contextPath = null,
        string? context = null)
        => _query.Autocomplete(basePath, prefix, mode, limit, contextPath, context);

    public IObservable<string?> GetPreRenderedHtml(string path)
        => _query
            .Query<MeshNode>(new MeshQueryRequest { Query = $"path:{path}", Limit = 1 })
            .Select(c => c.Items.FirstOrDefault()?.PreRenderedHtml);
}
