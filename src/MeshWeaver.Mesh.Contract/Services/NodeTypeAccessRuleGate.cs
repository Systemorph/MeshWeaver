using System.Reactive.Linq;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// The ONE way a caller asks "does this node type's own <see cref="INodeTypeAccessRule"/> decide
/// this operation, and what does it say?" — resolution, evaluation and the three terminals, in one
/// place so the seams that ask it cannot answer it differently.
///
/// <para>🚨 <b>Why it exists (issue #3061).</b> #2913 made
/// <c>MeshExtensions.CheckDeletePermissionForNode</c> resolve the rule through
/// <see cref="NodeTypeAccessRuleSet"/> instead of demanding <see cref="Permission.Delete"/>
/// outright — but it fixed only that ONE seam. The <c>[RequiresPermission]</c> DELIVERY gate
/// (<c>AccessControlPipeline</c>) runs FIRST, and it still yielded a raw
/// <c>(hubPath, Permission)</c> pair from
/// <c>RequiresPermissionAttribute.GetPermissionChecks</c> and folded it with no rule in sight. So
/// the two seams disagreed again, and the gate won because it is earlier: on
/// <c>memex.systemorph.com</c> a recursive delete of <c>Edu/Course</c> was refused with
/// <c>Access denied: user 'rbuergi' lacks Delete permission on
/// 'Edu/Course/_Activity/compile-…'</c> for all 72 of its <c>_Activity</c> satellites — whose
/// registered <c>SatelliteAccessRule</c> says a satellite's Delete is
/// <see cref="Permission.Update"/> on its <see cref="MeshNode.MainNode"/>, which the caller held.
/// Both seams now come through here.</para>
///
/// <para><b>The rule set is still the index</b> — this type adds no second registry. It supplies
/// the two things the two seams were each re-deriving: the (permission ⇒ operation) mapping a
/// delivery gate needs to ask the index a question at all, and the EVALUATION with all three
/// terminals represented.</para>
/// </summary>
public static class NodeTypeAccessRuleGate
{
    /// <summary>
    /// The <see cref="NodeOperation"/> a <c>[RequiresPermission(<paramref name="permission"/>)]</c>
    /// delivery gate is deciding ABOUT THE HUB'S OWN NODE, or <c>null</c> when no rule at that hub
    /// can speak for the check.
    ///
    /// <para>🚨 <b><see cref="Permission.Create"/> deliberately maps to nothing.</b> A create names
    /// a node that does not exist yet, and the gate evaluates on the PARENT's hub path — so the
    /// node whose type would be looked up here is the parent, and the parent's rule has no standing
    /// to decide a child's creation. <c>RlsNodeValidator</c> keys the Create rule off the node BEING
    /// CREATED (<c>context.Node.NodeType</c>, path-checked against the parent), which only the
    /// handler can supply. Returning null keeps the gate's coarse Create check exactly as it was and
    /// leaves the decision where it has always been.</para>
    ///
    /// <para>Everything outside the CRUD four (Comment, Thread, Execute, Export, Compile, Api, …)
    /// maps to nothing for the same reason in a different shape:
    /// <see cref="INodeTypeAccessRule.SupportedOperations"/> is expressed in
    /// <see cref="NodeOperation"/>s, so a rule cannot have an opinion about them.</para>
    /// </summary>
    /// <param name="permission">The permission the delivery gate demands.</param>
    /// <returns>The operation a rule could decide, or null.</returns>
    public static NodeOperation? SubjectOperationFor(Permission permission) => permission switch
    {
        Permission.Read => NodeOperation.Read,
        Permission.Update => NodeOperation.Update,
        Permission.Delete => NodeOperation.Delete,
        _ => null
    };

    /// <summary>
    /// The rule governing (<paramref name="nodeType"/>, <paramref name="operation"/>) on
    /// <paramref name="hub"/>'s mesh, or <c>null</c> when none does.
    ///
    /// <para>🚨 <c>null</c> is "no rule has an opinion", never "allowed" — see
    /// <see cref="NodeTypeAccessRuleSet.Find"/>. Every caller keeps its own standard check as the
    /// fallback; that fallback IS the closed-by-default behaviour.</para>
    /// </summary>
    /// <param name="hub">The hub whose service provider carries the mesh's rule index.</param>
    /// <param name="nodeType">The subject node's <see cref="MeshNode.NodeType"/>.</param>
    /// <param name="operation">The operation being decided.</param>
    /// <returns>The governing rule, or null.</returns>
    public static INodeTypeAccessRule? Find(IMessageHub hub, string? nodeType, NodeOperation operation)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return hub.ServiceProvider.GetService<NodeTypeAccessRuleSet>()?.Find(nodeType, operation);
    }

    /// <summary>
    /// Runs <paramref name="rule"/> and reports its answer as a
    /// <see cref="PermissionCheckOutcome"/> — the tri-state, because a rule has THREE terminals and
    /// only one of them is a verdict.
    ///
    /// <list type="bullet">
    ///   <item><description>emits <c>true</c>/<c>false</c> ⇒
    ///     <see cref="PermissionCheckOutcome.Granted"/> / <see cref="PermissionCheckOutcome.Denied"/>;</description></item>
    ///   <item><description>FAULTS ⇒ <see cref="PermissionCheckOutcome.Undetermined(string)"/> — a rule
    ///     reaches the same starve-able permission fold every other check does
    ///     (<c>SatelliteAccessRule</c> calls <c>hub.CheckPermission</c>), and a fault is not a
    ///     refusal;</description></item>
    ///   <item><description>COMPLETES WITHOUT EMITTING ⇒ <see cref="PermissionCheckOutcome.Undetermined(string)"/>
    ///     — the shape #2742 established: <c>CombineLatest</c> completes the instant any source
    ///     completes having produced nothing, and an empty check reads as "nothing objected"
    ///     everywhere downstream.</description></item>
    /// </list>
    ///
    /// <para>Undetermined carries <c>IsGranted = false</c>, so every consumer fails CLOSED whether
    /// or not it branches on the tri-state. There is deliberately no <c>.Catch(_ =&gt; true)</c>
    /// shape here: the identical instinct on the security-fold twin is what made a group deny fail
    /// OPEN (#2011).</para>
    ///
    /// <para>🚨 The DETAIL names the exception TYPE, never its message. This string is echoed to the
    /// caller (a <c>DeleteNodeResponse</c>, a <c>DeliveryFailure</c>), and an arbitrary rule's
    /// exception text can carry internal paths, connection strings or another tenant's identifiers.
    /// The full exception goes to <paramref name="logger"/>, where only an operator sees it.</para>
    /// </summary>
    /// <param name="rule">The governing rule.</param>
    /// <param name="context">The validation context — built identically by every caller.</param>
    /// <param name="userId">The identity being decided for.</param>
    /// <param name="logger">Operator log for a faulting rule; optional.</param>
    /// <returns>Exactly one outcome, always.</returns>
    public static IObservable<PermissionCheckOutcome> Evaluate(
        INodeTypeAccessRule rule,
        NodeValidationContext context,
        string userId,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(context);

        var nodeType = context.Node.NodeType ?? rule.NodeType;
        return Observable.Defer(() => rule.HasAccess(context, userId))
            // TakeDecisionOutsideGate, not a bare Take(1) — the rules reach the very same permission
            // fold every other check does, and the caller chains real work (a delete pipeline, the
            // downstream delivery handler) onto this verdict. See HubPermissionExtensions.
            .TakeDecisionOutsideGate()
            .Select(PermissionCheckOutcome.FromVerdict)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "The {NodeType} access rule for {Path} could not reach a verdict for {User} — "
                    + "reporting an availability failure, not a denial",
                    nodeType, context.Node.Path, userId);
                return Observable.Return(PermissionCheckOutcome.Undetermined(
                    $"the '{nodeType}' access rule failed ({ex.GetType().Name})"));
            })
            // 🚨 Built LAZILY (nullable + DefaultIfEmpty() + Select), never
            // `DefaultIfEmpty(Undetermined($"…"))` — that argument is an ordinary C# expression and
            // would format the message on EVERY evaluation to hand it to a branch that virtually
            // never fires.
            .Select(outcome => (PermissionCheckOutcome?)outcome)
            .DefaultIfEmpty()
            .Select(outcome => outcome ?? PermissionCheckOutcome.Undetermined(
                $"the '{nodeType}' access rule completed without producing a verdict"));
    }

    /// <summary>
    /// The node SERVED at <paramref name="path"/>, as this hub can see it: the storage adapter
    /// first, the static/config node provider second — the same order
    /// <c>MeshExtensions.ReadNodeAuthoritative</c> uses, and for the same reason (a partition whose
    /// root is a static node is present even though no row exists).
    ///
    /// <para>🚨 A read FAULT is propagated, never swallowed into <c>null</c>. The caller has to be
    /// able to tell "there is no node here, so no rule governs it" (a verdict) from "this hub could
    /// not find out" (an availability answer) — collapsing them would make a transient storage blip
    /// silently restore the pre-fix behaviour, which is the gate deciding its own input all over
    /// again.</para>
    /// </summary>
    /// <param name="hub">The hub whose persistence and static providers answer.</param>
    /// <param name="path">The node path to read.</param>
    /// <returns>The node, or null when nothing is served at that path.</returns>
    public static IObservable<MeshNode?> ReadSubjectNode(IMessageHub hub, string path)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return Observable.Defer(() =>
        {
            var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
            return persistence is null
                ? Observable.Return(hub.ServiceProvider.FindServedStaticNode(path))
                : persistence.Read(path, hub.JsonSerializerOptions)
                    .Take(1)
                    .DefaultIfEmpty(null)
                    // 🚨 The static fallback is resolved LAZILY — `FindServedStaticNode` enumerates
                    // every registered provider's node list, and this method is on the denial path
                    // of a hot delivery gate. Storage answers for all but the config-node case, so
                    // the enumeration must not be paid before the read that usually obviates it.
                    .Select(node => node ?? hub.ServiceProvider.FindServedStaticNode(path));
        });
    }
}
