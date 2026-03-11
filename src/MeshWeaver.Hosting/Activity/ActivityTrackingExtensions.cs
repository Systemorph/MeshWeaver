using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Activity;

/// <summary>
/// Extension methods for configuring activity tracking.
/// </summary>
public static class ActivityTrackingExtensions
{
    /// <summary>
    /// Adds activity tracking via ActivityLogBundler which persists bundled activity logs
    /// directly to IMeshStorage (bypassing message handlers to avoid infinite loops).
    /// </summary>
    public static MeshBuilder AddActivityTracking(this MeshBuilder builder)
    {
        return builder.ConfigureServices(services =>
        {
            services.AddScoped<ActivityLogBundler>(sp =>
            {
                var hub = sp.GetRequiredService<IMessageHub>();
                // Use IMeshStorage directly — NOT IMeshService which routes through
                // handlers and would trigger activity tracking again (infinite loop).
                var persistence = sp.GetRequiredService<IMeshStorage>();
                return new ActivityLogBundler(hub, async log =>
                {
                    var node = MeshNode.FromPath($"{log.HubPath}/_Activity/{log.Id}") with
                    {
                        NodeType = ActivityLogNodeType.NodeType,
                        Name = $"{log.Category}: {log.Messages.FirstOrDefault()?.Message ?? "Activity"}",
                        State = MeshNodeState.Active,
                        Content = log
                    };
                    await persistence.SaveNodeAsync(node);
                });
            });
            return services;
        });
    }
}
