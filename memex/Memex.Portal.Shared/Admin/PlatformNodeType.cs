using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;

namespace Memex.Portal.Shared.Admin;

/// <summary>
/// Node type for platform administration nodes.
/// Provides a Splitter-based settings UI with tabs for
/// Overview, Auth Providers, and Administrators.
/// </summary>
public static class PlatformNodeType
{
    public const string NodeType = "Platform";

    public static TBuilder AddPlatformType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Platform",
        AssemblyLocation = typeof(PlatformNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddPlatformViews()
            .AddMeshDataSource()
    };
}
