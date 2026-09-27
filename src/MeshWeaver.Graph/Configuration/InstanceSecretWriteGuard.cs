using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Refuses every create, update or delete of an <see cref="InstanceSecrets.NodeType"/> node that
/// does not come from the platform itself (the system identity or a hub).
///
/// <para>🚨 Why: <see cref="InstanceSecrets"/> checks the caller's rights, the slot and the
/// encryption, and only THEN writes as system. A person holding Create or Update on the Admin
/// partition could otherwise write a secret node DIRECTLY — skipping the slot check, the global
/// administrator check and the encryption — and the catalog would consume it. With this guard the
/// verbs are the only way in. The catalog additionally ignores any value that is not
/// <c>enc:</c>-tagged, so even a node that got past this guard could not inject a plaintext key.</para>
/// </summary>
internal sealed class InstanceSecretWriteGuard : INodeValidator, IOwnerEnforcedNodeValidator
{
    private static readonly IObservable<NodeValidationResult> Pass = Observable.Return(NodeValidationResult.Valid());

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } =
        [NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var isSecret = IsSecret(context.Node) || IsSecret(context.ExistingNode);
        if (!isSecret || IsPlatformWriter(context.AccessContext))
            return Pass;
        return Observable.Return(NodeValidationResult.Unauthorized(
            "Instance secrets are written only through the portal's secret verbs (InstanceSecrets), "
            + "which check the caller's rights and encrypt the value. A direct write is refused."));
    }

    private static bool IsSecret(MeshNode? node) =>
        string.Equals(node?.NodeType, InstanceSecrets.NodeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The platform's own writers: no identity (infrastructure), a hub, or the system user.</summary>
    internal static bool IsPlatformWriter(AccessContext? accessContext) =>
        accessContext is null
        || accessContext.IsHub
        || string.Equals(accessContext.ObjectId, WellKnownUsers.System, StringComparison.Ordinal);
}
