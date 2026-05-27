using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for UserActivity nodes in the graph.
/// UserActivity nodes track user navigation/access to mesh nodes.
/// Stored as satellite MeshNodes under User/{userId}/_UserActivity/.
/// Access is delegated to the MainNode (User node) via SatelliteAccessRule.
/// </summary>
public static class UserActivityNodeType
{
    public const string NodeType = "UserActivity";

    public static TBuilder AddUserActivityType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SatelliteAccessRule(NodeType, sp.GetRequiredService<IMessageHub>()));
            return services;
        });
        return builder;
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "User Activity",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<UserActivityRecord>())
    };
}
