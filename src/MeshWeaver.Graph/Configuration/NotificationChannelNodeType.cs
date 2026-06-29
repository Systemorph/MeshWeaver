using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration for <b>NotificationChannel</b> nodes — a user's configured delivery channels
/// (in-app, email, Teams, …). The notification triage agent routes notifications to the channels a
/// user's <see cref="NotificationRule"/>s select. User-creatable and owned: stored under
/// <c>{username}/_NotificationChannel/{id}</c>.
/// </summary>
public static class NotificationChannelNodeType
{
    /// <summary>The NodeType value used to identify notification-channel nodes.</summary>
    public const string NodeType = "NotificationChannel";

    /// <summary>Per-user namespace segment for a user's channels: <c>{username}/_NotificationChannel</c>.</summary>
    public const string UserSegment = "_NotificationChannel";

    /// <summary>Registers the built-in "NotificationChannel" MeshNode on the mesh builder.</summary>
    public static TBuilder AddNotificationChannelType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the NotificationChannel node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Notification Channel",
        Icon = "/static/NodeTypeIcons/bell.svg",
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<NotificationChannel>())
    };
}
