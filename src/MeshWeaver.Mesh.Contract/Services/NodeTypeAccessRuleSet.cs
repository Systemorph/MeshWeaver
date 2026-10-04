using System.Collections.Immutable;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// The mesh's registered <see cref="INodeTypeAccessRule"/>s indexed by node type — the ONE place
/// the question "does a rule govern this (node type, operation), and which one?" is answered.
///
/// <para>🚨 <b>This type exists because that question used to have TWO answers</b> (issue #2913).
/// <c>RlsNodeValidator</c> built its own dictionary and routed Read/Create/Update/Delete through the
/// matching rule, while the delete handler's pre-flight
/// (<c>MeshExtensions.CheckDeletePermissionForNode</c>) demanded <see cref="Permission.Delete"/>
/// outright and never looked at a rule. The two disagreed on every satellite:
/// <c>SatelliteAccessRule</c> maps a satellite's Delete to <see cref="Permission.Update"/> on its
/// <see cref="MeshNode.MainNode"/>, so an <c>Editor</c> (Update, no Delete) could PUBLISH a satellite
/// and then be refused when removing it — "you may turn it on but not off", the exact state a
/// revocable-consent feature exists to prevent. Both callers now resolve the rule HERE, so the two
/// paths cannot drift apart again.</para>
///
/// <para><b>Selection semantics</b> (unchanged from the dictionary this replaced): keyed by node
/// type, case-insensitive, LAST registration wins per type, and a rule applies only when its
/// <see cref="INodeTypeAccessRule.SupportedOperations"/> is empty (= all operations) or contains the
/// operation being decided. <see cref="Find"/> returning <c>null</c> means "no rule governs this" —
/// the caller falls back to its own standard permission check, which is where the closed-by-default
/// behaviour lives. It never means "allowed".</para>
///
/// <para>Registered as a mesh-scoped SINGLETON in <c>MeshBuilder</c> (never a static — see
/// Doc/Architecture/NoStaticState), for the same reason <c>RecentlyDeletedRegistry</c> is: the
/// delete handler resolves it off a hub <c>ServiceProvider</c> that chains to the mesh root, and
/// every <see cref="INodeTypeAccessRule"/> in the fleet is registered on the MESH service
/// collection (<c>builder.ConfigureServices</c>), so root resolution sees the complete set.</para>
/// </summary>
public sealed class NodeTypeAccessRuleSet
{
    private readonly ImmutableDictionary<string, INodeTypeAccessRule> rules;

    /// <summary>
    /// Indexes the mesh's registered access rules by node type.
    /// </summary>
    /// <param name="accessRules">Every <see cref="INodeTypeAccessRule"/> registered on the mesh.</param>
    public NodeTypeAccessRuleSet(IEnumerable<INodeTypeAccessRule> accessRules)
    {
        ArgumentNullException.ThrowIfNull(accessRules);
        rules = accessRules
            .GroupBy(r => r.NodeType, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The rule that governs <paramref name="operation"/> for <paramref name="nodeType"/>, or
    /// <c>null</c> when none does.
    ///
    /// <para>🚨 <c>null</c> is "no rule has an opinion", NOT "allowed". Every caller must fall back
    /// to its own standard permission check on a null — that fallback is the closed-by-default
    /// behaviour, and short-circuiting it to an allow would grant delete rights to every node type
    /// that has no rule.</para>
    /// </summary>
    /// <param name="nodeType">The node's <see cref="MeshNode.NodeType"/>; null/empty yields null.</param>
    /// <param name="operation">The operation being decided.</param>
    /// <returns>The governing rule, or null.</returns>
    public INodeTypeAccessRule? Find(string? nodeType, NodeOperation operation)
    {
        if (string.IsNullOrEmpty(nodeType) || !rules.TryGetValue(nodeType, out var rule))
            return null;

        return rule.SupportedOperations.Count == 0 || rule.SupportedOperations.Contains(operation)
            ? rule
            : null;
    }

    /// <summary>
    /// The rule that decides the NON-CRUD <paramref name="permission"/> (Comment, Thread, Execute, …)
    /// on a node of <paramref name="nodeType"/>, or <c>null</c> when none does. Same index and the same
    /// last-registration-wins selection as <see cref="Find"/>: the governing rule for the type must
    /// itself be an <see cref="INodeTypePermissionRule"/> that lists the permission.
    ///
    /// <para>🚨 Read, Create, Update and Delete — and any composite or empty value — always answer
    /// <c>null</c> here. The CRUD four are decided through <see cref="Find"/> and
    /// <see cref="INodeTypeAccessRule.HasAccess"/>; answering them twice would let one rule give two
    /// verdicts to one question depending on which seam asked.</para>
    /// </summary>
    /// <param name="nodeType">The node's <see cref="MeshNode.NodeType"/>; null/empty yields null.</param>
    /// <param name="permission">A single non-CRUD permission flag.</param>
    /// <returns>The governing rule, or null.</returns>
    public INodeTypePermissionRule? FindPermissionRule(string? nodeType, Permission permission)
    {
        if (!IsRuleDecidablePermission(permission)
            || string.IsNullOrEmpty(nodeType)
            || !rules.TryGetValue(nodeType, out var rule)
            || rule is not INodeTypePermissionRule permissionRule)
            return null;

        return permissionRule.SupportedPermissions.Contains(permission) ? permissionRule : null;
    }

    /// <summary>
    /// True for exactly one permission flag outside the CRUD four — the only shape
    /// <see cref="FindPermissionRule"/> answers.
    /// </summary>
    /// <param name="permission">The permission a seam is checking.</param>
    public static bool IsRuleDecidablePermission(Permission permission)
    {
        const Permission crud = Permission.Read | Permission.Create | Permission.Update | Permission.Delete;
        var bits = (int)permission;
        return bits != 0 && (bits & (bits - 1)) == 0 && (permission & crud) == 0;
    }
}
