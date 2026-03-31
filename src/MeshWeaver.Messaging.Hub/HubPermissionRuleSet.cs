using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Messaging;

/// <summary>
/// Hub-level permission rules checked by the AccessControlPipeline.
/// These rules are evaluated before ISecurityService — if any rule grants
/// the required permission, the ISecurityService check is skipped.
/// Configured via WithPublicRead() and similar hub configuration methods.
/// </summary>
public record HubPermissionRuleSet
{
    public IReadOnlyList<(Permission Permission, Func<IMessageDelivery, string?, bool> Check)> Rules { get; init; } = [];

    public HubPermissionRuleSet Add(Permission permission, Func<IMessageDelivery, string?, bool> rule)
        => this with { Rules = [.. Rules, (permission, rule)] };

    /// <summary>
    /// Checks if any rule grants the given permission for the delivery/user.
    /// </summary>
    public bool HasPermission(Permission permission, IMessageDelivery delivery, string? userId)
    {
        foreach (var (rulePermission, check) in Rules)
        {
            if (rulePermission.HasFlag(permission) && check(delivery, userId))
                return true;
        }
        return false;
    }
}

public static class HubPermissionExtensions
{
    /// <summary>
    /// Adds a hub-level permission rule that the AccessControlPipeline checks
    /// before falling back to ISecurityService.
    /// </summary>
    public static MessageHubConfiguration AddHubPermissionRule(
        this MessageHubConfiguration config,
        Permission permission,
        Func<IMessageDelivery, string?, bool> rule)
    {
        var existing = config.Get<HubPermissionRuleSet>() ?? new();
        return config.Set(existing.Add(permission, rule));
    }
}
