using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Activity;

/// <summary>
/// Extension methods for configuring activity tracking.
/// </summary>
public static class ActivityTrackingExtensions
{
    /// <summary>
    /// Checks if a path is within a satellite partition (contains /_ segments
    /// — e.g., /_Thread/, /_Comment/, /_Access/, /_activity/).
    /// </summary>
    private static bool IsSatellitePath(string path) => path.Contains("/_");

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
                // Use IMeshStorage directly — NOT IMeshNodePersistence which routes through
                // handlers and would trigger activity tracking again (infinite loop).
                var persistence = sp.GetRequiredService<IMeshStorage>();
                return new ActivityLogBundler(hub, log =>
                {
                    // Skip activity tracking for satellite node hubs.
                    // Satellite paths contain /_X/ segments (e.g., /_Thread/, /_Comment/, /_Access/).
                    // These generate too many updates during streaming and shouldn't have activity logs.
                    if (log.HubPath != null && IsSatellitePath(log.HubPath))
                        return;

                    var lf = hub.ServiceProvider.GetService<ILoggerFactory>();
                    var pathLogger = lf?.CreateLogger("ActivityLogBundler");

                    // Reactive composition end-to-end — no await, no .ToTask. Bridge to the
                    // observable hubNode read via SelectMany, then SaveNode chained after.
                    // The callback is sync so onFlush can drop the deferred Subscribe; any
                    // hub-touching work happens off the bundler's timer thread.
                    var hubNodeObs = log.HubPath != null
                        ? persistence.GetNode(log.HubPath).Take(1)
                        : Observable.Return<MeshNode?>(null);

                    hubNodeObs
                        .SelectMany(hubNode =>
                        {
                            if (hubNode != null && hubNode.MainNode != hubNode.Path)
                                return Observable.Empty<MeshNode>();
                            var node = MeshNode.FromPath($"{log.HubPath}/_activity/{log.Id}") with
                            {
                                NodeType = ActivityNodeType.NodeType,
                                Name = $"{log.Category}: {log.Messages.FirstOrDefault()?.Message ?? "Activity"}",
                                MainNode = log.HubPath!,
                                State = MeshNodeState.Active,
                                Content = log
                            };
                            return persistence.SaveNode(node);
                        })
                        .Subscribe(
                            _ => { },
                            ex => pathLogger?.LogWarning(ex, "Failed to persist activity log for hub {Path}", log.HubPath));
                });
            });
            return services;
        });
    }
}
