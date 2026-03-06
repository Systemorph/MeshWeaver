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
    /// as MeshNodes through IMeshNodeFactory.
    /// </summary>
    public static MeshBuilder AddActivityTracking(this MeshBuilder builder)
    {
        return builder.ConfigureServices(services =>
        {
            services.AddScoped<ActivityLogBundler>(sp =>
            {
                var hub = sp.GetRequiredService<IMessageHub>();
                var nodeFactory = sp.GetRequiredService<IMeshNodeFactory>();
                return new ActivityLogBundler(hub, async log =>
                {
                    var node = MeshNode.FromPath($"{log.HubPath}/ActivityLog/{log.Id}") with
                    {
                        NodeType = ActivityLogNodeType.NodeType,
                        Name = $"{log.Category}: {log.Messages.FirstOrDefault()?.Message ?? "Activity"}",
                        State = MeshNodeState.Active,
                        Content = log
                    };
                    await nodeFactory.CreateNodeAsync(node);
                });
            });
            return services;
        });
    }
}
