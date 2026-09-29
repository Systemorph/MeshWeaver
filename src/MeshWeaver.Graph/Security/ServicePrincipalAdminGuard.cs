using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Keeps SERVICE principals (<see cref="ServiceIdentity"/>) out of platform administration — the
/// write-side half of "a service never holds global admin" (the read-side half is
/// <c>IsGlobalAdmin</c>, which answers <c>false</c> for a service object id whatever the grants say).
///
/// <list type="number">
/// <item>An <see cref="AccessAssignment"/> in the <b>Admin partition</b> whose subject is a service
/// object id is refused — whoever writes it. Platform administration is for people.</item>
/// <item>A caller that IS a service principal may not create, update or delete anything in the Admin
/// partition. So a grant that reached a service by another road (a group whose members include it)
/// still cannot let it write its own record, mint itself tokens, or touch any admin surface.</item>
/// </list>
///
/// <para>Pure — reads nothing, decides from the node and the caller alone — so it is not an
/// owner-only validator and runs wherever the write pipeline runs. See
/// <c>Doc/Architecture/ServiceIdentities</c>.</para>
/// </summary>
internal sealed class ServicePrincipalAdminGuard(IMessageHub hub) : INodeValidator
{
    private static readonly IObservable<NodeValidationResult> Pass =
        Observable.Return(NodeValidationResult.Valid());

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } =
        [NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        => Refusal(context) is { } reason
            ? Observable.Return(NodeValidationResult.Unauthorized(reason))
            : Pass;

    /// <summary>The refusal for <paramref name="context"/>, or <c>null</c> when the write may proceed.</summary>
    /// <param name="context">The validation context.</param>
    /// <returns>The reason, or <c>null</c>.</returns>
    internal string? Refusal(NodeValidationContext context)
    {
        var node = context.Node;
        if (!IsInAdminPartition(node.Path))
            return null;

        var caller = context.AccessContext?.ObjectId;
        if (ServiceIdentity.IsServiceObjectId(caller))
            return $"Service principal '{caller}' may not write in the Admin partition ({node.Path}): "
                   + "platform administration is for people.";

        if (context.Operation != NodeOperation.Delete
            && string.Equals(node.NodeType, AccessAssignmentNodeType.NodeType, StringComparison.OrdinalIgnoreCase)
            && ServiceSubject(node) is { } subject)
            return $"Refused: '{subject}' is a service principal and may not be granted anything in the "
                   + $"Admin partition ({node.Path}) — a service principal never holds platform administration.";

        return null;
    }

    private static bool IsInAdminPartition(string? path) =>
        !string.IsNullOrEmpty(path)
        && (string.Equals(path, AdminAppNodeType.Path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(AdminAppNodeType.Path + "/", StringComparison.OrdinalIgnoreCase));

    private string? ServiceSubject(MeshNode node)
    {
        var subject = node.ContentAs<AccessAssignment>(hub.JsonSerializerOptions)?.AccessObject;
        return ServiceIdentity.IsServiceObjectId(subject) ? subject : null;
    }
}
