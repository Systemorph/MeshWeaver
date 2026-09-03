using System.Text.RegularExpressions;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// What a registry will accept as an instance id — checked HERE, before the id is claimed.
///
/// <para>🚨 The rule is the registry's (<c>MeshWeaverInstanceService.IsValidInstanceId</c>), and it
/// is duplicated deliberately rather than called: the setup host composes no mesh and cannot reach
/// that service, and the alternative — discovering a malformed id from a 400 after the round trip —
/// wastes an id that may already have been claimed by the time the error is read. The two are
/// pinned against each other by <c>InstanceIdRulesMatchTheRegistryTest</c>.</para>
///
/// <para>A guid satisfies it as-is, which is why the wizard mints one: 36 lowercase characters of
/// hex and single hyphens, no leading or trailing hyphen.</para>
/// </summary>
public static partial class InstanceIdRules
{
    /// <summary>Lowercase letters, digits and single hyphens; 3–48 characters; not hyphen-terminated.</summary>
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){2,47}$")]
    private static partial Regex Pattern();

    /// <summary>Whether <paramref name="instanceId"/> is one a registry will accept.</summary>
    /// <param name="instanceId">The candidate id.</param>
    public static bool IsWellFormed(string? instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) && Pattern().IsMatch(instanceId);
}
