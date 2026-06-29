using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Messaging;

/// <summary>
/// Hub-level permission rules checked by the AccessControlPipeline.
/// These rules are evaluated before SecurityService — if any rule grants
/// the required permission, the SecurityService check is skipped.
/// Configured via WithPublicRead() and similar hub configuration methods.
/// </summary>
public record HubPermissionRuleSet
{
    /// <summary>
    /// The configured rules: each pairs a granted <see cref="Permission"/> with a
    /// predicate over the delivery and (optional) user id that decides whether the
    /// grant applies. Immutable — extend via <see cref="Add"/>.
    /// </summary>
    public IReadOnlyList<(Permission Permission, Func<IMessageDelivery, string?, bool> Check)> Rules { get; init; } = [];

    /// <summary>
    /// Returns a new rule set with an additional permission rule appended.
    /// </summary>
    /// <param name="permission">The permission this rule grants when its predicate matches.</param>
    /// <param name="rule">Predicate over the delivery and user id deciding whether the grant applies.</param>
    /// <returns>A new <see cref="HubPermissionRuleSet"/> including the added rule.</returns>
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

/// <summary>
/// Configuration extensions for adding hub-level permission rules to a
/// <see cref="MessageHubConfiguration"/>.
/// </summary>
public static class HubPermissionExtensions
{
    /// <summary>
    /// Adds a hub-level permission rule that the AccessControlPipeline checks
    /// before falling back to SecurityService.
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
