using System.Collections.Concurrent;
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
    /// Adds the access control pipeline step to a hub configuration.
    /// </summary>
    public static MessageHubConfiguration AddAccessControlPipeline(this MessageHubConfiguration config)
        => config.AddDeliveryPipeline(pipeline =>
        {
            var hub = pipeline.Hub;
            var securityService = hub.ServiceProvider.GetService<ISecurityService>();
            if (securityService == null)
                return pipeline;

            var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("AccessControlPipeline");

            // Hub-level permission rules (e.g., WithPublicRead) — checked before ISecurityService
            var hubPermissions = hub.Configuration.Get<HubPermissionRuleSet>();

            return pipeline.AddPipeline(async (delivery, ct, next) =>
            {
                var attr = GetAttribute(delivery.Message.GetType());
                if (attr == null)
                    return await next.Invoke(delivery, ct);

                var userId = ResolveIdentity(delivery, accessService);

                var hubPath = string.Join("/", hub.Address.Segments);

                foreach (var (path, permission) in attr.GetPermissionChecks(delivery, hubPath))
                {
                    // Check hub-level rules first (e.g., WithPublicRead grants Read to authenticated users)
                    if (hubPermissions != null && hubPermissions.HasPermission(permission, delivery, userId))
                        continue;

                    var hasPermission = !string.IsNullOrEmpty(userId)
                        ? await securityService.HasPermissionAsync(path, userId, permission, ct)
                        : await securityService.HasPermissionAsync(path, permission, ct);

                    if (!hasPermission)
                    {
                        var effectiveUser = userId ?? "(anonymous)";
                        var message = $"Access denied: user '{effectiveUser}' lacks {permission} permission on '{path}'";
                        logger?.LogWarning("AccessControlPipeline: {Message}", message);

                        hub.Post(
                            new DeliveryFailure(delivery)
                            {
                                ErrorType = ErrorType.Unauthorized,
                                Message = message
                            },
                            o => o.ResponseFor(delivery));

                        return delivery.Processed();
                    }
                }

                return await next.Invoke(delivery, ct);
            });
        });

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
