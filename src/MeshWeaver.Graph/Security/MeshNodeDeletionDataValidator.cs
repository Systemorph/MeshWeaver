using System.Reactive.Linq;
using MeshWeaver.Data.Validation;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Makes a <see cref="MeshNode"/> leaving a per-node hub's workspace through a
/// <c>DataChangeRequest</c> answerable to the same delete-validator chain as a
/// <see cref="DeleteNodeRequest"/>.
///
/// <para>🚨 A MeshNode in a deletion of a <c>DataChangeRequest</c> is a delete:
/// <c>MeshNodeTypeSource</c> answers it with <c>DeleteAndPublish</c> on storage. That path was
/// checked by <see cref="RlsDataValidator"/> (Delete permission) and nothing else, so every guard
/// written as an <see cref="MeshWeaver.Mesh.Services.INodeValidator"/> — the partition-root guard,
/// the in-use plan-tier guard, the keep-one-space-admin invariant, the stranded-partition teardown
/// — was skipped by a caller who could post the change instead of the delete. This validator runs
/// that chain for each such node, under the request's own identity, and refuses the whole change
/// with the first validator's reason.</para>
///
/// <para>Only entities that ARE MeshNodes take part; every other entity type in the same request
/// is left to the validators that own it. Registered per hub, beside the mesh data source.</para>
/// </summary>
internal sealed class MeshNodeDeletionDataValidator(IMessageHub hub) : IDataValidator
{
    /// <inheritdoc />
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Delete];

    /// <inheritdoc />
    public IObservable<DataValidationResult> Validate(DataValidationContext context)
    {
        if (context.Operation != DataOperation.Delete
            || context.Entity.As<MeshNode>(hub.JsonSerializerOptions) is not { Path.Length: > 0 } node)
            return Observable.Return(DataValidationResult.Valid());

        return hub.RunDeletionValidators(node, context.AccessContext)
            .Select(refusal => refusal is not { } r
                ? DataValidationResult.Valid()
                : DataValidationResult.Invalid(
                    r.ErrorMessage ?? $"Deleting '{node.Path}' was refused by a delete validator",
                    r.Reason == NodeDeletionRejectionReason.NodeNotFound
                        ? DataValidationRejectionReason.EntityNotFound
                        : DataValidationRejectionReason.ValidationFailed));
    }
}
