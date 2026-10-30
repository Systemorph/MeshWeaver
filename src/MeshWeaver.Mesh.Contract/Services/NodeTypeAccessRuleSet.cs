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
    // Each entry is the registration's position, the registration as resolved (a module's forwarding
    // proxy, or an instance) and what the set hands out for it (Bound).
    private readonly ImmutableDictionary<string, ImmutableArray<(int Position, INodeTypeAccessRule Registered, INodeTypeAccessRule Bound)>> rules;

    // 🚨 Module rules whose node type could NOT be read when the set was built (review of #6854): their
    // lease failed then — a construction fault, a generation that could not produce the rule, a module
    // not held. The set is a mesh-lifetime singleton, so dropping them dropped the module's rule for
    // good, past a fault that has since cleared and past every later generation. They are retained, in
    // registration order, and their node type is read under the lease each lookup takes.
    private readonly ImmutableArray<(int Position, INodeTypeAccessRule Registered)> unindexed;

    /// <summary>
    /// Indexes the mesh's registered access rules by node type.
    ///
    /// <para>🚨 <b>A rule whose module is gone is not a member</b> (<see cref="ModuleServiceProxy.Serves"/>).
    /// A module registers its rule as a platform interface, so the root holds a forwarding proxy for it;
    /// a module WITHDRAWN at boot (its shared instances could not be built) leaves that proxy behind, and
    /// reading its <see cref="INodeTypeAccessRule.NodeType"/> threw <c>Module … holds no service scope</c>
    /// right here. Every write's <c>RlsNodeValidator</c> takes this set, so one withdrawn module broke
    /// every node write in the mesh — every package install included. Such a rule is skipped while it
    /// cannot be leased — when the set is built (it is retained, its node type read at lookup, so it
    /// governs again once it can be leased) and whenever a type is looked up: its
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
        var read = accessRules
            .Select((rule, position) => (Position: position, Rule: rule, NodeType: NodeTypeOf(rule)))
            .ToImmutableArray();
        rules = read
            .Where(r => r.NodeType is not null)
            .GroupBy(r => r.NodeType!, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(
                g => g.Key,
                g => g.Select(r => (r.Position, r.Rule, Bind(r.Rule, r.NodeType!))).ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
        unindexed = read
            .Where(r => r.NodeType is null && r.Rule is ModuleServiceProxy)
            .Select(r => (r.Position, r.Rule))
            .ToImmutableArray();
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

    // The node type each retained unindexed rule named the last time it could be leased, by registration
    // position — so a later FAULT of that rule fails closed for its own type only.
    private ImmutableDictionary<int, string> learned = ImmutableDictionary<int, string>.Empty;

    // LAST registration wins, among the rules that can still be leased. `decide` reads the governing
    // rule's metadata UNDER the lease that established it serves: true hands out its bound rule,
    // false means it governs without an opinion on this question (null), and a rule whose module is
    // gone (not held, or retiring) is skipped for the registration before it — the feature is absent.
    //
    // 🚨 A rule whose module is HELD but that cannot be PRODUCED — its construction failed, or its
    // generation cannot serve it — is NOT absent, and it FAILS CLOSED for the type it governs (review of
    // #6854): it is handed out as a rule that denies (`Denying`). Skipping it would fall back to the
    // ordinary path permission, so a caller holding an ordinary Update grant would pass a write the
    // module's rule exists to refuse. Other types are untouched: their lookups never meet this rule.
    //
    // A retained unindexed rule (see `unindexed`) is a candidate at its registration position: it governs
    // the type when its lease can be taken NOW and the leased rule names the type. When it faults, it
    // fails closed for the type it last named (`learned`) — and, if it has NEVER been produced in this
    // process, for every lookup, because nothing says which type it governs: denying is the safe reading
    // of a security rule nobody can read.
    private INodeTypeAccessRule? Governing(string nodeType, Func<INodeTypeAccessRule, INodeTypeAccessRule, bool> decide)
    {
        var indexed = rules.TryGetValue(nodeType, out var registered)
            ? registered
            : ImmutableArray<(int Position, INodeTypeAccessRule Registered, INodeTypeAccessRule Bound)>.Empty;
        if (indexed.IsEmpty && unindexed.IsEmpty)
            return null;
        // Both lists are in registration order; walk them together from the last registration back.
        var i = indexed.Length - 1;
        var u = unindexed.Length - 1;
        while (i >= 0 || u >= 0)
        {
            if (u < 0 || (i >= 0 && indexed[i].Position > unindexed[u].Position))
            {
                var entry = indexed[i--];
                using var lease = ModuleServiceProxy.TryLeaseOrFault(entry.Registered, out var fault);
                if (lease is null)
                {
                    if (fault == ModuleLeaseFault.None)
                        continue;
                    return Denying(entry.Registered, nodeType);
                }
                return decide(entry.Registered, lease.Service) ? entry.Bound : null;
            }
            var (position, candidate) = unindexed[u--];
            using (var lease = ModuleServiceProxy.TryLeaseOrFault(candidate, out var fault))
            {
                if (lease is null)
                {
                    if (fault == ModuleLeaseFault.None)
                        continue;
                    if (learned.TryGetValue(position, out var named) && !string.Equals(named, nodeType, StringComparison.OrdinalIgnoreCase))
                        continue;
                    return Denying(candidate, nodeType);
                }
                var leasedType = lease.Service.NodeType;
                ImmutableInterlocked.AddOrUpdate(ref learned, position, leasedType, (_, _) => leasedType);
                if (!string.Equals(leasedType, nodeType, StringComparison.OrdinalIgnoreCase))
                    continue;
                return decide(candidate, lease.Service) ? Bind(candidate, leasedType) : null;
            }
        }
        return null;
    }

    // A rule that could not be produced, handed out in its registered shape so the permission lookup
    // treats it exactly as it would treat the rule: it governs every operation and denies.
    private static INodeTypeAccessRule Denying(INodeTypeAccessRule registered, string nodeType) =>
        registered is INodeTypePermissionRule
            ? new DenyingPermissionRule(nodeType)
            : new DenyingRule(nodeType);

    private class DenyingRule(string nodeType) : INodeTypeAccessRule
    {
        public string NodeType => nodeType;

        // Empty = every operation.
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [];

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId) => Observable.Return(false);
    }

    private sealed class DenyingPermissionRule(string nodeType) : DenyingRule(nodeType), INodeTypePermissionRule
    {
        public IReadOnlyCollection<Permission> SupportedPermissions => [];

        public IObservable<bool> HasPermission(MeshNode node, AccessContext? accessContext, string? userId, Permission permission) =>
            Observable.Return(false);
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
    /// Whether the rule that governs <paramref name="operation"/> for <paramref name="nodeType"/> is a
    /// <typeparamref name="TRule"/>. The same selection as <see cref="Find"/>, but the type test is made
    /// on the rule ITSELF, read under its lease: what <see cref="Find"/> hands out for a module's rule is
    /// a lease wrapper, so a type test on that answer never sees the module's rule (review of #7655).
    ///
    /// <para>A module rule that cannot be produced governs its type as a denying rule (see
    /// <c>Governing</c>), and counts as a <typeparamref name="TRule"/> here: nobody can read what it is,
    /// so a caller that protects a class of node types fails closed on it.</para>
    /// </summary>
    /// <typeparam name="TRule">The rule type to test for.</typeparam>
    /// <param name="nodeType">The node's <see cref="MeshNode.NodeType"/>; null/empty yields false.</param>
    /// <param name="operation">The operation being decided.</param>
    /// <returns>True when the governing rule is a <typeparamref name="TRule"/>.</returns>
    public bool IsGovernedBy<TRule>(string? nodeType, NodeOperation operation) where TRule : class
        => !string.IsNullOrEmpty(nodeType)
           && Governing(nodeType, (_, rule) =>
                   rule.SupportedOperations is var operations
                   && (operations.Count == 0 || operations.Contains(operation))
                   && rule is TRule)
               is not null;

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
