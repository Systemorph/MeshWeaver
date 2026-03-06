using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

public static class UserActivityNodeType
{
    public const string NodeType = "UserActivity";

    public static TBuilder AddUserActivityType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "User Activity",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(UserActivityNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<UserActivityRecord>())
    };
}
