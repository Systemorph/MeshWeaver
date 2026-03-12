using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

/// <summary>
/// Internal helper for routing node CRUD operations through the message bus.
/// Used by MeshService and MeshCatalog.
/// Identity is always resolved from AccessService.Context (set by the calling code
/// or via using (accessService.ImpersonateAsHub(hub)) for hub-level operations).
/// </summary>
internal sealed class HubNodePersistence(
    IMessageHub hub,
    MeshCatalog catalog)
{
    private PostOptions ConfigurePost(PostOptions o)
        => o.WithTarget(catalog.MeshAddress);

    public async Task<MeshNode> CreateNodeAsync(MeshNode node, CancellationToken ct = default)
    {
        var response = await hub.AwaitResponse(new CreateNodeRequest(node), ConfigurePost, ct);
        var r = response.Message;
        if (r.Success && r.Node != null)
            return r.Node;
        throw r.RejectionReason switch
        {
            NodeCreationRejectionReason.ValidationFailed =>
                new UnauthorizedAccessException(r.Error ?? "Access denied"),
            NodeCreationRejectionReason.NodeAlreadyExists =>
                new InvalidOperationException($"Node already exists: {node.Path}"),
            _ => new InvalidOperationException(r.Error ?? "Node creation failed")
        };
    }

    public async Task<MeshNode> UpdateNodeAsync(MeshNode node, CancellationToken ct = default)
    {
        var response = await hub.AwaitResponse(new UpdateNodeRequest(node), ConfigurePost, ct);
        var r = response.Message;
        if (r.Success && r.Node != null)
            return r.Node;
        throw r.RejectionReason switch
        {
            NodeUpdateRejectionReason.ValidationFailed =>
                new UnauthorizedAccessException(r.Error ?? "Access denied"),
            NodeUpdateRejectionReason.NodeNotFound =>
                new InvalidOperationException($"Node not found: {node.Path}"),
            _ => new InvalidOperationException(r.Error ?? "Node update failed")
        };
    }

    public async Task DeleteNodeAsync(string path, CancellationToken ct = default)
    {
        var response = await hub.AwaitResponse(new DeleteNodeRequest(path) { Recursive = true }, ConfigurePost, ct);
        var r = response.Message;
        if (r.Success)
            return;
        throw r.RejectionReason switch
        {
            NodeDeletionRejectionReason.ValidationFailed =>
                new UnauthorizedAccessException(r.Error ?? "Access denied"),
            NodeDeletionRejectionReason.NodeNotFound =>
                new InvalidOperationException($"Node not found: {path}"),
            _ => new InvalidOperationException(r.Error ?? "Node deletion failed")
        };
    }

    public Task<MeshNode> CreateTransientAsync(MeshNode node, CancellationToken ct = default)
        => catalog.CreateTransientNodeAsync(node, ct);
}
