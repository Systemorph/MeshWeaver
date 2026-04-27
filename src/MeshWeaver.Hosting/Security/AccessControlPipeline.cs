using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
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
    /// unauthorized deliveries via <see cref="DeliveryFailure"/>.
    /// (The <see cref="GetPermissionRequest"/> handler is registered on the mesh
    /// hub itself by <see cref="SecurityServiceExtensions.AddRowLevelSecurity"/>
    /// — see <see cref="HandleGetPermission"/>.)
    /// </summary>
    public static MessageHubConfiguration AddAccessControlPipeline(this MessageHubConfiguration config)
        => config.AddDeliveryPipeline(pipeline =>
        {
            var hub = pipeline.Hub;
            // Hub-level permission rules (e.g., WithPublicRead) read from the
            // hub's configuration only — no DI resolution at registration time.
            var hubPermissions = hub.Configuration.Get<HubPermissionRuleSet>();

            // CRITICAL: do NOT resolve ISecurityService / AccessService /
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

                var securityService = hub.ServiceProvider.GetService<ISecurityService>();
                if (securityService == null)
                    return next.Invoke(delivery, ct);

                var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
                logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.AccessContext");

                var userId = ResolveIdentity(delivery, accessService);

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
                pendingChecks.ToObservable()
                    .Select(check => (string.IsNullOrEmpty(userId)
                            ? securityService.HasPermission(check.Path, check.Permission)
                            : securityService.HasPermission(check.Path, userId, check.Permission))
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
                            // Permission lookup failed — let next process naturally so a
                            // downstream handler can decide what to do (avoids stuck deliveries).
                            logger?.LogWarning(ex, "AccessControlPipeline: permission check threw — falling through");
                            _ = next.Invoke(delivery, ct);
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
    /// Resolves the per-hub scoped <see cref="ISecurityService"/> and replies
    /// via Subscribe — no await, no scope juggling at the caller site.
    /// </summary>
    internal static IMessageDelivery HandleGetPermission(IMessageHub hub, IMessageDelivery<GetPermissionRequest> request)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.GetPermission");
        var ownPath = hub.Address.ToString();
        logger?.LogDebug("[GP] enter hub={Hub}", ownPath);

        var sec = hub.ServiceProvider.GetService<ISecurityService>();
        if (sec is null)
        {
            logger?.LogDebug("[GP] sec is null → posting None");
            hub.Post(new GetPermissionResponse(Permission.None), o => o.ResponseFor(request));
            return request.Processed();
        }

        // Always evaluate for the current user on the hub's own path.
        sec.GetEffectivePermissions(ownPath)
            .Take(1)
            .Subscribe(perms =>
            {
                logger?.LogDebug("[GP] reply hub={Hub} perms={Perms}", ownPath, perms);
                hub.Post(new GetPermissionResponse(perms), o => o.ResponseFor(request));
            },
            ex => logger?.LogWarning(ex, "[GP] stream error hub={Hub}", ownPath));

        return request.Processed();
    }

    /// <summary>
    /// Resolves the user identity from multiple sources in priority order:
    /// 1. delivery.AccessContext — stamped by the sender's PostPipeline
    /// 2. SubscribeRequest.Identity — explicit identity on the subscription (survives Orleans routing)
    /// 3. accessService.Context — set by UserServiceDeliveryPipeline (may not be set yet)
    /// 4. accessService.CircuitContext — Blazor circuit (monolith only)
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

        // 3. AccessService context (set by earlier pipeline steps)
        userId = accessService.Context?.ObjectId;
        if (!string.IsNullOrEmpty(userId))
            return userId;

        // 4. Blazor circuit context (monolith only)
        return accessService.CircuitContext?.ObjectId;
    }

    private static RequiresPermissionAttribute? GetAttribute(Type messageType)
        => AttributeCache.GetOrAdd(messageType, static type =>
            type.GetCustomAttribute<RequiresPermissionAttribute>(inherit: true));
}
