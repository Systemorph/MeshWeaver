using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

/// <summary>
/// Scoped IMeshNodePersistence implementation — each hub gets its own instance
/// with the correct IMessageHub injected (same pattern as PersistenceService).
/// Automatically resolves the current user identity from AccessService,
/// or uses hub.Address when impersonated.
/// </summary>
internal sealed class HubNodePersistence(
    IMessageHub hub,
    MeshCatalog catalog,
    bool impersonate = false) : IMeshNodePersistence
{
    private string? CurrentIdentity => impersonate
        ? hub.Address.ToFullString()
        : hub.ServiceProvider.GetService<AccessService>()?.Context?.ObjectId;

    private PostOptions ConfigurePost(PostOptions o)
    {
        o = o.WithTarget(catalog.MeshAddress);
        return impersonate ? o.ImpersonateAsHub() : o;
    }

    public Task<MeshNode> CreateNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<MeshNode>();
        if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled(ct));

        var request = new CreateNodeRequest(node) { CreatedBy = createdBy ?? CurrentIdentity };
        var delivery = hub.Post(request, ConfigurePost);

        if (delivery == null)
        {
            tcs.SetException(new InvalidOperationException("Failed to post CreateNodeRequest"));
            return tcs.Task;
        }

        hub.RegisterCallback<CreateNodeResponse>(delivery, response =>
        {
            var r = response.Message;
            if (r.Success && r.Node != null)
                tcs.TrySetResult(r.Node);
            else
                tcs.TrySetException(r.RejectionReason switch
                {
                    NodeCreationRejectionReason.ValidationFailed =>
                        new UnauthorizedAccessException(r.Error ?? "Access denied"),
                    NodeCreationRejectionReason.NodeAlreadyExists =>
                        new InvalidOperationException($"Node already exists: {node.Path}"),
                    _ => new InvalidOperationException(r.Error ?? "Node creation failed")
                });
            return response;
        });

        return tcs.Task;
    }

    public Task<MeshNode> UpdateNodeAsync(MeshNode node, string? updatedBy = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<MeshNode>();
        if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled(ct));

        var request = new UpdateNodeRequest(node) { UpdatedBy = updatedBy ?? CurrentIdentity };
        var delivery = hub.Post(request, ConfigurePost);

        if (delivery == null)
        {
            tcs.SetException(new InvalidOperationException("Failed to post UpdateNodeRequest"));
            return tcs.Task;
        }

        hub.RegisterCallback<UpdateNodeResponse>(delivery, response =>
        {
            var r = response.Message;
            if (r.Success && r.Node != null)
                tcs.TrySetResult(r.Node);
            else
                tcs.TrySetException(r.RejectionReason switch
                {
                    NodeUpdateRejectionReason.ValidationFailed =>
                        new UnauthorizedAccessException(r.Error ?? "Access denied"),
                    NodeUpdateRejectionReason.NodeNotFound =>
                        new InvalidOperationException($"Node not found: {node.Path}"),
                    _ => new InvalidOperationException(r.Error ?? "Node update failed")
                });
            return response;
        });

        return tcs.Task;
    }

    public Task DeleteNodeAsync(string path, string? deletedBy = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource();
        if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled(ct));

        var request = new DeleteNodeRequest(path) { DeletedBy = deletedBy ?? CurrentIdentity, Recursive = true };
        var delivery = hub.Post(request, ConfigurePost);

        if (delivery == null)
        {
            tcs.SetException(new InvalidOperationException("Failed to post DeleteNodeRequest"));
            return tcs.Task;
        }

        hub.RegisterCallback<DeleteNodeResponse>(delivery, response =>
        {
            var r = response.Message;
            if (r.Success)
                tcs.TrySetResult();
            else
                tcs.TrySetException(r.RejectionReason switch
                {
                    NodeDeletionRejectionReason.ValidationFailed =>
                        new UnauthorizedAccessException(r.Error ?? "Access denied"),
                    NodeDeletionRejectionReason.NodeNotFound =>
                        new InvalidOperationException($"Node not found: {path}"),
                    _ => new InvalidOperationException(r.Error ?? "Node deletion failed")
                });
            return response;
        });

        return tcs.Task;
    }

    public Task<MeshNode> CreateTransientAsync(MeshNode node, CancellationToken ct = default)
        => catalog.CreateTransientNodeAsync(node, CurrentIdentity, ct);

    public IMeshNodePersistence ImpersonateAsNode()
        => impersonate ? this : new HubNodePersistence(hub, catalog, true);
}
