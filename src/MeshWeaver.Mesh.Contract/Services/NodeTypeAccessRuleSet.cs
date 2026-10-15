using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

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
    // Per node type, every registered rule in registration order — the governing one is the LAST that
    // can still be leased (see Find), so a rule whose module is withdrawn or retired leaves the set.
    // Each entry is the registration as resolved (a module's forwarding proxy, or an instance) and what
    // the set hands out for it (Bound).
    private readonly ImmutableDictionary<string, ImmutableArray<(INodeTypeAccessRule Registered, INodeTypeAccessRule Bound)>> rules;

    /// <summary>
    /// Indexes the mesh's registered access rules by node type.
    ///
    /// <para>🚨 <b>A rule whose module is gone is not a member</b> (<see cref="ModuleServiceProxy.Serves"/>).
    /// A module registers its rule as a platform interface, so the root holds a forwarding proxy for it;
    /// a module WITHDRAWN at boot (its shared instances could not be built) leaves that proxy behind, and
    /// reading its <see cref="INodeTypeAccessRule.NodeType"/> threw <c>Module … holds no service scope</c>
    /// right here. Every write's <c>RlsNodeValidator</c> takes this set, so one withdrawn module broke
    /// every node write in the mesh — every package install included. Such a rule is left out when the
    /// set is built, and a rule whose module is retired later is skipped when a type is looked up: its
    /// node type falls back to the caller's standard check (closed by default), as for a type no module
    /// ever governed, and the withdrawal is reported where every module that failed to install is.</para>
    ///
    /// <para>🚨 <b>Every read of a module's rule is LEASED, never checked-then-called</b> (MeshWeaver#6715).
    /// "Does its module still serve?" followed by a call through the proxy could be split by a
    /// retirement, and the call then threw <c>Module … holds no service scope</c>. The rule's metadata
    /// (<see cref="INodeTypeAccessRule.NodeType"/> here, its supported operations and permissions in
    /// <see cref="Find"/>) is read under ONE lease (<see cref="ModuleServiceProxy.TryLease{T}"/>), and the
    /// rule handed out for a module evaluates under a lease of its own that spans the evaluation
    /// (<see cref="ModuleServiceProxy.Leased{T, TResult}"/>): a retirement that starts during either is
    /// deferred to the release. A retirement that falls BETWEEN the selection and the evaluation is a
    /// named <see cref="ModuleRetiredException"/> — the decision fails closed and says why, and asking
    /// again decides without the retired rule.</para>
    /// </summary>
    /// <param name="accessRules">Every <see cref="INodeTypeAccessRule"/> registered on the mesh.</param>
    public NodeTypeAccessRuleSet(IEnumerable<INodeTypeAccessRule> accessRules)
    {
        ArgumentNullException.ThrowIfNull(accessRules);
        rules = accessRules
            .Select(rule => (Rule: rule, NodeType: NodeTypeOf(rule)))
            .Where(r => r.NodeType is not null)
            .GroupBy(r => r.NodeType!, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(
                g => g.Key,
                g => g.Select(r => (r.Rule, Bind(r.Rule, r.NodeType!))).ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    // The rule's node type, read under one lease; null when its module is no longer held.
    private static string? NodeTypeOf(INodeTypeAccessRule rule)
    {
        using var lease = ModuleServiceProxy.TryLease(rule);
        return lease?.Service.NodeType;
    }

    // What the set hands out: an ordinary rule as it is; a module's forwarding proxy as a rule whose
    // every call is leased.
    private static INodeTypeAccessRule Bind(INodeTypeAccessRule rule, string nodeType) =>
        rule is not ModuleServiceProxy
            ? rule
            : rule is INodeTypePermissionRule permissionRule
                ? new LeasedPermissionRule(permissionRule, nodeType)
                : new LeasedAccessRule(rule, nodeType);

    // LAST registration wins, among the rules that can still be leased. `decide` reads the governing
    // rule's metadata UNDER the lease that established it serves: true hands out its bound rule,
    // false means it governs without an opinion on this question (null), and a rule whose module is
    // gone is skipped for the registration before it.
    private INodeTypeAccessRule? Governing(string nodeType, Func<INodeTypeAccessRule, INodeTypeAccessRule, bool> decide)
    {
        if (!rules.TryGetValue(nodeType, out var registered))
            return null;
        for (var i = registered.Length - 1; i >= 0; i--)
        {
            using var lease = ModuleServiceProxy.TryLease(registered[i].Registered);
            if (lease is null)
                continue;
            return decide(registered[i].Registered, lease.Service) ? registered[i].Bound : null;
        }
        return null;
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
        if (string.IsNullOrEmpty(nodeType))
            return null;

        return Governing(nodeType, (_, rule) =>
            rule.SupportedOperations is var operations && (operations.Count == 0 || operations.Contains(operation)));
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
        if (!IsRuleDecidablePermission(permission) || string.IsNullOrEmpty(nodeType))
            return null;

        // The REGISTERED shape decides whether it is a permission rule (a module's proxy implements
        // exactly the interface it was registered as), the leased instance what it supports.
        return Governing(nodeType, (registered, rule) =>
                registered is INodeTypePermissionRule
                && rule is INodeTypePermissionRule leased
                && leased.SupportedPermissions.Contains(permission))
            as INodeTypePermissionRule;
    }

    /// <summary>
    /// A module's rule as the set hands it out: the node type read once when the set was built, and
    /// every other call made under a lease on the module's CURRENT generation — the metadata for the
    /// read, an evaluation for its whole subscription (MeshWeaver#6715). A module retired before the
    /// call is a named <see cref="ModuleRetiredException"/>.
    /// </summary>
    private class LeasedAccessRule(INodeTypeAccessRule forwarder, string nodeType) : INodeTypeAccessRule
    {
        protected string What => $"the access rule for node type '{nodeType}'";

        public string NodeType => nodeType;

        public IReadOnlyCollection<NodeOperation> SupportedOperations => Read(forwarder, r => r.SupportedOperations);

        // One verdict (the contract), so the lease ends with it even when the rule's stream does not.
        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId) =>
            ModuleServiceProxy.Leased(forwarder, r => r.HasAccess(context, userId).Take(1), What);

        protected TResult Read<TRule, TResult>(TRule rule, Func<TRule, TResult> read) where TRule : class
        {
            using var lease = ModuleServiceProxy.TryLease(rule) ?? throw ModuleRetiredException.For(rule, What);
            return read(lease.Service);
        }
    }

    private sealed class LeasedPermissionRule(INodeTypePermissionRule forwarder, string nodeType)
        : LeasedAccessRule(forwarder, nodeType), INodeTypePermissionRule
    {
        public IReadOnlyCollection<Permission> SupportedPermissions => Read(forwarder, r => r.SupportedPermissions);

        public IObservable<bool> HasPermission(MeshNode node, AccessContext? accessContext, string? userId, Permission permission) =>
            ModuleServiceProxy.Leased(forwarder, r => r.HasPermission(node, accessContext, userId, permission).Take(1), What);
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
