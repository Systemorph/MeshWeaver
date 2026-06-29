using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration for <b>NotificationRule</b> nodes — user-authored, plain-English (or lightly
/// structured) rules that the notification triage agent uses to decide whether a notification is
/// worth sending and to which channel(s). Unlike system-managed types (Email, Notification), these
/// are <b>user-creatable</b>: a user authors them under their own namespace
/// (<c>{username}/_NotificationRule/{id}</c>), so the type stays in search/create contexts.
/// </summary>
public static class NotificationRuleNodeType
{
    /// <summary>The NodeType value used to identify notification-rule nodes.</summary>
    public const string NodeType = "NotificationRule";

    /// <summary>Per-user namespace segment for a user's rules: <c>{username}/_NotificationRule</c>.</summary>
    public const string UserSegment = "_NotificationRule";

    /// <summary>Registers the built-in "NotificationRule" MeshNode on the mesh builder.</summary>
    public static TBuilder AddNotificationRuleType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the NotificationRule node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Notification Rule",
        Icon = "/static/NodeTypeIcons/bell.svg",
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<NotificationRule>())
    };
}
