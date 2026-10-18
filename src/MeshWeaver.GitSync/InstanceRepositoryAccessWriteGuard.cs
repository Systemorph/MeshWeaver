using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.GitSync;

/// <summary>
/// Refuses every create, update or delete of an instance's own repository-access records — an
/// <see cref="InstancePackageSources.NodeType"/> or an <see cref="InstanceGitHubApp.NodeType"/>
/// node — that does not come from the platform itself (the system identity or a hub).
///
/// <para>🚨 Why: the verbs (<see cref="InstancePackageSources"/>, <see cref="InstanceGitHubApp"/>)
/// check that the CALLER is a global administrator and only then write as system. Generic node CRUD
/// does not: an Editor grant in the Admin partition carries Create and Update without making its
/// holder a global administrator, so without this guard such a person could inject a package source
/// the feed and the default install trust, or rewrite the pending state of a GitHub App flow. With it
/// the verbs are the only way in — the shape of the Instance Secrets write guard.</para>
/// </summary>
internal sealed class InstanceRepositoryAccessWriteGuard : INodeValidator, IOwnerEnforcedNodeValidator
{
    private static readonly IObservable<NodeValidationResult> Pass = Observable.Return(NodeValidationResult.Valid());

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } =
        [NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var guarded = IsGuarded(context.Node) || IsGuarded(context.ExistingNode);
        if (!guarded || IsPlatformWriter(context.AccessContext))
            return Pass;
        // In the writer's language: the refusal reaches the rejected caller.
        return Observable.Return(NodeValidationResult.Unauthorized(
            LocalizationCatalog.Get("instanceRepositoryAccess.error.directWrite", context.AccessContext?.Locale)));
    }

    /// <summary>Whether <paramref name="node"/> is one of the guarded record types. Pure.</summary>
    internal static bool IsGuarded(MeshNode? node) =>
        string.Equals(node?.NodeType, InstancePackageSources.NodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(node?.NodeType, InstanceGitHubApp.NodeType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(node?.NodeType, InstanceGitHubApp.FlowNodeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The platform's own writers: no identity (infrastructure), a hub, or the system user. Pure.</summary>
    internal static bool IsPlatformWriter(AccessContext? accessContext) =>
        accessContext is null
        || accessContext.IsHub
        || string.Equals(accessContext.ObjectId, WellKnownUsers.System, StringComparison.Ordinal);
}
