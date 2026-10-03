using System.Reactive.Linq;
using MeshWeaver.Data.Validation;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Makes a <see cref="MeshNode"/> ENTERING a per-node hub's workspace through a
/// <c>DataChangeRequest</c> answerable to the same create-validator chain as a
/// <see cref="CreateNodeRequest"/> — the counterpart of <see cref="MeshNodeDeletionDataValidator"/>.
///
/// <para>🚨 A MeshNode new to the workspace is a create: <c>MeshNodeTypeSource</c> answers it with
/// a raw <c>Write</c> (announced as <c>Created</c>) on storage. That path was checked by
/// <see cref="RlsDataValidator"/> (Create permission) and nothing else, so every guard written as
/// an <see cref="INodeValidator"/> — the creatable-types rule, the partition write guard, the
/// content schema and discriminator checks, the service-principal and instance-secret guards —
/// was skipped by a caller who could post the change instead of the create.</para>
///
/// <para>The caller chooses which list a node goes in, and the type source writes an entry of
/// <c>Updates</c> it has never stored exactly as it writes a <c>Creations</c> entry. So an
/// update is a create too when storage does not hold the path — read authoritatively, never from
/// the query index. An update of a node storage DOES hold is left to the validators that already
/// answer for updates.</para>
///
/// <para>Runs under the request's own identity and refuses the whole change with the first
/// validator's reason. Only entities that ARE MeshNodes take part. Registered per hub, beside
/// the mesh data source.</para>
/// </summary>
internal sealed class MeshNodeCreationDataValidator(IMessageHub hub) : IDataValidator
{
    /// <inheritdoc />
    public IReadOnlyCollection<DataOperation> SupportedOperations => [DataOperation.Create, DataOperation.Update];

    /// <inheritdoc />
    public IObservable<DataValidationResult> Validate(DataValidationContext context)
    {
        if (context.Operation is not (DataOperation.Create or DataOperation.Update)
            || context.Entity.As<MeshNode>(hub.JsonSerializerOptions) is not { Path.Length: > 0 } node)
            return Observable.Return(DataValidationResult.Valid());

        // Resolved NOW, on the caller's thread, while the hub is known to be alive.
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        var isCreate = context.Operation == DataOperation.Create || storage is null
            ? Observable.Return(true)
            : storage.Read(node.Path, hub.JsonSerializerOptions)
                .Take(1)
                .DefaultIfEmpty(null)
                .Select(stored => stored is null);

        return isCreate.SelectMany(create => create
            ? hub.RunCreationValidators(node, context.AccessContext)
                .Select(refusal => refusal is not { } r
                    ? DataValidationResult.Valid()
                    : DataValidationResult.Invalid(
                        r.Refusal?.English ?? $"Creating '{node.Path}' was refused by a create validator ({r.Reason})",
                        r.Reason == NodeCreationRejectionReason.Unavailable
                            ? DataValidationRejectionReason.Unknown
                            : DataValidationRejectionReason.ValidationFailed))
            : Observable.Return(DataValidationResult.Valid()));
    }
}
