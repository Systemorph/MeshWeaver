using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for the GlobalSettings node type.
/// A single node of this type at path <c>_Setting</c> hosts the platform-wide settings page.
/// </summary>
public static class GlobalSettingsNodeType
{
    /// <summary>
    /// The NodeType value used to identify the global settings node.
    /// </summary>
    public const string NodeType = "GlobalSettings";

    /// <summary>
    /// Well-known path for the global settings node.
    /// </summary>
    public const string SettingsPath = "_Setting";

    /// <summary>
    /// Registers the built-in "GlobalSettings" MeshNode on the mesh builder
    /// and creates the singleton _Setting node.
    /// </summary>
    public static TBuilder AddGlobalSettingsType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);

        // Create the well-known _Setting node
        builder.AddMeshNodes(new MeshNode(SettingsPath)
        {
            NodeType = NodeType,
            Name = "Settings",
            State = MeshNodeState.Active,
            ExcludeFromContext = new HashSet<string> { "search", "create" },
        });

        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the GlobalSettings node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Global Settings",
        Icon = "/static/NodeTypeIcons/settings.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(GlobalSettingsNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddDefaultGlobalSettingsMenuItems()
            .AddLayout(layout => layout
                .WithDefaultArea(GlobalSettingsLayoutArea.GlobalSettingsArea)
                .WithView(GlobalSettingsLayoutArea.GlobalSettingsArea, GlobalSettingsLayoutArea.GlobalSettings))
    };
}
