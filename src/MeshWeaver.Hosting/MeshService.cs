using System.Reactive.Linq;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

/// <summary>
/// Scoped IMeshService implementation.
/// Writes go through hub messaging (Post + RegisterCallback) — no direct persistence dependency.
/// Reads go through MeshQuery (aggregated query providers).
/// Identity is captured from AccessService and stamped on each delivery.
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
    private Address? _meshAddress;
    private Address MeshAddress => _meshAddress ??= hub.GetMeshHub().Address;

    private AccessContext? CaptureContext()
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context ?? accessService?.CircuitContext;
    }

    private PostOptions ConfigurePost(PostOptions o, AccessContext? captured)
    {
        o = o.WithTarget(MeshAddress);
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
        // (atioz 2026-06-18: System compile/import writes posted as Anonymous → the Doc/_Policy
        // Create=false cap denied them → activities never landed → phantom-path NotFound storm.)
        var captured = CaptureContext();
        return Observable.Defer(() =>
        {
            var request = new CreateNodeRequest(node);
            if (string.IsNullOrEmpty(request.CreatedBy)
                && captured?.ObjectId is { Length: > 0 } callerId)
                request = request with { CreatedBy = callerId };
            return hub.Observe(request, o => ConfigurePost(o, captured))
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
            return hub.Observe(request, o => ConfigurePost(o, captured))
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

    public IObservable<MeshNode> CreateTransient(MeshNode node)
    {
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (persistence == null)
            return CreateNode(node);

        // Persistence ALLOWED here — `CreateTransient` is the canonical entry that
        // sets a transient MeshNode into the mesh. The request handler path for
        // `CreateNodeRequest` would force the node to `Active`; we deliberately want
        // a node in `Transient` state to register it without a permanent commit, so we
        // write straight through `IStorageAdapter`. This is the *only* IMeshService
        // method allowed to bypass the hub-message pipeline — every other CRUD method
        // routes through `Post + RegisterCallback` and never touches persistence.
        var transientNode = node with { State = MeshNodeState.Transient };
        return persistence.Write(transientNode, hub.JsonSerializerOptions)
            .Where(n => n is not null)
            .Select(n => n!);
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
            return hub.Observe(req, o => ConfigurePost(o, captured))
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
    {
        // 🚨 Stamp the caller's identity from THIS scope's AccessService onto
        // the request BEFORE handing to the singleton MeshQuery + providers.
        // The providers are singletons and inject the ROOT AccessService which
        // has no per-circuit context — so a Blazor circuit asking for its own
        // items would get GetEffectiveUserId -> Anonymous and every query
        // would filter to Anonymous-visible only ("items + threads empty"
        // prod symptom 2026-05-22). MeshService IS scoped, so its hub's
        // ServiceProvider does see the circuit's AccessService with the user
        // identity middleware set.
        //
        // 🚨 ONLY stamp when UserId is genuinely UNSET (null). Empty string is
        // the explicit-Anonymous marker — tests querying as anonymous user
        // pass UserId="" deliberately, and downstream GetEffectiveUserId
        // (StorageAdapterMeshQueryProvider) treats "" as Anonymous (per the
        // contract in its own xmldoc). Stamping over "" with the captured
        // admin context (the user the test class logged in via DevLogin)
        // caused MeshQuery_AnonymousUser_FiltersRestrictedNodes to see
        // admin's view instead of Anonymous's view (2026-05-22 trace).
        if (request.UserId is null)
        {
            var captured = CaptureContext();
            if (!string.IsNullOrEmpty(captured?.ObjectId))
                request = request with { UserId = captured.ObjectId };
        }
        return _query.Query<T>(request);
    }

    public IObservable<T?> Select<T>(string path, string property)
        => _query.Select<T>(path, property);

    public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request)
    {
        // Same identity-stamp guard as Query — without it scoped DI
        // consumers (Blazor circuits, MCP children) would hit the singleton
        // providers as Anonymous and filter everything out.
        if (request.UserId is null)
        {
            var captured = CaptureContext();
            if (!string.IsNullOrEmpty(captured?.ObjectId))
                request = request with { UserId = captured.ObjectId };
        }
        return _query.Query(request);
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
