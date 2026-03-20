using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Messaging.Security;

/// <summary>
/// Marks a message type with the permission required to deliver it to a hub.
/// When a hub has access control enabled, incoming messages with this attribute
/// are checked against the sender's effective permissions on the hub's path.
/// If the sender lacks the required permission, the message is rejected with a DeliveryFailure.
///
/// For complex scenarios (e.g., Move needs Delete on source + Create on target),
/// inherit from this attribute and override <see cref="GetPermissionChecks"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public class RequiresPermissionAttribute(Permission permission) : Attribute
{
    public Permission Permission { get; } = permission;

    /// <summary>
    /// Returns the permission checks required for this message.
    /// Default implementation: checks <see cref="Permission"/> on the hub's path.
    /// Override for complex scenarios that need multiple path/permission checks.
    /// </summary>
    /// <param name="delivery">The incoming message delivery</param>
    /// <param name="hubPath">The hub's address path (node namespace)</param>
    /// <returns>Pairs of (path, permission) to check; all must pass</returns>
    public virtual IEnumerable<(string Path, Permission Permission)> GetPermissionChecks(
        IMessageDelivery delivery, string hubPath)
    {
        yield return (hubPath, Permission);
    }
}
