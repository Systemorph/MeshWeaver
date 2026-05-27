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
/// </summary>
public static class AccessControlPipeline
{
    private static readonly ConcurrentDictionary<Type, RequiresPermissionAttribute?> AttributeCache = new();

    /// <summary>
    /// Adds the access control pipeline step to a per-node hub. Checks
    /// <see cref="RequiresPermissionAttribute"/> on incoming messages and rejects
    /// unauthorized deliveries via <see cref="DeliveryFailure"/>. Also wires up
    /// the <see cref="GetPermissionRequest"/> handler so every per-node hub
    /// answers "what permissions does the caller have on this path?" via the
    /// canonical message-bus path — this is what <see cref="MeshNodeStreamCache"/>
    /// uses to gate <see cref="MeshNodeStreamCache.GetStream"/> per user.
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

                if (hub.Configuration.Get<EffectivePermissionsDelegate>() is null)
                    return next.Invoke(delivery, ct);

                var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
                logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.AccessContext");

                var userId = ResolveIdentity(delivery, accessService);

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

                var hubPath = string.Join("/", hub.Address.Segments);

                // Filter the permission checks attribute decided this delivery needs;
                // hub-level rules (e.g. WithPublicRead) get short-circuited synchronously
                // here so the reactive pipeline below only handles the remaining checks.
                var pendingChecks = ImmutableList<(string Path, Permission Permission)>.Empty;
                foreach (var check in attr.GetPermissionChecks(delivery, hubPath))
                {
                    if (hubPermissions != null && hubPermissions.HasPermission(check.Permission, delivery, userId))
                        continue;
                    pendingChecks = pendingChecks.Add(check);
                }

                if (pendingChecks.IsEmpty)
                    return next.Invoke(delivery, ct);

                // Sync-delivery shape (Doc/Architecture/AsynchronousCalls.md): the
                // pipeline lambda returns delivery.Forwarded() immediately. The
                // reactive chain runs each permission check via the IObservable<bool>
                // surface (.HasPermission), short-circuits on the first denial, and
                // either posts the rejection response or fires next from inside
                // Subscribe — fire-and-forget for next.Invoke (its Task is not
                // observed by anyone since downstream handlers post their own response).
                var decided = false;
                // 🚨 Always pass an explicit userId (defaulting to Anonymous)
                // — never the no-arg overload that would read accessService.Context,
                // which can hold stale "system-security" from hub-init's
                // ImpersonateAsSystem scope. See ResolveIdentity's comment.
                var effectiveUserId = userId ?? WellKnownUsers.Anonymous;
                pendingChecks.ToObservable()
                    .Select(check => hub.CheckPermission(check.Path, effectiveUserId, check.Permission)
                        // HasPermission rides the live AccessAssignment synced
                        // stream — a hot, never-completing observable. Take(1)
                        // closes each inner so Concat below actually advances
                        // through the check list and OnCompleted fires.
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
                        .Take(1)
                        .Catch<bool, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "AccessControlPipeline: permission check on {Path} for {Permission} threw — failing closed. "
                                + "Hub={Hub}, message={MessageType}",
                                check.Path, check.Permission, hub.Address, delivery.Message.GetType().Name);
                            return Observable.Return(false);
                        })
                        .Select(ok => (Check: check, Ok: ok)))
                    .Concat()
                    .Subscribe(
                        result =>
                        {
                            if (decided || result.Ok) return; // permitted or already rejected
                            decided = true;
                            var effectiveUser = userId ?? "(anonymous)";
                            var message = $"Access denied: user '{effectiveUser}' lacks {result.Check.Permission} permission on '{result.Check.Path}'";
                            logger?.LogWarning("AccessControlPipeline: {Message}", message);

                            hub.Post(
                                new DeliveryFailure(delivery)
                                {
                                    ErrorType = ErrorType.Unauthorized,
                                    Message = message
                                },
                                o => o.ResponseFor(delivery));
                        },
                        ex =>
                        {
                            if (decided) return;
                            decided = true;
                            // Fail closed: a permission lookup that throws is treated as a
                            // denial — post a DeliveryFailure so the caller observes a clean
                            // Unauthorized rather than waiting for the request timeout (the
                            // previous fall-through silently invoked next, which then hung
                            // because downstream handlers expected a permission check to
                            // have happened).
                            var effectiveUser = userId ?? "(anonymous)";
                            var message = $"Access denied: permission check failed for user '{effectiveUser}' on '{hubPath}' — {ex.Message}";
                            logger?.LogWarning(ex, "AccessControlPipeline: {Message}", message);

                            hub.Post(
                                new DeliveryFailure(delivery)
                                {
                                    ErrorType = ErrorType.Unauthorized,
                                    Message = message
                                },
                                o => o.ResponseFor(delivery));
                        },
                        () =>
                        {
                            if (decided) return;
                            decided = true;
                            // All checks passed — invoke next; fire-and-forget.
                            _ = next.Invoke(delivery, ct);
                        });

                return Task.FromResult(delivery.Forwarded());
            });
        });

    /// <summary>
    /// Sync handler for <see cref="GetPermissionRequest"/>. The hub always
    /// evaluates permissions on its OWN path (<c>hub.Address.ToString()</c>) —
    /// the request never carries a path; routing decides which hub responds.
    /// Resolves the per-hub scoped <see cref="SecurityService"/> and replies
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
