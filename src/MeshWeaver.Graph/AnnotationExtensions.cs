using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for adding tracked change support to a message hub.
/// Tracked changes are stored per-node in the _Tracking sub-partition.
/// Comments remain in _Comment via <see cref="CommentsExtensions"/>.
/// </summary>
public static class AnnotationExtensions
{
    /// <summary>
    /// The sub-partition name where tracked changes are stored.
    /// </summary>
    public const string TrackingPartition = "_Tracking";

    /// <summary>
    /// Marker type used to detect if tracking is enabled in a hub configuration.
    /// </summary>
    public record TrackingEnabled;

    /// <summary>
    /// Adds tracked change support to the message hub configuration.
    /// Registers the TrackedChange type under the _Tracking partition.
    /// Comments are handled separately by AddComments().
    /// </summary>
    public static MessageHubConfiguration AddTracking(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithType<TrackedChange>(nameof(TrackedChange))
            .Set(new TrackingEnabled())
            .AddData(data => data.WithDataSource(_ =>
                new MeshDataSource(Guid.NewGuid().AsString(), data.Workspace)
                    .WithType<TrackedChange>(TrackingPartition, nameof(TrackedChange))));
    }

    /// <summary>
    /// Checks if tracking is enabled in the configuration.
    /// </summary>
    public static bool HasTracking(this MessageHubConfiguration configuration)
        => configuration.Get<TrackingEnabled>() != null;
}
