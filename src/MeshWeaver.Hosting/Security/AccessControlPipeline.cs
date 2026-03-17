using System.Collections.Concurrent;
using System.Reflection;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
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

            return pipeline.AddPipeline(async (delivery, ct, next) =>
            {
                var attr = GetAttribute(delivery.Message.GetType());
                if (attr == null)
                    return await next.Invoke(delivery, ct);

                var userId = accessService.Context?.ObjectId
                             ?? accessService.CircuitContext?.ObjectId;

                var hubPath = string.Join("/", hub.Address.Segments);

                foreach (var (path, permission) in attr.GetPermissionChecks(delivery, hubPath))
                {
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

    private static RequiresPermissionAttribute? GetAttribute(Type messageType)
        => AttributeCache.GetOrAdd(messageType, static type =>
            type.GetCustomAttribute<RequiresPermissionAttribute>(inherit: true));
}
