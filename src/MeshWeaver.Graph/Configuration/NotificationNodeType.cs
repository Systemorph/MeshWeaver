using MeshWeaver.Data;
using MeshWeaver.Graph.Logon;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Notification nodes in the graph.
/// Notification nodes are system-generated — excluded from search and create contexts.
/// </summary>
public static class NotificationNodeType
{
    /// <summary>
    /// The NodeType value used to identify notification nodes.
    /// </summary>
    public const string NodeType = "Notification";

    /// <summary>
    /// Registers the built-in "Notification" MeshNode on the mesh builder, together with the
    /// retention policy and the pass that applies it.
    /// </summary>
    public static TBuilder AddNotificationType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureServices(services => services
            // Mesh-scoped singleton, read once from the host's configuration — the same shape
            // AddAssemblyCacheRetention uses, so a deployment tunes retention in its chart rather
            // than against a constant buried in code (Doc/Architecture/NotificationRetention).
            .AddSingleton(sp => NotificationRetention.FromConfiguration(sp.GetService<IConfiguration>()))
            // Registered HERE and not beside the other logon actions: the pass exists because
            // notifications accumulate, and if the notification type ever stops shipping, so should
            // its retention. AddGraph calls both, so the runner is always present to run it.
            .AddSingleton<ILogonAction, NotificationRetentionLogonAction>());
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Notification node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Notification",
        Icon = "/static/NodeTypeIcons/bell.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .ApplyNodeHubContributions(NodeType)
            .AddMeshDataSource(source => source
                .WithContentType<Notification>())
    };
}
