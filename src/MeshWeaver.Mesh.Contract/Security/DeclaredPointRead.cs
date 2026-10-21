using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Decides a node type's DECLARED point read (<see cref="PointReadDeclaration"/>): whether a caller
/// may read the ONE node at a path because the node's type declares it and the node names the
/// caller's audience (MeshWeaver#4306).
///
/// <para><b>Consulted only for a point read, and only after the standard check DEFINITIVELY
/// denied.</b> The seams that serve a single node at its own path ask it on a denial: the RLS node
/// validator for a read whose context says it is a point read
/// (<see cref="NodeValidationContext.IsPointRead"/>, set by the node hub's own data read), the
/// <c>[RequiresPermission]</c> delivery gate for a Read on the hub's own path — and there ONLY for
/// the allow-listed request shapes (<see cref="RequestShape"/>: the node itself, or a layout area the
/// type declares) — and the node-stream cache's read gate. An undetermined check is
/// never re-decided here. Its answer replaces the denial only when it ADMITS. Listings and queries
/// never ask it — a query's row filter leaves <see cref="NodeValidationContext.IsPointRead"/> false,
/// and the Postgres permission table knows nothing of it — so a node admitted this way is invisible
/// to every listing. It is not a grant row and it never enters the permission fold.</para>
///
/// <para><b>Live.</b> <see cref="Admits"/> reads the node from its OWNER's current state (the owner's
/// own answer first, then the node stream, as System — never a stored snapshot or a mirror the owner
/// has already moved past), follows the type's
/// declaration, withdraws at the node's expiry and follows a group audience's memberships. A seam
/// takes the first verdict; the node stream keeps following it and cuts an admitted subscription
/// when it turns false (revocation, expiry, a changed audience, a removed declaration).</para>
///
/// <para><b>Fail closed.</b> No node, a node the platform did not issue, a node outside the declared
/// namespace, a type the platform does not own, no declaration, a revoked or expired node, an
/// unmatched subject, an anonymous caller (named, or on a VIRTUAL context) on an instance that denies
/// anonymous access, an API-token caller, and every fault all answer <c>false</c>.</para>
/// </summary>
public static class DeclaredPointRead
{
    /// <summary>The <c>NodeTypeDefinition</c> property that carries the declaration.</summary>
    public const string DeclarationProperty = "pointRead";

    /// <summary>
    /// Whether <paramref name="caller"/> may point-read the node at <paramref name="path"/> through
    /// its type's declared point read — LIVE: re-emits when the node, its type's declaration, the
    /// expiry or a group audience's memberships change it. Everything is read here as System from
    /// the owners' current state, never taken from a caller-supplied context. Cold.
    /// </summary>
    /// <param name="hub">The hub asking.</param>
    /// <param name="path">The exact path being read.</param>
    /// <param name="caller">The caller's context; null or virtual is the anonymous caller.</param>
    /// <param name="area">Null for a read of the node itself; otherwise the layout area of the node to
    /// render (<c>""</c> = the default area), admitted only when the declaration lists it
    /// (<see cref="PointReadDeclaration.Areas"/>).</param>
    public static IObservable<bool> Admits(IMessageHub hub, string path, AccessContext? caller, string? area = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return Observable.Defer(() =>
        {
            var userId = SubjectOf(caller);
            // 🚨 An API token is admitted by the permission fold or not at all: the fold clamps a
            // token by its scope's live `api` policy (PermissionEvaluator), which a declaration —
            // a browser surface — must not reopen.
            // A System caller never needs a declaration (the fold grants it everything), so a System
            // identity reaching this point is an ambient context leaking into a caller's place — refused.
            if (string.IsNullOrEmpty(path) || caller?.IsApiToken == true
                || string.Equals(userId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
                || AnonymousAccess.Refuses(hub.ServiceProvider, userId, caller))
                return Observable.Return(false);
            var options = hub.JsonSerializerOptions;
            var access = hub.ServiceProvider.GetService<AccessService>();
            // A stored existence probe first, so a path with nothing stored opens no live read (a
            // point read of an absent node terminates NotFound). A stale NEGATIVE refuses — the
            // closed direction; content is never decided from this snapshot.
            return hub.ReadStoredNode(path)
                .Take(1)
                .DefaultIfEmpty(null)
                .SelectMany(stored => stored is null
                    ? Observable.Return(false)
                    : OwnerCurrent(hub, access, path)
                        .Select(node => Decide(hub, access, node, userId, caller, options, area))
                        .Switch())
                .DistinctUntilChanged()
                .Catch((Exception ex) =>
                {
                    hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(DeclaredPointRead))
                        .LogWarning(ex, "The declared point read of {Path} for {User} could not be decided — refused", path, userId);
                    return Observable.Return(false);
                });
        });
    }

    /// <summary>
    /// The live Read verdict of a POINT read of <paramref name="path"/> for <paramref name="viewer"/>,
    /// decided the way the RLS validator decides a point read: the node type's own
    /// <see cref="INodeTypeAccessRule"/> for Read when one governs, otherwise the permission fold —
    /// both evaluated AS <paramref name="viewer"/> — and, only when that is a DEFINITIVE denial, the
    /// declared point read. For a page that must follow whether its viewer may still read the node
    /// it shows. Never use it to filter a listing: a listing is not a point read.
    ///
    /// <para>🚨 <b>The standard leg runs under the viewer's context, never the ambient one</b>
    /// (MeshWeaver#4306, follow-up 3). The fold reads the caller's flags off the AMBIENT context
    /// (the API-token clamp reads <see cref="AccessContext.IsApiToken"/>), so a check composed under
    /// whatever context happened to be ambient answered a Bearer viewer as if it were a browser — and
    /// a browser viewer as if it were a token when a token was ambient.</para>
    ///
    /// <para>🚨 <b>The type's rule is consulted, as at every other point-read seam</b> (follow-up 4).
    /// A rule that DENIES outranks a grant the fold would give; a rule that GRANTS admits; a rule
    /// that faults or answers nothing is UNDETERMINED, which fails closed and is never re-decided
    /// through the declaration — exactly as the RLS validator treats it.</para>
    ///
    /// <para>🚨 <b>Live in all of its inputs</b> (review of #7013). The standard leg is
    /// <see cref="ObserveStandardRead"/> — see there for what it follows.</para>
    /// </summary>
    /// <param name="hub">The hub asking.</param>
    /// <param name="path">The exact path being read.</param>
    /// <param name="viewer">The viewer's context as the render turn resolved it — its virtual and API-token flags are kept; null is the anonymous viewer.</param>
    public static IObservable<bool> ObservePointRead(IMessageHub hub, string path, AccessContext? viewer)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return Observable.Defer(() =>
        {
            var userId = SubjectOf(viewer);
            // The instance-level anonymous switch outranks every rule, as at the RLS validator.
            if (string.IsNullOrEmpty(path) || AnonymousAccess.Refuses(hub.ServiceProvider, userId, viewer))
                return Observable.Return(false);
            return ObserveStandardRead(hub, path, viewer)
                .Select(outcome => outcome.IsGranted
                    ? Observable.Return(true)
                    // "We could not check" is never laundered into a declaration question.
                    : outcome.IsUndetermined
                        ? Observable.Return(false)
                        : Admits(hub, path, viewer))
                .Switch()
                .DistinctUntilChanged()
                .Catch((Exception ex) =>
                {
                    Logger(hub)?.LogWarning(ex, "The point-read verdict of {Path} for {User} could not be decided — refused", path, userId);
                    return Observable.Return(false);
                });
        });
    }

    /// <summary>
    /// 🚨 <b>THE live standard-read path</b> of a point read of <paramref name="path"/> for
    /// <paramref name="viewer"/>: the type's <see cref="INodeTypeAccessRule"/> for Read when one
    /// governs, otherwise the permission fold — re-decided whenever an input of that decision
    /// changes, for the whole subscription. Use it wherever a verdict must FOLLOW the read (an open
    /// page, an admitted subscription), never to filter a listing.
    ///
    /// <list type="bullet">
    ///   <item><description><b>The node is the OWNER's current revision</b>, never a stored snapshot:
    ///     storage is only an existence probe (a point read of an absent node opens no live read), then
    ///     every revision the owner acknowledges re-selects the rule and re-supplies its
    ///     <see cref="NodeValidationContext.Node"/> — a repointed <see cref="MeshNode.MainNode"/> or a
    ///     changed <see cref="MeshNode.NodeType"/> decides the very next verdict. A node the owner no
    ///     longer has is <see cref="PermissionCheckOutcome.Denied"/>.</description></item>
    ///   <item><description><b>A rule is re-ASKED, because its contract is one verdict</b>
    ///     (<c>NodeTypeAccessRuleSet</c>'s lease takes the first; <c>SpaceAccessRule</c> does too). It is
    ///     asked again on every revision, whenever the viewer's live permission fold on the node or its
    ///     <see cref="MeshNode.MainNode"/> re-emits, and whenever an access input commits anywhere in
    ///     the mesh (<see cref="IMeshInvalidationFeed"/>: an <c>AccessAssignment</c>,
    ///     <c>GroupMembership</c>, <c>PartitionAccessPolicy</c> or <c>Role</c>) — so a revoked grant
    ///     withdraws a rule's earlier grant on the SAME subscription. No timer, no poll.</description></item>
    ///   <item><description><b>Every evaluation runs AS <paramref name="viewer"/></b>, each one
    ///     re-established under <see cref="ImpersonationScopeExtensions.RunAs{T}(AccessService?, AccessContext?, Func{IObservable{T}})"/>
    ///     at the moment it subscribes — a re-evaluation triggered from another thread never inherits
    ///     that thread's context.</description></item>
    ///   <item><description><b>Fail closed.</b> A rule or fold that faults or completes without a
    ///     verdict is <see cref="PermissionCheckOutcome.Undetermined(string)"/>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="hub">The hub asking.</param>
    /// <param name="path">The exact path being read.</param>
    /// <param name="viewer">The viewer's context, flags and all; null is the anonymous viewer.</param>
    public static IObservable<PermissionCheckOutcome> ObserveStandardRead(IMessageHub hub, string path, AccessContext? viewer)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return Observable.Defer(() =>
        {
            if (string.IsNullOrEmpty(path))
                return Observable.Return(PermissionCheckOutcome.Denied);
            var userId = SubjectOf(viewer);
            var identity = viewer ?? new AccessContext { ObjectId = WellKnownUsers.Anonymous, Name = WellKnownUsers.Anonymous };
            var access = hub.ServiceProvider.GetService<AccessService>();
            var logger = Logger(hub);
            return hub.ReadStoredNode(path)
                .Take(1)
                .DefaultIfEmpty(null)
                .SelectMany(stored => stored is null
                    ? Observable.Return(PermissionCheckOutcome.Denied)
                    // Off the mirror's gate, in order, before a revision subscribes a rule or a fold.
                    : OffTheGate(OwnerCurrent(hub, access, path)
                            .DistinctUntilChanged(node => node?.Version ?? long.MinValue))
                        .Select(node => node is null
                            ? Observable.Return(PermissionCheckOutcome.Denied)
                            : StandardRead(hub, access, path, node, identity, userId, logger))
                        .Switch())
                .DistinctUntilChanged()
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "The Read check of {Path} for {User} could not reach a verdict — undetermined, refused", path, userId);
                    return Observable.Return(PermissionCheckOutcome.Undetermined($"the Read check on '{path}' failed ({ex.GetType().Name})"));
                });
        });
    }

    /// <summary>The standard leg for ONE owner revision: the governing rule, re-asked live, or the live fold.</summary>
    private static IObservable<PermissionCheckOutcome> StandardRead(
        IMessageHub hub, AccessService? access, string path, MeshNode node, AccessContext viewer, string userId, ILogger? logger)
    {
        if (NodeTypeAccessRuleGate.Find(hub, node.NodeType, NodeOperation.Read) is not { } rule)
            return Classified(access.RunAs(viewer, () => hub.CheckPermission(path, userId, Permission.Read)), path, userId, logger);
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Read,
            Node = node,
            AccessContext = viewer,
            IsPointRead = true,
        };
        // 🚨 Each evaluation is ONE verdict, bounded by the same budget the RLS validator holds a
        // check to: a re-evaluation that never answers times out INTO Classified and is undetermined
        // — refused — instead of leaving the previous grant in force. The idle live subscription
        // itself is never timed out; only each evaluation is.
        var budget = (hub.ServiceProvider.GetService<MeshOperationOptions>() ?? new MeshOperationOptions())
            .PermissionEstablishmentBudget;
        return OffTheGate(RuleInputsChanged(hub, access, node, viewer, userId)
                .StartWith(System.Reactive.Unit.Default))
            .Select(_ => Classified(
                access.RunAs(viewer, () => Observable.Defer(() => rule.HasAccess(context, userId)).Take(1).Timeout(budget)),
                path, userId, logger))
            .Switch();
    }

    /// <summary>
    /// Every input a Read rule's verdict can rest on, as a signal: the viewer's live permission fold
    /// on the node and on its <see cref="MeshNode.MainNode"/> (each subscribed AS the viewer), and every
    /// access-input commit the mesh's invalidation feed reports that can reach THIS node
    /// (<see cref="AffectsRead"/>). A fold that faults faults this signal, which the caller reports as
    /// undetermined — never a silent stop.
    ///
    /// <para>🚨 A fold's INITIAL value is a signal too, never skipped: a revocation committed while
    /// these watches were being established is already in that first value, and no commit will
    /// arrive for it later.</para>
    /// </summary>
    private static IObservable<System.Reactive.Unit> RuleInputsChanged(
        IMessageHub hub, AccessService? access, MeshNode node, AccessContext viewer, string userId)
    {
        var folds = new[] { node.Path, node.MainNode }
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => access.RunAs(viewer, () => hub.GetEffectivePermissions(p!, userId))
                .DistinctUntilChanged()
                .Select(_ => System.Reactive.Unit.Default));
        var scopes = ReadScopes(node);
        var feed = hub.ServiceProvider.GetService<IMeshInvalidationFeed>();
        var commits = feed is null
            ? Observable.Never<System.Reactive.Unit>()
            : Observable.Create<System.Reactive.Unit>(observer => feed.Subscribe(change =>
            {
                if (AffectsRead(change, scopes))
                    observer.OnNext(System.Reactive.Unit.Default);
            }));
        return folds.Append(commits).Merge();
    }

    /// <summary>
    /// Re-emits each notification of <paramref name="source"/> as its OWN pool work item, in order —
    /// off every source gate (the permission fold's <c>CombineLatest</c>, the node-stream mirror, the
    /// invalidation feed's delivery) before the caller re-evaluates, so a re-evaluation that enters
    /// another fold never runs inside the gate that signalled it (the #899 lock-order hazard
    /// <c>HubPermissionExtensions.TakeDecisionOutsideGate</c> documents). Per notification, never a
    /// long-lived <c>ObserveOn</c>: each notification is an <c>Observable.Return(value, Scheduler.Default)</c>
    /// that completes after one pool work item (the <c>HopOffGate</c> shape in
    /// <c>HubPermissionExtensions</c>), so no scheduled loop — and no thread — is held per open
    /// subscription. The identity is NOT carried across the hop — the
    /// caller re-establishes it (<c>RunAs(viewer)</c>) for each evaluation.
    /// </summary>
    internal static IObservable<T> OffTheGate<T>(IObservable<T> source)
        => source.Select(value => Observable.Return(value, ReEvaluationScheduler)).Concat();

    /// <summary>The scheduler <see cref="OffTheGate{T}"/> hops through — a pool work item per notification.</summary>
    internal static System.Reactive.Concurrency.IScheduler ReEvaluationScheduler => System.Reactive.Concurrency.Scheduler.Default;

    /// <summary>
    /// The scopes whose access inputs can change a Read decision about <paramref name="node"/>: every
    /// ancestor-or-self of its path and of its <see cref="MeshNode.MainNode"/>, the root, and the
    /// <c>Admin</c> partition (where the platform-admin predicate lives).
    /// </summary>
    private static System.Collections.Immutable.ImmutableHashSet<string> ReadScopes(MeshNode node)
    {
        var scopes = System.Collections.Immutable.ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        scopes.Add("");
        scopes.Add(PermissionEvaluator.AdminScope);
        foreach (var path in new[] { node.Path, node.MainNode })
        {
            var segments = (path ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i <= segments.Length; i++)
                scopes.Add(string.Join('/', segments.Take(i)));
        }
        return scopes.ToImmutable();
    }

    /// <summary>
    /// Whether a committed change can move a Read decision about a node whose scopes are
    /// <paramref name="scopes"/>: a grant (<c>{scope}/_Access/…</c>), a partition policy
    /// (<c>{scope}/_Policy</c>) or a sync config (<c>{scope}/_GitSync</c> — a one-way sync is what
    /// admits a platform admin to a system-owned Space) ON one of those scopes, matched by namespace
    /// and id so a deletion that carries no node type still counts; and any group membership or role
    /// definition, which can widen or narrow a principal anywhere.
    /// </summary>
    private static bool AffectsRead(MeshChangeEvent change, System.Collections.Immutable.ImmutableHashSet<string> scopes)
    {
        if (string.Equals(change.NodeType, SecurityQueries.GroupMembershipNodeType, StringComparison.Ordinal)
            || string.Equals(change.NodeType, SecurityQueries.RoleNodeType, StringComparison.Ordinal))
            return true;
        var ns = (change.Namespace ?? "").Trim('/');
        const string accessSegment = "_Access";
        if (string.Equals(change.NodeType, AccessAssignmentNodeType, StringComparison.Ordinal)
            || string.Equals(ns, accessSegment, StringComparison.OrdinalIgnoreCase)
            || ns.EndsWith("/" + accessSegment, StringComparison.OrdinalIgnoreCase))
        {
            var scope = string.Equals(ns, accessSegment, StringComparison.OrdinalIgnoreCase)
                ? ""
                : ns.EndsWith("/" + accessSegment, StringComparison.OrdinalIgnoreCase)
                    ? ns[..^(accessSegment.Length + 1)]
                    : ns;
            return scopes.Contains(scope);
        }
        if (string.Equals(change.NodeType, PartitionAccessPolicyNodeType, StringComparison.Ordinal)
            || string.Equals(change.Id, PartitionPolicyId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(change.Id, AccessAssignmentGuard.SyncConfigId, StringComparison.OrdinalIgnoreCase))
            return scopes.Contains(ns);
        return false;
    }

    private const string AccessAssignmentNodeType = "AccessAssignment";
    private const string PartitionAccessPolicyNodeType = "PartitionAccessPolicy";
    private const string PartitionPolicyId = "_Policy";

    /// <summary>A verdict stream with all three terminals represented: a fault or an empty completion is undetermined.</summary>
    private static IObservable<PermissionCheckOutcome> Classified(IObservable<bool> verdicts, string path, string userId, ILogger? logger)
        => verdicts
            .Select(PermissionCheckOutcome.FromVerdict)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "The Read check of {Path} for {User} could not reach a verdict — undetermined, refused", path, userId);
                return Observable.Return(PermissionCheckOutcome.Undetermined($"the Read check on '{path}' failed ({ex.GetType().Name})"));
            })
            .Select(outcome => (PermissionCheckOutcome?)outcome)
            .DefaultIfEmpty()
            .Select(outcome => outcome ?? PermissionCheckOutcome.Undetermined($"the Read check on '{path}' completed without a verdict"));

    private static ILogger? Logger(IMessageHub hub)
        => hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(DeclaredPointRead));

    /// <summary>
    /// What <paramref name="declaration"/> admits on a node whose content is <paramref name="content"/>,
    /// at <paramref name="now"/>: the subject and the instant the admission ends; null when it admits
    /// nobody (no subject, revoked, no expiry where one is declared, or already expired). Pure.
    /// </summary>
    /// <param name="declaration">The type's declaration.</param>
    /// <param name="content">The node's content as JSON.</param>
    /// <param name="now">The instant to judge the expiry at.</param>
    public static PointReadAdmission? Admission(PointReadDeclaration declaration, JsonElement? content, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (content is not { ValueKind: JsonValueKind.Object } body || string.IsNullOrWhiteSpace(declaration.SubjectProperty))
            return null;
        if (!string.IsNullOrWhiteSpace(declaration.RevokedProperty)
            && body.TryGetProperty(declaration.RevokedProperty!, out var revoked)
            && revoked.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return null;
        DateTimeOffset? until = null;
        if (!string.IsNullOrWhiteSpace(declaration.ExpiresProperty))
        {
            if (!(body.TryGetProperty(declaration.ExpiresProperty!, out var expires)
                  && expires.ValueKind == JsonValueKind.String
                  && DateTimeOffset.TryParse(expires.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                      System.Globalization.DateTimeStyles.None, out var at)
                  && now < at))
                return null;
            until = at;
        }
        return body.TryGetProperty(declaration.SubjectProperty, out var subject)
               && subject.ValueKind == JsonValueKind.String
               && subject.GetString()?.Trim() is { Length: > 0 } named
            ? new PointReadAdmission(named, until)
            : null;
    }

    /// <summary>
    /// Whether <paramref name="node"/> sits where <paramref name="declaration"/> honours a point read:
    /// a DIRECT child of the declared namespace. A declaration that names none admits nobody. Pure.
    /// </summary>
    /// <param name="declaration">The type's declaration.</param>
    /// <param name="node">The node being read.</param>
    public static bool InDeclaredNamespace(PointReadDeclaration declaration, MeshNode node)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(node);
        var declared = declaration.Namespace?.Trim().Trim('/');
        return !string.IsNullOrEmpty(declared)
               && string.Equals((node.Namespace ?? "").Trim().Trim('/'), declared, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ONE shape of request a declared point read may admit at a node's own hub, or null for
    /// every other shape — an ALLOW-list, never a deny-list (the read's reference comes from the data
    /// layer, <see cref="ReadRequests.ReferenceOf"/>):
    /// <list type="bullet">
    ///   <item><description>a data read or subscription for the node
    ///     itself — a <see cref="MeshNodeReference"/> naming no path or exactly <paramref name="hubPath"/>
    ///     — answered as a read of the node (<see cref="PointReadRequest.Area"/> null);</description></item>
    ///   <item><description>a data read or subscription for a
    ///     LAYOUT AREA of the node — answered with that area's name (<c>""</c> for the default area),
    ///     which the declaration must list.</description></item>
    /// </list>
    /// A collection, an entity or pointer reference, a query, a stream of another path and every
    /// other message is NOT a point read: the hub's workspace holds more than the node (its
    /// satellites), so admitting such a request would serve rows the declaration never named. Pure.
    /// </summary>
    /// <param name="message">The delivered message.</param>
    /// <param name="hubPath">The path of the hub it was delivered to.</param>
    public static PointReadRequest? RequestShape(object? message, string hubPath)
    {
        return ReadRequests.ReferenceOf(message) switch
        {
            MeshNodeReference node when string.IsNullOrEmpty(node.Path)
                                        || string.Equals(node.Path.Trim().Trim('/'), hubPath.Trim().Trim('/'), StringComparison.OrdinalIgnoreCase)
                => new PointReadRequest(Area: null),
            LayoutAreaReference layout => new PointReadRequest(Area: layout.Area ?? ""),
            _ => null,
        };
    }

    /// <summary>Whether <paramref name="declaration"/> lists <paramref name="area"/> among the areas a point read may render (<c>""</c> = the default area). Pure.</summary>
    /// <param name="declaration">The type's declaration.</param>
    /// <param name="area">The requested area.</param>
    public static bool DeclaresArea(PointReadDeclaration declaration, string area)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var requested = (area ?? "").Trim();
        return declaration.Areas.Any(a => string.Equals((a ?? "").Trim(), requested, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the node's last write was made by the platform's System identity — the only issuer a declared point read admits. Pure.</summary>
    /// <param name="node">The node.</param>
    public static bool IsPlatformIssued(MeshNode node)
        => string.Equals(node.LastModifiedBy?.Trim(), WellKnownUsers.System, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="caller"/> may read THIS revision of a node through its type's declared
    /// point read — decided on the revision's own content (issuance, revocation, expiry, audience),
    /// then live for its expiry, its type's declaration and a group audience's memberships
    /// (MeshWeaver#4306, follow-up 1). A seam that forwards a node's revisions to a declaration-only
    /// caller asks this for EACH revision before forwarding it, so the revision that revokes or
    /// narrows the read is never delivered; it combines it with <see cref="Admits"/>, which decides
    /// from the owner's current state. Fails closed exactly as <see cref="Admits"/> does. Cold.
    /// </summary>
    /// <param name="hub">The hub asking.</param>
    /// <param name="revision">The revision about to be served.</param>
    /// <param name="caller">The caller's context; null or virtual is the anonymous caller.</param>
    /// <param name="area">Null for a read of the node itself; otherwise the layout area to render (<c>""</c> = the default area).</param>
    public static IObservable<bool> AdmitsRevision(IMessageHub hub, MeshNode? revision, AccessContext? caller, string? area = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return Observable.Defer(() =>
        {
            var userId = SubjectOf(caller);
            if (revision is null || string.IsNullOrEmpty(revision.Path) || RefusedOutright(hub, userId, caller))
                return Observable.Return(false);
            return Decide(hub, hub.ServiceProvider.GetService<AccessService>(), revision, userId, caller, hub.JsonSerializerOptions, area)
                .DistinctUntilChanged()
                .Catch((Exception ex) =>
                {
                    hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(DeclaredPointRead))
                        .LogWarning(ex, "The declared point read of revision {Version} of {Path} for {User} could not be decided — refused",
                            revision.Version, revision.Path, userId);
                    return Observable.Return(false);
                });
        });
    }

    /// <summary>
    /// The callers a declaration never admits, whatever the node says: an API token (admitted by the
    /// permission fold or not at all), a System identity in a caller's place, and a logged-out caller
    /// on an instance that denies anonymous access. The same refusals <see cref="Admits"/> makes.
    /// </summary>
    private static bool RefusedOutright(IMessageHub hub, string userId, AccessContext? caller)
        => caller?.IsApiToken == true
           || string.Equals(userId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
           || AnonymousAccess.Refuses(hub.ServiceProvider, userId, caller);

    /// <summary>The verdict for ONE revision of the node: issuance, declaration, namespace, content, then the audience and the expiry, live.</summary>
    private static IObservable<bool> Decide(
        IMessageHub hub, AccessService? access, MeshNode? node, string userId, AccessContext? caller, JsonSerializerOptions options,
        string? area)
    {
        if (node is null || !IsPlatformIssued(node) || string.IsNullOrEmpty(node.NodeType))
            return Observable.Return(false);
        return DeclarationOf(hub, access, node.NodeType!)
            .Select(declaration =>
                declaration is not null
                && InDeclaredNamespace(declaration, node)
                && (area is null || DeclaresArea(declaration, area))
                && Admission(declaration, ContentOf(node, options), DateTimeOffset.UtcNow) is { } admission
                    ? SubjectAdmits(hub, admission.Subject, userId, caller)
                        .CombineLatest(Open(admission.Until), (subject, open) => subject && open)
                    : Observable.Return(false))
            .Switch();
    }

    /// <summary><c>true</c> until <paramref name="until"/>, then <c>false</c>; always <c>true</c> when nothing ends it.</summary>
    private static IObservable<bool> Open(DateTimeOffset? until)
        => until is { } at
            ? Observable.Timer(at).Select(_ => false).StartWith(true)
            : Observable.Return(true);

    /// <summary>
    /// Whether <paramref name="subject"/> names <paramref name="userId"/>: <see cref="WellKnownUsers.Anonymous"/>
    /// names every caller, <see cref="WellKnownUsers.Public"/> every signed-in one, a user id that user,
    /// and any other value a group — matched through the same transitive membership expansion the
    /// permission fold uses. Live for a group.
    /// </summary>
    private static IObservable<bool> SubjectAdmits(IMessageHub hub, string subject, string userId, AccessContext? caller)
    {
        if (string.Equals(subject, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(true);
        var signedIn = WellKnownUsers.IsAuthenticated(userId) && caller?.IsVirtual != true;
        if (string.Equals(subject, WellKnownUsers.Public, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(signedIn);
        if (!signedIn)
            return Observable.Return(false);
        if (string.Equals(subject, userId, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(true);
        return PermissionEvaluator.ObserveUserGroups(hub, userId)
            .Select(groups => groups.Contains(subject))
            .DistinctUntilChanged();
    }

    /// <summary>
    /// The declaration of <paramref name="nodeType"/>, read only from a type the platform OWNS: the
    /// host-declared configuration node, or a stored type node in a system-owned partition (one-way
    /// <c>_GitSync</c>), followed live. A host-declared type is authoritative — a stored node at the
    /// same path cannot replace its declaration. Null for any other type, and when no declaration
    /// is present.
    /// </summary>
    private static IObservable<PointReadDeclaration?> DeclarationOf(IMessageHub hub, AccessService? access, string nodeType)
    {
        var options = hub.JsonSerializerOptions;
        if (hub.ServiceProvider.FindServedStaticNode(nodeType) is { } declared)
            return Observable.Return(Parse(declared, options));
        var partition = nodeType.Split('/', 2)[0];
        if (partition.Length == 0)
            return Observable.Return<PointReadDeclaration?>(null);
        return hub.ReadStoredNode(AccessAssignmentGuard.SyncConfigPath(partition))
            .Take(1)
            .DefaultIfEmpty(null)
            .SelectMany(sync => AccessAssignmentGuard.IsSystemOwned(sync, options)
                // The type node from its OWNER first, like the instance: a warm mirror must not keep
                // admitting by a declaration the owner has already withdrawn or narrowed.
                ? OwnerCurrent(hub, access, nodeType).Select(type => type is null ? null : Parse(type, options))
                : Observable.Return<PointReadDeclaration?>(null))
            .DistinctUntilChanged();
    }

    /// <summary>
    /// The node at <paramref name="path"/> as its OWNER holds it now, then live. The first revision is
    /// the owner's own answer (a point read of its node, as System), so a write the owner has
    /// acknowledged — a revocation above all — decides the very next read; the process-wide node
    /// stream mirror can trail that acknowledgement, so it only FOLLOWS afterwards, and any mirror
    /// revision older than the owner's is ignored.
    /// </summary>
    private static IObservable<MeshNode?> OwnerCurrent(IMessageHub hub, AccessService? access, string path)
    {
        var ownerRead = access is null
            ? hub.GetMeshNode(path)
            : access.RunAsSystem(() => hub.GetMeshNode(path));
        return ownerRead
            .Take(1)
            .SelectMany(current => current is null
                ? Observable.Return<MeshNode?>(null)
                : SystemRead(hub, access, path)
                    .Where(mirror => mirror is null || mirror.Version >= current.Version)
                    .StartWith(current));
    }

    /// <summary>The node at <paramref name="path"/> from the node-stream mirror, live, read as System — the node-stream read gate passes System straight through, so this never re-enters it.</summary>
    private static IObservable<MeshNode?> SystemRead(IMessageHub hub, AccessService? access, string path)
        => access is null
            ? hub.GetMeshNodeStream(path).Select(n => (MeshNode?)n)
            : access.RunAsSystem(() => hub.GetMeshNodeStream(path).Select(n => (MeshNode?)n));

    private static PointReadDeclaration? Parse(MeshNode typeNode, JsonSerializerOptions options)
        => ContentOf(typeNode, options) is { ValueKind: JsonValueKind.Object } content
           && content.TryGetProperty(DeclarationProperty, out var declaration)
           && declaration.ValueKind == JsonValueKind.Object
            ? declaration.Deserialize<PointReadDeclaration>(options)
            : null;

    private static JsonElement? ContentOf(MeshNode node, JsonSerializerOptions options)
    {
        try
        {
            return node.Content switch
            {
                null => null,
                JsonElement element => element,
                var typed => JsonSerializer.SerializeToElement(typed, typed.GetType(), options),
            };
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static string SubjectOf(AccessContext? caller)
        => caller is null || caller.IsVirtual || string.IsNullOrEmpty(caller.ObjectId)
            ? WellKnownUsers.Anonymous
            : caller.ObjectId;
}

/// <summary>A request a declared point read may admit: a read of the node itself (<paramref name="Area"/> null) or of one of its layout areas.</summary>
/// <param name="Area">Null for the node itself; the layout area's name otherwise (<c>""</c> = the default area).</param>
public sealed record PointReadRequest(string? Area);

/// <summary>What a declared point read admits on one node revision: the subject, and the instant the admission ends (null when nothing ends it).</summary>
/// <param name="Subject">The admitted grant subject.</param>
/// <param name="Until">The instant the admission ends, or null.</param>
public sealed record PointReadAdmission(string Subject, DateTimeOffset? Until);
