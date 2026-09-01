using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Security;

/// <summary>
/// Delivery pipeline step that checks <see cref="RequiresPermissionAttribute"/> on incoming messages.
/// When a message type is decorated with [RequiresPermission(...)], the pipeline calls
/// <see cref="RequiresPermissionAttribute.GetPermissionChecks"/> to determine which
/// (path, permission) pairs to validate against the sender's effective permissions.
/// If any check fails, a <see cref="DeliveryFailure"/> with <see cref="ErrorType.Unauthorized"/>
/// is sent back and the message is marked as processed.
///
/// <para>🚨 A check that could not be EVALUATED is reported as
/// <see cref="ErrorType.Unavailable"/>, never as a denial (issue #974). The pipeline refuses the
/// delivery either way — fail-closed is unchanged — but a faulted permission fold produces no
/// verdict, so claiming "user 'x' lacks Read on 'y'" would be a false, actionable-looking
/// statement that sends a correctly-entitled user to request permissions they already hold. The
/// distinction is decided inside the fold (<c>hub.CheckPermissionOutcome</c> →
/// <see cref="PermissionCheckOutcome"/>), rides the chain as a tri-state, and is flattened onto
/// the bus vocabulary exactly once, at the <see cref="DeliveryFailure"/> that crosses the hub
/// boundary.</para>
/// </summary>
public static class AccessControlPipeline
{
    private static readonly ConcurrentDictionary<Type, RequiresPermissionAttribute?> AttributeCache = new();

    /// <summary>
    /// True when the gate could not run because THIS HUB IS GONE — it is shutting down (its own
    /// disposal, or an ancestor's freeze), or the failure is the disposed lifetime scope its
    /// services resolve from.
    ///
    /// <para>Both halves are needed and neither is sufficient. <see cref="IMessageHub.IsShuttingDown"/>
    /// alone misses the RECYCLE window this exists for: the delivery is being evaluated on a hub
    /// whose disposal has already COMPLETED, so it is not "shutting down" any more, it is gone —
    /// and the only evidence left is the <see cref="ObjectDisposedException"/> its scope throws.
    /// The exception test alone would over-claim: an <see cref="ObjectDisposedException"/> from
    /// something the HANDLER touched on a live hub is a real fault, not a recycle, so the message
    /// is matched on the Autofac scope wording the rest of the codebase already keys on (the same
    /// two shapes <c>TeardownStragglerCapturer</c> filters).</para>
    /// </summary>
    /// <summary>
    /// The refusal a delivery gets when it reached a hub that is GONE (see <see cref="IsHubGone(IMessageHub, Exception)"/>).
    ///
    /// <para>🚨 THE WORDING IS CONTRACT, exactly as <c>MessageService.NackThroughParent</c>'s is.
    /// The mesh classifies delivery failures by their MESSAGE TEXT as well as their ErrorType, and
    /// this sentence must be matched as TRANSIENT by every one of
    /// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c>, <c>RoutingGrain.IsTransientFailure</c>
    /// and <c>AreaErrorClassifier.IsTransientHubFailure</c> — that is the whole point: the caller
    /// must RE-PROBE and land on the fresh activation instead of taking a corpse's answer as
    /// final. It therefore carries their markers verbatim ("is shutting down", "Rejecting now").
    /// Reword it casually and #2727 comes back silently — nothing fails to compile, the delivery
    /// is still refused, and the caller simply stops retrying again. Pinned by
    /// <c>RecycledHubRefusalTest</c>.</para>
    /// </summary>
    /// <summary><see cref="RecyclingRefusal(Address, string, Exception)"/> for a reason string.</summary>
    internal static string RecyclingRefusal(Address address, string messageTypeName, string? reason) =>
        $"Hub {address} is shutting down (its access gate could not reach a verdict: {reason}) "
        + $"— cannot evaluate access for {messageTypeName}; the address may reactivate "
        + "(recycle / restart). Rejecting now.";

    internal static string RecyclingRefusal(Address address, string messageTypeName, Exception error) =>
        $"Hub {address} is shutting down (its lifetime scope is gone, {error.GetType().Name}) "
        + $"— cannot evaluate access for {messageTypeName}; the address may reactivate "
        + "(recycle / restart). Rejecting now.";

    /// <summary>
    /// <see cref="IsHubGone(IMessageHub, Exception)"/> for a path that has a REASON STRING rather
    /// than an exception — the permission fold reports an undetermined outcome as text.
    /// </summary>
    internal static bool IsHubGone(IMessageHub hub, string? reason)
        => hub.IsShuttingDown
           || (reason is not null
               && (reason.Contains("LifetimeScope", StringComparison.OrdinalIgnoreCase)
                   || reason.Contains("nested lifetimes cannot be created", StringComparison.OrdinalIgnoreCase)));

    internal static bool IsHubGone(IMessageHub hub, Exception error)
    {
        if (hub.IsShuttingDown)
            return true;
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is not ObjectDisposedException)
                continue;
            var msg = e.Message ?? string.Empty;
            if (msg.Contains("LifetimeScope", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("nested lifetimes cannot be created", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// One (path, permission) check together with the outcome the fold reached for it. Carries the
    /// tri-state (<see cref="PermissionCheckOutcome"/>) AND the identity of the check that produced
    /// it, so the <see cref="DeliveryFailure"/> can name the exact path/permission that refused the
    /// delivery — the whole point of running the checks in order.
    /// </summary>
    private sealed record EvaluatedCheck(string Path, Permission Permission, PermissionCheckOutcome Outcome);

    /// <summary>
    /// Adds the access control pipeline step to a per-node hub. Checks
    /// <see cref="RequiresPermissionAttribute"/> on incoming messages and rejects
    /// unauthorized deliveries via <see cref="DeliveryFailure"/>. Also wires up
    /// the <see cref="GetPermissionRequest"/> handler so every per-node hub
    /// answers "what permissions does the caller have on this path?" via the
    /// canonical message-bus path — this is what <see cref="MeshNodeStreamCache"/>
    /// uses to gate <c>MeshNodeStreamCache.GetStream</c> per user.
    /// </summary>
    public static MessageHubConfiguration AddAccessControlPipeline(this MessageHubConfiguration config)
        => config
        .WithHandler<GetPermissionRequest>(HandleGetPermission)
        .AddDeliveryPipeline(pipeline =>
        {
            var hub = pipeline.Hub;
            // Hub-level permission rules (e.g., WithPublicRead) read from the
            // hub's configuration only — no DI resolution at registration time.
            var hubPermissions = hub.Configuration.Get<HubPermissionRuleSet>();

            // CRITICAL: do NOT resolve SecurityService / AccessService /
            // ILoggerFactory at pipeline-registration time. This callback runs
            // synchronously inside MessageService.ctor (which is itself being
            // resolved by Autofac during MessageHub construction). Resolving
            // any scoped service that transitively depends on IMessageHub here
            // creates a circular DI resolution → stack overflow on hub
            // creation. Instead, resolve lazily per-delivery via the closure
            // below — by then the hub's DI scope is fully built.
            ILogger? logger = null;

            return pipeline.AddPipeline((delivery, ct, next) =>
            {
                var attr = GetAttribute(delivery.Message.GetType());
                if (attr == null)
                    return next.Invoke(delivery, ct);

                // 🚨 SWEEP (issue #974). Everything down to the reactive chain runs SYNCHRONOUSLY
                // on the delivery thread, OUTSIDE the fold's classifier — and every line of it can
                // throw on a hub that is mid-disposal: `hub.Configuration` and
                // `hub.ServiceProvider.GetRequiredService` raise ObjectDisposedException once the
                // DI scope is torn down, and `attr.GetPermissionChecks` runs attribute-supplied
                // code. Un-guarded, such a throw escapes the pipeline entirely and the caller sees
                // an unclassified fault (or, worse, parks until its request timeout) — the exact
                // "removing a catch MOVES the failure" edge #970's own review sweep found in
                // LoadDbRolesAsync, where the hub and workspace were resolved outside the
                // classified chain. A disposing hub is an availability condition, so it gets the
                // same honest answer the fold legs give: refuse the delivery, report Unavailable.
                bool rlsDisabled;
                bool unsecuredDeclared;
                string? userId = null;
                var hubPath = string.Empty;
                var pendingChecks = ImmutableList<(string Path, Permission Permission)>.Empty;
                try
                {
                    rlsDisabled = hub.Configuration.Get<EffectivePermissionsDelegate>() is null;
                    // Resolved unconditionally now: the missing-evaluator branch below has to be
                    // able to REPORT itself, so the logger can no longer hang off the RLS-on path.
                    unsecuredDeclared = hub.Configuration.Get<UnsecuredMeshDeclaration>() is not null;
                    logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("MeshWeaver.AccessContext");
                    if (!rlsDisabled)
                    {
                        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();

                        userId = ResolveIdentity(delivery, accessService);

                        // Restore the sender's AccessContext onto this scope's AccessService so
                        // the permission fold snapshots the CALLER (PermissionEvaluator captures
                        // `accessService.Context ?? accessService.CircuitContext` on this thread,
                        // before any Rx scheduler hop). The fold reads exactly two flags off it:
                        //   • IsApiToken — the API-token clamp, which zeroes the permission set
                        //     for a Bearer context this path's live policy does not admit; and
                        //   • IsHub — the hub-credential early return.
                        // Neither has anything to do with Roles.
                        //
                        // 🚨 THE CONDITION USED TO BE `Roles: { Count: > 0 }` (issue #2976), and
                        // that was the runtime form of the rule this file already states below:
                        // the gate's own INPUT decided whether the gate ran, so "the clamp did not
                        // run" and "the clamp passed" were indistinguishable. Its comment justified
                        // itself in terms of claim-based role resolution — a mechanism that no
                        // longer exists (the 2026-08-05 paywall fix took claim roles out of node
                        // permissions; #2974 removed their last foothold, the API-token capability
                        // hatch). AccessContext.Roles is read NOWHERE in PermissionEvaluator, so
                        // the condition survived with its reason gone — and it was never the right
                        // one: a token minted through an IdP that emits no role claims (the
                        // ORDINARY case; ApiToken.Roles is usually empty) arrived here with
                        // Roles = [], the restore was skipped, capturedContext was null, IsApiToken
                        // was never seen and the Bearer delivery was evaluated as an interactive
                        // session. The exact-read path was never exposed — MeshNodeStreamCache
                        // .GetStreamRaw captures the caller itself — so the hole was precisely the
                        // MESSAGE-ROUTED checks this pipeline gates. Pinned by
                        // RoutedApiTokenClampTest.
                        //
                        // 🚨 A REAL PRINCIPAL, not "any context": a hub-shaped ObjectId (sync/,
                        // mesh/, node/, activity/, portal/) is NOT a user identity and must never
                        // be installed as one — the mesh-wide rule AccessService.SetContext's leak
                        // tripwire, UserServiceDeliveryPipeline's `shouldStamp` and
                        // MeshNodeStreamCache's pass-through all encode. Those keep falling back to
                        // the hub/System rules exactly as before; only the empty-ObjectId anonymous
                        // context is likewise skipped, since it names nobody to restore.
                        if (delivery.AccessContext is { } userCtx
                            && !string.IsNullOrEmpty(userCtx.ObjectId)
                            && !AccessService.LooksLikeHubPrincipal(userCtx.ObjectId))
                        {
                            accessService.SetContext(userCtx);
                        }

                        // Log identity resolution details for debugging access issues
                        if (string.IsNullOrEmpty(userId))
                            logger?.LogWarning(
                                "AccessControlPipeline: ANONYMOUS delivery — hub={Hub}, message={MessageType}, " +
                                "delivery.AccessContext={DeliveryContext}, accessService.Context={ServiceContext}, " +
                                "circuitContext={CircuitContext}, sender={Sender}",
                                hub.Address,
                                delivery.Message.GetType().Name,
                                delivery.AccessContext?.ObjectId ?? "(null)",
                                accessService.Context?.ObjectId ?? "(null)",
                                accessService.CircuitContext?.ObjectId ?? "(null)",
                                delivery.Sender);

                        hubPath = string.Join("/", hub.Address.Segments);

                        // Filter the permission checks attribute decided this delivery needs;
                        // hub-level rules (e.g. WithPublicRead) get short-circuited synchronously
                        // here so the reactive pipeline below only handles the remaining checks.
                        foreach (var check in attr.GetPermissionChecks(delivery, hubPath))
                        {
                            if (hubPermissions != null && hubPermissions.HasPermission(check.Permission, delivery, userId))
                                continue;
                            pendingChecks = pendingChecks.Add(check);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // NOT a swallow: the delivery is refused (next is never invoked) and the
                    // reason is both logged and carried to the caller. It is classified, because
                    // "the gate could not run" is not the same fact as "you are not allowed".
                    //
                    // 🚨 …and "the gate could not run BECAUSE THIS HUB IS GONE" is a third fact
                    // again, which is what issue #2727 is. A delivery routed to a hub that has
                    // since been RECYCLED reaches this pipeline holding the OLD hub's captured
                    // `hub`, whose lifetime scope core#2438 now genuinely closes — so the first
                    // `GetRequiredService` throws ObjectDisposedException and every such delivery
                    // was answered `Unavailable` with a sentence carrying none of the markers the
                    // transient classifiers match (MeshNodeStreamCache.IsTransientOwnerFailure,
                    // RoutingGrain.IsTransientFailure, AreaErrorClassifier.IsTransientHubFailure).
                    // The caller therefore treated a hub that is COMING BACK as a terminal answer
                    // and never re-probed: RecycleSurvivesItsOwnDisposeTest reads null for an
                    // address that is alive one activation later, SilentReadNackTest gets a
                    // non-NACK, and a layout render wedges instead of retrying. Before #2438 the
                    // scope never actually closed, so the same ordering resolved from a zombie
                    // scope and passed — the bug was always here, the leak fix only exposed it.
                    //
                    // So: recognise it, and answer it in the vocabulary the rest of the mesh
                    // already speaks for a recycling address — the SAME wording MessageService's
                    // shutting-down gate uses, markers included ("is shutting down", "the address
                    // may reactivate (recycle / restart)", "Rejecting now"), so the existing
                    // re-probe machinery rides it out and lands on the fresh activation.
                    // Refusal is unchanged: `next` is still never invoked, so nothing is granted.
                    if (IsHubGone(hub, ex))
                    {
                        var recycling = RecyclingRefusal(hub.Address, delivery.Message.GetType().Name, ex);
                        logger?.LogDebug(ex,
                            "AccessControlPipeline: {MessageType} reached {Hub} after that hub was disposed — "
                            + "NACKing as SHUTTING DOWN so the caller re-probes the fresh activation, "
                            + "not as a permission verdict",
                            delivery.Message.GetType().Name, hub.Address);
                        // 🚨 Through the PARENT. This hub's own Post is gated closed once it is past
                        // DisposeHostedHubs (PostImplGeneric refuses every non-shutdown message), so
                        // posting the refusal here is how the caller ended up with silence instead of
                        // a NACK. Same escape MessageService.NackThroughParent uses; correlation
                        // rides ResponseFor's RequestId, never the posting hub's identity. During a
                        // whole-mesh teardown the parent is itself past that point, the guard skips
                        // the post, and nobody is waiting anyway.
                        var parent = hub.Configuration.ParentHub;
                        if (parent is not null && parent.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
                            parent.Post(
                                new DeliveryFailure(delivery)
                                {
                                    ErrorType = ErrorType.ShuttingDown,
                                    Message = recycling
                                },
                                o => o.ResponseFor(delivery));
                        // 🚨 Forwarded, NOT Failed — the SAME shape both sibling branches use, and
                        // for the same reason. The authoritative typed DeliveryFailure has just been
                        // posted; returning a Failed delivery on top of it makes ReportFailure post a
                        // SECOND, unclassified one for the same request (that is what
                        // MessageService.FailureAlreadyReported exists to suppress, and this path has
                        // no marker to carry). Two answers to one correlation is strictly worse than
                        // the misclassification this fix removes.
                        return Observable.Return(delivery.Forwarded());
                    }

                    logger?.LogWarning(ex,
                        "AccessControlPipeline: could not evaluate access for {MessageType} on {Hub} — "
                        + "reporting UNAVAILABLE (retryable), not a denial",
                        delivery.Message.GetType().Name, hub.Address);
                    hub.Post(
                        new DeliveryFailure(delivery)
                        {
                            ErrorType = ErrorType.Unavailable,
                            Message =
                                $"Permission check unavailable on '{hub.Address}' — the access gate could not run, "
                                + $"so no verdict was reached. This is NOT a statement about this user's rights. "
                                + $"Retry shortly. Cause: {ex.GetType().Name}: {ex.Message}"
                        },
                        o => o.ResponseFor(delivery));
                    return Observable.Return(delivery.Forwarded());
                }

                // 🚨 THE GATE DOES NOT SKIP ITSELF. This pipeline exists only to enforce
                // [RequiresPermission]; reaching it with no EffectivePermissionsDelegate means the
                // gate was installed without the evaluator it needs, and the old behaviour —
                // `rlsDisabled ⇒ next.Invoke` — let EVERY permission check through while looking
                // exactly like a mesh where every check passed. That is the "a gate never tests its
                // own inputs" rule (AGENTS.md) in runtime form: an input-shaped condition that
                // silently converts "could not run" into "allowed".
                //
                // In THIS tree the state is unreachable by construction — AddAccessControlPipeline
                // has exactly one caller and it sits in the same expression as AddRowLevelSecurity,
                // so pipeline-installed ⟺ delegate-installed. The branch is for the embedder who
                // wires the pipeline by hand, and for the day that single call site is split.
                //
                // Reported as Unavailable, not Unauthorized, for the reason issue #974 established:
                // no verdict was reached, so claiming the user lacks a permission would be a false
                // and actionable-looking statement. Fail closed, and say honestly why.
                if (rlsDisabled && !unsecuredDeclared)
                {
                    var message =
                        $"Permission check unavailable on '{hub.Address}' — the access control pipeline is "
                        + $"installed but no EffectivePermissionsDelegate is registered, so no verdict can be "
                        + $"reached for {delivery.Message.GetType().Name}. Register one with "
                        + $"AddRowLevelSecurity(), or declare the mesh ungated on purpose with "
                        + $"AllowUnsecuredMesh(reason).";
                    logger?.LogError("AccessControlPipeline: {Message}", message);
                    hub.Post(
                        new DeliveryFailure(delivery)
                        {
                            ErrorType = ErrorType.Unavailable,
                            Message = message
                        },
                        o => o.ResponseFor(delivery));
                    return Observable.Return(delivery.Forwarded());
                }

                if (rlsDisabled || pendingChecks.IsEmpty)
                    return next.Invoke(delivery, ct);

                // Sync-delivery shape (Doc/Architecture/AsynchronousCalls.md): the
                // pipeline lambda returns delivery.Forwarded() immediately. The
                // reactive chain runs each permission check via the IObservable<bool>
                // surface (.HasPermission), short-circuits on the first decisive
                // outcome, and either posts the rejection response or fires next from
                // inside Subscribe — fire-and-forget for next.Invoke (its Task is not
                // observed by anyone since downstream handlers post their own response).
                // 🚨 Always pass an explicit userId (defaulting to Anonymous)
                // — never the no-arg overload that would read accessService.Context,
                // which can hold stale "system-security" from hub-init's
                // ImpersonateAsSystem scope. See ResolveIdentity's comment.
                var effectiveUserId = userId ?? WellKnownUsers.Anonymous;
                pendingChecks.ToObservable()
                    // 🚨 CheckPermissionOutcome, NOT CheckPermission + a local .Catch(→false)
                    // — issue #974. The fold is the only party that knows whether it reached a
                    // verdict, so it is the only place that may decide "denied" vs. "could not
                    // determine". It hands back a PermissionCheckOutcome and the tri-state rides
                    // the whole chain to the responder below; nothing downstream re-derives the
                    // difference from a bare false or an exception message.
                    //
                    // What was here before: `.Catch<bool, Exception>(_ => Observable.Return(false))`.
                    // Fail-closed, and a LIE — a faulted fold arrived at the caller as "Access
                    // denied: user 'x' lacks Read permission on 'y'", which is an actionable-looking
                    // sentence that sends a correctly-entitled user to request permissions they
                    // already hold. The replacement is still fail-closed (Undetermined carries
                    // IsGranted=false, so the `!evaluated.Outcome.IsGranted` filter below gates
                    // exactly as before) — it simply stops claiming to know why.
                    //
                    // No Timeout here: the access cache must always be a
                    // reactive Subscribe over the hierarchical union
                    // (self + ancestors) of AccessAssignment streams,
                    // which is already populated synchronously from
                    // IStaticNodeProvider at SecurityService construction.
                    // A 10s wait was a workaround for a wedged cache —
                    // fix the cache, don't ceiling-block here. If the
                    // cache genuinely never emits, that's a framework
                    // bug to surface, not paper over with a deny.
                    .Select(check => hub.CheckPermissionOutcome(check.Path, effectiveUserId, check.Permission)
                        // 🚨 TakeDecisionOutsideGate, NOT a bare Take(1) — issue #899. This is
                        // the BROADEST generator of the Rx lock-order inversion in the repo:
                        // the all-granted branch below invokes `next`, i.e. the ENTIRE
                        // downstream handler for EVERY [RequiresPermission] message. On a warm
                        // permission cache the fold emits synchronously during Subscribe while
                        // holding its CombineLatest gate, so with a bare Take(1) that whole
                        // handler body — storage writes, cache invalidation, the (synchronous,
                        // by contract) change-feed fan-out — ran inside the lock, and two hubs
                        // doing it at once deadlock on {own fold gate, shared synced-query
                        // gate}. The Take(1) is still needed (CheckPermission rides the live
                        // AccessAssignment synced stream and never completes, so Concat below
                        // would never advance); TakeDecisionOutsideGate keeps it and adds the
                        // hop. Placed AFTER the Catch so the fail-closed path leaves the gate
                        // too. Identity is unaffected: the inner UserServiceDeliveryPipeline
                        // re-stamps AccessService.Context from delivery.AccessContext before
                        // the handler body runs, and the pool hop flows ExecutionContext
                        // anyway. See HubPermissionExtensions.TakeDecisionOutsideGate.
                        .TakeDecisionOutsideGate()
                        .Select(outcome => new EvaluatedCheck(check.Path, check.Permission, outcome)))
                    // Concat, not Merge: the checks run ONE AT A TIME, in order — which is what
                    // makes the termination below actually save the remaining evaluations, and
                    // what makes the FIRST decisive outcome (not an arbitrary racing one) the
                    // answer the caller gets.
                    .Concat()
                    // 🚨 TERMINATES at the first decisive outcome — it does not merely ignore the
                    // rest. Where+Take(1) disposes the Concat subscription, so the remaining
                    // inner observables are never subscribed; CheckPermissionOutcome is built
                    // inside Observable.Defer, so "never subscribed" means the fold never runs.
                    // The guard this replaces (a `decided` bool consulted in all three Subscribe
                    // callbacks) suppressed the duplicate POST while letting every remaining check
                    // evaluate anyway. That is work whose result cannot change the answer, spent
                    // at the exact moment the system is least able to afford it: an Undetermined
                    // outcome means a degraded dependency (wedged access cache, unreachable
                    // Postgres, hub mid-disposal), and every superfluous check re-hits it. This
                    // repo has twice watched that shape become the outage — the 429 wedge that
                    // leaked round hubs into a 502, and the NotFound storm that wedged a portal
                    // (Doc/Architecture/ErrorPropagationAndWedges.md). Refusing early is both the
                    // cheaper and the safer answer.
                    .Where(evaluated => !evaluated.Outcome.IsGranted)
                    .Take(1)
                    // Exactly ONE decision emission, by construction: the check that refused the
                    // delivery, or null meaning "every check was granted". The single-decision
                    // invariant is now the SHAPE of the stream rather than a mutable flag three
                    // callbacks have to remember to consult.
                    .Select(evaluated => (EvaluatedCheck?)evaluated)
                    .DefaultIfEmpty()
                    .Subscribe(
                        refusal =>
                        {
                            if (refusal is null)
                            {
                                // All checks passed — invoke next. next is a cold
                                // IObservable now; Subscribe to run the downstream side
                                // effect (the old Task was hot/already-running).
                                // onError is mandatory: a faulted downstream chain would
                                // otherwise vanish unobserved inside the security pipeline.
                                next.Invoke(delivery, ct).Subscribe(
                                    _ => { },
                                    ex => logger?.LogError(ex,
                                        "AccessControlPipeline: downstream pipeline faulted after permission pass for {MessageType} on {Hub}",
                                        delivery.Message.GetType().Name, hub.Address));
                                return;
                            }

                            var effectiveUser = userId ?? "(anonymous)";

                            // 🚨 The tri-state ends HERE, and this is the one place it is allowed
                            // to (issue #974). Everything up to this point carried a
                            // PermissionCheckOutcome; the ANSWER has to go back to the caller as a
                            // DeliveryFailure, which crosses a hub (and, in the Orleans portal, a
                            // SILO) boundary and carries a single flat ErrorType. So the tri-state
                            // is projected onto the bus vocabulary exactly once, at the boundary
                            // that flattens it — never re-derived afterwards from the message text.
                            //
                            //   Denied       → ErrorType.Unauthorized  ("we checked; you may not")
                            //   Undetermined → ErrorType.Unavailable   ("we could not check")
                            //
                            // Both refuse the delivery — the fail-closed behaviour is UNCHANGED
                            // and `next` is not invoked on either leg. What changes is that the
                            // second one stops impersonating the first.
                            // 🚨 THREE outcomes, not two — because "we could not check" has two very
                            // different causes and only one of them is about this mesh being busy.
                            //
                            //   Denied                      → Unauthorized  ("we checked; you may not")
                            //   Undetermined, hub GONE      → ShuttingDown  ("this activation is going away")
                            //   Undetermined, hub healthy   → Unavailable   ("we could not check")
                            //
                            // The middle one is issue #2673, measured. When the owner is recycled
                            // mid-read the fold cannot reach a verdict, and answering that as
                            // Unavailable — with wording no transient classifier matches
                            // (MeshNodeStreamCache.IsTransientOwnerFailure and friends key on
                            // "is shutting down" / "Rejecting now") — makes GetMeshNode take it as
                            // TERMINAL and resolve NULL for a node that exists at an address that
                            // reactivates. Measured shape:
                            //   "Permission check unavailable for user 'Roland' on
                            //    'TestData/reprobe-recovers' (Read) — no verdict was reached"
                            //   → [TEST] recovered in 62 ms: (null)
                            // Answering it in the recycling vocabulary instead makes the caller
                            // re-probe, which is what lands it on the fresh activation.
                            //
                            // Fail-closed is UNCHANGED on every leg: `next` is never invoked here.
                            var (errorType, message) = refusal.Outcome.IsUndetermined
                                ? IsHubGone(hub, refusal.Outcome.UndeterminedReason)
                                    ? (ErrorType.ShuttingDown,
                                        RecyclingRefusal(hub.Address, delivery.Message.GetType().Name,
                                            refusal.Outcome.UndeterminedReason))
                                    : (ErrorType.Unavailable,
                                        $"Permission check unavailable for user '{effectiveUser}' on '{refusal.Path}' "
                                        + $"({refusal.Permission}) — no verdict was reached, so this is NOT a statement "
                                        + $"about this user's rights. Retry shortly. Cause: {refusal.Outcome.UndeterminedReason}")
                                : (ErrorType.Unauthorized,
                                    $"Access denied: user '{effectiveUser}' lacks {refusal.Permission} permission on '{refusal.Path}'");

                            // Include the triggering message + sender so a denial on a rogue/reserved path
                            // (e.g. 'login') names its caller instead of being an unattributable warning.
                            logger?.LogWarning("AccessControlPipeline: {Message} (triggered by message={MessageType} sender={Sender} hub={Hub})",
                                message, delivery.Message.GetType().Name, delivery.Sender, hub.Address);

                            hub.Post(
                                new DeliveryFailure(delivery)
                                {
                                    ErrorType = errorType,
                                    Message = message
                                },
                                o => o.ResponseFor(delivery));
                        },
                        ex =>
                        {
                            // Reaching here means the fault escaped CheckPermissionOutcome's
                            // classifier — it came from the Rx machinery BETWEEN the folds
                            // (ToObservable / Concat / the TakeDecisionOutsideGate scheduler hop),
                            // not from a fold itself. Same honest answer, same reason: we did not
                            // reach a verdict, so we must not claim one.
                            //
                            // Fail closed: `next` is NOT invoked (a fall-through would hang the
                            // caller, because downstream handlers assume a check already happened).
                            // But the refusal is reported as Unavailable, not Unauthorized — the
                            // old "Access denied: permission check failed for user 'x'" told an
                            // entitled user to go ask for permissions they already had.
                            var effectiveUser = userId ?? "(anonymous)";
                            var message =
                                $"Permission check unavailable for user '{effectiveUser}' on '{hubPath}' — the check "
                                + $"itself failed, so no verdict was reached. This is NOT a statement about this "
                                + $"user's rights. Retry shortly. Cause: {ex.GetType().Name}: {ex.Message}";
                            logger?.LogWarning(ex, "AccessControlPipeline: {Message}", message);

                            hub.Post(
                                new DeliveryFailure(delivery)
                                {
                                    ErrorType = ErrorType.Unavailable,
                                    Message = message
                                },
                                o => o.ResponseFor(delivery));
                        });

                return Observable.Return(delivery.Forwarded());
            });
        });

    /// <summary>
    /// Sync handler for <see cref="GetPermissionRequest"/>. The hub always
    /// evaluates permissions on its OWN path (<c>hub.Address.ToString()</c>) —
    /// the request never carries a path; routing decides which hub responds.
    /// Resolves the per-hub scoped <c>SecurityService</c> and replies
    /// via Subscribe — no await, no scope juggling at the caller site.
    /// </summary>
    internal static IMessageDelivery HandleGetPermission(IMessageHub hub, IMessageDelivery<GetPermissionRequest> request)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.GetPermission");
        var ownPath = hub.Address.ToString();
        logger?.LogDebug("[GP] enter hub={Hub}", ownPath);

        if (hub.Configuration.Get<EffectivePermissionsDelegate>() is null)
        {
            logger?.LogDebug("[GP] RLS not enabled → posting None");
            hub.Post(new GetPermissionResponse(Permission.None), o => o.ResponseFor(request));
            return request.Processed();
        }

        // Resolve the originating user explicitly via the same ResolveIdentity
        // path the pre-handler permission pipeline uses — NEVER the no-arg
        // GetEffectivePermissions(ownPath), which falls back to
        // accessService.Context. That AsyncLocal at handler-entry holds
        // "system-security" from SecurityService's bootstrap-time
        // ImpersonateAsSystem scope (it leaks past the using-block because the
        // bootstrap action-block thread captured the context at construction).
        // Trusting it returned Permission.All for every caller — including
        // anonymous deliveries — and silently turned every GetPermission
        // probe into a System-level reply.
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var userId = ResolveIdentity(request, accessService) ?? WellKnownUsers.Anonymous;

        // 🚨 EVERY terminal answers — #1362/#1364's rule, applied here too (#1446). The fold can
        // complete WITHOUT emitting (an empty static seed plus an `enriched` leg that completes
        // having produced nothing), and a Subscribe with no completion arm discards that terminal
        // silently: the request is then owed a reply that nobody will ever post, and the caller
        // learns about it only when its own RequestTimeout fires — naming the caller's impatience
        // rather than the fold that produced nothing. DefaultIfEmpty turns "completed with no
        // verdict" into an explicit one, so all three arms lead to exactly one Post.
        hub.GetEffectivePermissions(ownPath, userId)
            .Take(1)
            .Select(perms => (Permission?)perms)
            .DefaultIfEmpty(null)
            .Subscribe(perms =>
            {
                if (perms is null)
                    logger?.LogWarning(
                        "[GP] the permission fold for hub={Hub} user={User} COMPLETED without a "
                        + "verdict — answering None rather than leaving the request unanswered",
                        ownPath, userId);
                else
                    logger?.LogDebug("[GP] reply hub={Hub} user={User} perms={Perms}", ownPath, userId, perms);
                hub.Post(new GetPermissionResponse(perms ?? Permission.None), o => o.ResponseFor(request));
            },
            ex =>
            {
                logger?.LogWarning(ex, "[GP] stream error hub={Hub}", ownPath);
                hub.Post(new GetPermissionResponse(Permission.None), o => o.ResponseFor(request));
            });

        return request.Processed();
    }

    /// <summary>
    /// Resolves the user identity from sources in priority order:
    /// 1. delivery.AccessContext — stamped by the sender's PostPipeline (source of truth)
    /// 2. SubscribeRequest.Identity — explicit identity on the subscription (survives Orleans routing)
    /// 3. accessService.CircuitContext — Blazor circuit (monolith only)
    ///
    /// 🚨 NOT consulted: accessService.Context (the AsyncLocal). This pipeline
    /// runs BEFORE UserServiceDeliveryPipeline (pipelines compose outside-in
    /// via Aggregate), so the AsyncLocal at this point reflects whatever was
    /// on the action-block thread when the hub initialized — typically
    /// "system-security" because SecurityService ran under
    /// `using ImpersonateAsSystem()` during its bootstrap. Trusting that
    /// value gave Anonymous deliveries System-level permissions on every
    /// hub whose SecurityService had initialized (symptom 2026-05-22:
    /// UserHubAccessTest.AnonymousUser_CannotReadUserHub passed an
    /// anonymous GetDataRequest because ResolveIdentity returned
    /// "system-security" and System has Permission.All).
    /// </summary>
    private static string? ResolveIdentity(IMessageDelivery delivery, AccessService accessService)
    {
        // 1. Delivery AccessContext (source of truth from sender)
        var userId = delivery.AccessContext?.ObjectId;
        if (!string.IsNullOrEmpty(userId))
            return userId;

        // 2. Explicit identity on SubscribeRequest (survives Orleans serialization)
        if (delivery.Message is SubscribeRequest sub && !string.IsNullOrEmpty(sub.Identity))
            return sub.Identity;

        // 3. Blazor circuit context (monolith only — set per-circuit-activity,
        //    not contaminated by hub-init impersonations).
        return accessService.CircuitContext?.ObjectId;
    }

    private static RequiresPermissionAttribute? GetAttribute(Type messageType)
        => AttributeCache.GetOrAdd(messageType, static type =>
            type.GetCustomAttribute<RequiresPermissionAttribute>(inherit: true));
}
