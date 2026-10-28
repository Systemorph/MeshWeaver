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
        // The content-delete demand (DeletePermission.RequiredFor) asks a DELETE question.
        PermissionDemand.UpdateOrDelete => NodeOperation.Delete,
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

        return EvaluateVerdict(() => rule.HasAccess(context, userId),
            context.Node.NodeType ?? rule.NodeType, context.Node.Path, userId, logger);
    }

    /// <summary>
    /// A write PRE-FLIGHT for <paramref name="caller"/>: the raw <c>(path, permission)</c> fold, and a
    /// DEFINITIVE denial re-decided through the node's own <see cref="INodeTypeAccessRule"/> — the
    /// same two questions the owner's write gate asks, so a verb that has to decide BEFORE it causes
    /// an effect the owner never sees (bytes saved into a content collection) cannot refuse what the
    /// owner would allow, nor allow what it would refuse.
    ///
    /// <para>🚨 The identity is EXPLICIT: the caller captured at the verb's entry, never the ambient
    /// context at subscribe time, which inside a continuation is the delivering thread's.</para>
    ///
    /// <para>Every terminal is the honest one: a fold or rule that faults or produces no verdict is
    /// <see cref="PermissionCheckOutcome.Undetermined(string)"/> (<c>IsGranted = false</c>, so a
    /// consumer fails closed), a node no rule governs leaves the fold's denial standing.</para>
    /// </summary>
    /// <param name="hub">The hub whose evaluator, rule index and storage answer.</param>
    /// <param name="path">The node the write targets.</param>
    /// <param name="caller">The caller captured at the verb's entry; null is anonymous.</param>
    /// <param name="permission">The permission the write needs.</param>
    /// <param name="logger">Operator log for a faulting rule or read; optional.</param>
    /// <returns>Exactly one outcome.</returns>
    public static IObservable<PermissionCheckOutcome> CheckFor(
        IMessageHub hub,
        string path,
        AccessContext? caller,
        Permission permission,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var userId = string.IsNullOrEmpty(caller?.ObjectId) || caller.IsVirtual
            ? WellKnownUsers.Anonymous
            : caller.ObjectId;
        // 🚨 TakeDecisionOutsideGate, never a bare Take(1) (#899): on a warm cache the fold emits
        // synchronously while holding its gate, and what follows — the subject-node read, the rule,
        // and the CALLER's write chained onto this verdict — must not run inside that lock.
        // 🚨 And a TERMINAL for silence: a fold that never emits nor completes would otherwise hang
        // the verb forever. Bounded by the same PermissionEstablishmentBudget the owner's write
        // validator (RlsNodeValidator) uses, and reported as Undetermined — fail closed, never a
        // denial nobody established.
        var budget = (hub.ServiceProvider.GetService<MeshOperationOptions>() ?? new MeshOperationOptions())
            .PermissionEstablishmentBudget;
        return hub.CheckPermissionOutcome(path, userId, permission)
            .TakeDecisionOutsideGate()
            .SelectMany(outcome => outcome.IsGranted || outcome.IsUndetermined
                ? Observable.Return(outcome)
                : ReconsiderThroughRule(hub, path, caller, userId, permission, logger))
            .Timeout(budget)
            .Catch((TimeoutException ex) =>
            {
                logger?.LogWarning(ex,
                    "The {Permission} pre-flight on {Path} for {User} reached no verdict within {Budget} — "
                    + "reporting an availability failure, not a denial", permission, path, userId, budget);
                return Observable.Return(PermissionCheckOutcome.Undetermined(
                    $"the {permission} check on '{path}' reached no verdict within {budget.TotalSeconds:0.#}s"));
            });
    }

    private static IObservable<PermissionCheckOutcome> ReconsiderThroughRule(
        IMessageHub hub, string path, AccessContext? caller, string userId, Permission permission, ILogger? logger)
    {
        if (SubjectOperationFor(permission) is not { } operation)
            return Observable.Return(PermissionCheckOutcome.Denied);
        return ReadSubjectNode(hub, path)
            .SelectMany(node => node is null || Find(hub, node.NodeType, operation) is not { } rule
                ? Observable.Return(PermissionCheckOutcome.Denied)
                : Evaluate(rule, new NodeValidationContext
                {
                    Operation = operation,
                    Node = node,
                    AccessContext = caller,
                }, userId, logger))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Could not read the node at {Path} to apply its node-type access rule for {User} — "
                    + "reporting an availability failure, not a denial", path, userId);
                return Observable.Return(PermissionCheckOutcome.Undetermined(
                    $"the node at '{path}' could not be read to apply its node-type access rule "
                    + $"({ex.GetType().Name})"));
            });
    }

    /// <summary>
    /// The verdict of a POINT read of <paramref name="path"/>: <paramref name="outcome"/> unless it is
    /// a definitive DENIAL and the node's type DECLARES a point read that admits
    /// <paramref name="caller"/> (<see cref="DeclaredPointRead"/>) — then granted. A grant and an
    /// undetermined outcome pass through unchanged ("we could not check" is never laundered into a
    /// declaration question). Only seams that serve one node at its own path call this; a listing
    /// never does.
    /// </summary>
    /// <param name="hub">The hub asking.</param>
    /// <param name="path">The exact path being read.</param>
    /// <param name="caller">The caller's context.</param>
    /// <param name="outcome">The standard check's (and the type rule's) verdict.</param>
    /// <param name="request">The request's shape (<see cref="DeclaredPointRead.RequestShape"/>) — null for any shape that is not a point read, which is never admitted.</param>
    public static IObservable<PermissionCheckOutcome> ReconsiderPointRead(
        IMessageHub hub, string path, AccessContext? caller, PermissionCheckOutcome outcome, PointReadRequest? request)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.IsGranted || outcome.IsUndetermined || request is null)
            return Observable.Return(outcome);
        // Bounded by the same budget the RLS validator holds a permission check to: a stalled
        // admission answers Undetermined — fail closed, and never a denial nobody established.
        var budget = (hub.ServiceProvider.GetService<MeshOperationOptions>() ?? new MeshOperationOptions())
            .PermissionEstablishmentBudget;
        return DeclaredPointRead.Admits(hub, path, caller, request.Area)
            .TakeDecisionOutsideGate()
            .DefaultIfEmpty(false)
            .Select(admitted => admitted ? PermissionCheckOutcome.FromVerdict(true) : outcome)
            .Timeout(budget)
            .Catch((TimeoutException _) => Observable.Return(PermissionCheckOutcome.Undetermined(
                $"the declared point read of '{path}' reached no verdict within {budget.TotalSeconds:0.#}s")));
    }

    /// <summary>
    /// The non-CRUD twin of <see cref="Find"/>:the <see cref="INodeTypePermissionRule"/> that decides
    /// <paramref name="permission"/> (Comment, Thread, Execute, …) on a node of
    /// <paramref name="nodeType"/>, or <c>null</c> when none does. <c>null</c> is "no rule has an
    /// opinion" — the caller's denial stands.
    /// </summary>
    /// <param name="hub">The hub whose service provider carries the mesh's rule index.</param>
    /// <param name="nodeType">The subject node's <see cref="MeshNode.NodeType"/>.</param>
    /// <param name="permission">The single non-CRUD permission being decided.</param>
    /// <returns>The governing rule, or null.</returns>
    public static INodeTypePermissionRule? FindPermissionRule(IMessageHub hub, string? nodeType, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return hub.ServiceProvider.GetService<NodeTypeAccessRuleSet>()?.FindPermissionRule(nodeType, permission);
    }

    /// <summary>
    /// Runs a <see cref="INodeTypePermissionRule"/> for a non-CRUD permission and reports it with the
    /// same three terminals as <see cref="Evaluate"/> — a fault or an empty completion is
    /// <see cref="PermissionCheckOutcome.Undetermined(string)"/>, never a grant.
    /// </summary>
    /// <param name="rule">The governing rule.</param>
    /// <param name="node">The node the rule governs.</param>
    /// <param name="accessContext">The caller's captured context; may be null.</param>
    /// <param name="userId">The identity being decided for.</param>
    /// <param name="permission">The permission being decided.</param>
    /// <param name="logger">Operator log for a faulting rule; optional.</param>
    /// <returns>Exactly one outcome, always.</returns>
    public static IObservable<PermissionCheckOutcome> EvaluatePermission(
        INodeTypePermissionRule rule,
        MeshNode node,
        AccessContext? accessContext,
        string userId,
        Permission permission,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(node);
        return EvaluateVerdict(() => rule.HasPermission(node, accessContext, userId, permission),
            node.NodeType ?? rule.NodeType, node.Path, userId, logger);
    }

    private static IObservable<PermissionCheckOutcome> EvaluateVerdict(
        Func<IObservable<bool>> verdict, string nodeType, string? path, string userId, ILogger? logger)
    {
        return Observable.Defer(verdict)
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
                    nodeType, path, userId);
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
        // ONE implementation of "storage first, served static second" — StoredNodeReads, which the
        // boot sweeps also use to read content without activating a hub (#6623). The static
        // fallback stays LAZY there: this method is on the denial path of a hot delivery gate.
        => hub.ReadStoredNode(path);
}
