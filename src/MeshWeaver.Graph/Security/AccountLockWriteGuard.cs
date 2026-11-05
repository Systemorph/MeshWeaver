using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// The write-side invariants of an <see cref="AccountLock"/> record, whoever writes it — the
/// <see cref="AccountLocks.Lock"/> verb, an MCP <c>create</c>/<c>update</c>/<c>delete</c>, an import,
/// or an upsert that RETYPES a node into a lock (<see cref="INodeTypeTransitionValidator"/>):
///
/// <list type="number">
/// <item>Only a platform administrator — or System, executing a verb that already checked one —
/// creates, changes or deletes a lock record. A grant on the Admin partition short of platform
/// administration (an Editor there) does not suffice.</item>
/// <item>The record sits at <see cref="AccountLock.PathOf"/> of the user it names, and that user is a
/// person (<see cref="AccountLocks.IsLockable"/>).</item>
/// <item>A lock IN FORCE never names a platform administrator, and never its own writer.</item>
/// </list>
///
/// <para>Item 3 is ALSO true at read time, whatever wrote the record: the permission fold ignores a lock
/// on anyone who holds platform administration (<c>PermissionEvaluator.ObserveEffectiveLock</c>), so a
/// write path this validator does not see can still not lock an administrator out. Refusals are in the
/// writer's language. See <c>Doc/Architecture/AccountLockout</c>.</para>
/// </summary>
internal sealed class AccountLockWriteGuard(IMessageHub hub) : INodeValidator, INodeTypeTransitionValidator
{
    private static readonly IObservable<NodeValidationResult> Pass =
        Observable.Return(NodeValidationResult.Valid());

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } =
        [NodeOperation.Create, NodeOperation.Update, NodeOperation.Delete];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var node = context.Node;
        if (!string.Equals(node.NodeType, AccountLock.NodeTypeName, StringComparison.Ordinal))
            return Pass;

        var locale = context.AccessContext?.Locale;
        var writer = context.AccessContext?.ObjectId;
        var writerIsSystem = string.Equals(writer, WellKnownUsers.System, StringComparison.Ordinal);

        // A delete only gives access back; it still needs the authority every lock mutation needs.
        if (context.Operation == NodeOperation.Delete)
            return writerIsSystem ? Pass : RequirePlatformAdmin(writer, locale, Pass);

        var record = node.ContentAs<AccountLock>(hub.JsonSerializerOptions);
        if (record is null)
            return Refuse(LocalizationCatalog.Get("accountLock.refused.unreadable", locale, node.Path));
        if (!string.Equals(node.Namespace, AccountLock.Namespace, StringComparison.Ordinal)
            || !AccountLocks.IdComparer.Equals(node.Id, record.UserId))
            return Refuse(LocalizationCatalog.Get("accountLock.refused.misplaced", locale, node.Path, record.UserId));
        if (!AccountLocks.IsLockable(record.UserId))
            return Refuse(LocalizationCatalog.Get("accountLock.refused.notAPerson", locale, record.UserId));

        var targetChecks = !record.IsInForce
            ? Pass
            : AccountLocks.IdComparer.Equals(writer, record.UserId)
              || AccountLocks.IdComparer.Equals(record.LockedBy, record.UserId)
                ? Refuse(LocalizationCatalog.Get("accountLock.refused.self", locale))
                : hub.IsGlobalAdmin(record.UserId)
                    .TakeDecisionOutsideGate()
                    .Select(isAdmin => isAdmin
                        ? NodeValidationResult.Unauthorized(
                            LocalizationCatalog.Get("accountLock.refused.administrator", locale, record.UserId))
                        : NodeValidationResult.Valid());

        return writerIsSystem ? targetChecks : RequirePlatformAdmin(writer, locale, targetChecks);
    }

    private IObservable<NodeValidationResult> RequirePlatformAdmin(
        string? writer, string? locale, IObservable<NodeValidationResult> then)
        => string.IsNullOrEmpty(writer)
            ? Refuse(LocalizationCatalog.Get("accountLock.refused.noCaller", locale))
            : hub.IsGlobalAdmin(writer)
                .TakeDecisionOutsideGate()
                .SelectMany(isAdmin => isAdmin
                    ? then
                    : Refuse(LocalizationCatalog.Get("accountLock.refused.notAdmin", locale, writer)));

    private static IObservable<NodeValidationResult> Refuse(string reason)
        => Observable.Return(NodeValidationResult.Unauthorized(reason));
}
