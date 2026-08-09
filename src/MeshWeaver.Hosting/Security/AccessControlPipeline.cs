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
                string? userId = null;
                var hubPath = string.Empty;
                var pendingChecks = ImmutableList<(string Path, Permission Permission)>.Empty;
                try
                {
                    rlsDisabled = hub.Configuration.Get<EffectivePermissionsDelegate>() is null;
                    if (!rlsDisabled)
                    {
                        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
                        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
                            ?.CreateLogger("MeshWeaver.AccessContext");

                        userId = ResolveIdentity(delivery, accessService);

                        // Restore the sender's AccessContext onto this scope's AccessService
                        // so SecurityService.GetEffectivePermissions — which reads claim-based
                        // roles from _accessService.Context.Roles — can resolve them. Trigger:
                        // delivery carries non-empty user Roles (signed claim payload).
                        if (delivery.AccessContext is { Roles: { Count: > 0 } } userCtx)
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
                    // IsGranted=false, so `result.Outcome.IsGranted` gates exactly as before) — it
                    // simply stops claiming to know why.
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
                        // the onCompleted branch below invokes `next`, i.e. the ENTIRE
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
                            var (errorType, message) = refusal.Outcome.IsUndetermined
                                ? (ErrorType.Unavailable,
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

        hub.GetEffectivePermissions(ownPath, userId)
            .Take(1)
            .Subscribe(perms =>
            {
                logger?.LogDebug("[GP] reply hub={Hub} user={User} perms={Perms}", ownPath, userId, perms);
                hub.Post(new GetPermissionResponse(perms), o => o.ResponseFor(request));
            },
            ex => logger?.LogWarning(ex, "[GP] stream error hub={Hub}", ownPath));

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
