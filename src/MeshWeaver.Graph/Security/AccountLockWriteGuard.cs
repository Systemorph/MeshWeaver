using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// The write-side invariants of an <see cref="AccountLock"/> record, whoever writes it — the
/// <see cref="AccountLocks.Lock"/> verb, an MCP <c>create</c>/<c>update</c>, an import:
///
/// <list type="number">
/// <item>The record sits at <see cref="AccountLock.PathOf"/> of the user it names, and that user is
/// a person (<see cref="AccountLocks.IsLockable"/>) — a record that names someone else is refused
/// rather than left inert.</item>
/// <item>A lock IN FORCE never names a platform administrator — their <c>Admin/_Access</c> grant is
/// removed by another administrator first — and never the person writing it. So no lock can leave
/// an instance without a human administrator, which would hand the next sign-in the first-user
/// bootstrap.</item>
/// </list>
///
/// <para>Who may write at all is the Admin partition's own question (only a global admin holds a
/// grant there); this guard adds what a grant cannot express. Lifting a lock (an update that sets
/// <see cref="AccountLock.UnlockedAt"/>) and deleting a record only give access back, and pass.
/// See <c>Doc/Architecture/AccountLockout</c>.</para>
/// </summary>
internal sealed class AccountLockWriteGuard(IMessageHub hub) : INodeValidator
{
    private static readonly IObservable<NodeValidationResult> Pass =
        Observable.Return(NodeValidationResult.Valid());

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } =
        [NodeOperation.Create, NodeOperation.Update];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var node = context.Node;
        if (!string.Equals(node.NodeType, AccountLock.NodeTypeName, StringComparison.Ordinal))
            return Pass;

        var record = node.ContentAs<AccountLock>(hub.JsonSerializerOptions);
        if (record is null)
            return Refuse($"'{node.Path}' is an account lock without a readable lock record.");
        if (!string.Equals(node.Namespace, AccountLock.Namespace, StringComparison.Ordinal)
            || !string.Equals(node.Id, record.UserId, StringComparison.Ordinal))
            return Refuse($"An account lock lives at {AccountLock.Namespace}/{{userId}} and names that user; "
                          + $"'{node.Path}' names '{record.UserId}'.");
        if (!AccountLocks.IsLockable(record.UserId))
            return Refuse($"'{record.UserId}' is not a person's account and cannot be locked.");
        if (!record.IsInForce)
            return Pass;

        var writer = context.AccessContext?.ObjectId;
        if (string.Equals(writer, record.UserId, StringComparison.Ordinal)
            || string.Equals(record.LockedBy, record.UserId, StringComparison.Ordinal))
            return Refuse("An account cannot be locked by its own holder.");

        return hub.IsGlobalAdmin(record.UserId)
            .TakeDecisionOutsideGate()
            .Select(isAdmin => isAdmin
                ? NodeValidationResult.Unauthorized(
                    $"'{record.UserId}' is a platform administrator. Another administrator removes their "
                    + "Admin grant first; a lock never removes an administrator.")
                : NodeValidationResult.Valid());
    }

    private static IObservable<NodeValidationResult> Refuse(string reason)
        => Observable.Return(NodeValidationResult.Unauthorized(reason));
}
