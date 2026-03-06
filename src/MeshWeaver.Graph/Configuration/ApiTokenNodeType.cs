using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for ApiToken nodes in the graph.
/// ApiToken nodes are system-managed — excluded from search and create contexts.
/// </summary>
public static class ApiTokenNodeType
{
    public const string NodeType = "ApiToken";

    public static TBuilder AddApiTokenType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "API Token",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(ApiTokenNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddApiTokenViews()
            .AddMeshDataSource(source => source
                .WithContentType<ApiToken>())
    };
}
