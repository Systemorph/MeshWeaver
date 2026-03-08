using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Wrapper around IMeshNodePersistence that sends all operations
/// with the hub's own identity (ImpersonateAsHub) instead of the current user.
/// </summary>
internal sealed class ImpersonatedNodePersistence(
    IMessageHub hub,
    IMeshNodePersistence inner) : IMeshNodePersistence
{
    public Task<MeshNode> CreateNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<MeshNode>();
        if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled(ct));

        var request = new CreateNodeRequest(node) { CreatedBy = createdBy };
        var delivery = hub.Post(request, o => o.WithTarget(hub.Address).ImpersonateAsHub());

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

        var request = new UpdateNodeRequest(node) { UpdatedBy = updatedBy };
        var delivery = hub.Post(request, o => o.WithTarget(hub.Address).ImpersonateAsHub());

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

        var request = new DeleteNodeRequest(path) { DeletedBy = deletedBy, Recursive = true };
        var delivery = hub.Post(request, o => o.WithTarget(hub.Address).ImpersonateAsHub());

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
        => inner.CreateTransientAsync(node, ct);

    public IMeshNodePersistence ImpersonateAsNode() => this;
}
