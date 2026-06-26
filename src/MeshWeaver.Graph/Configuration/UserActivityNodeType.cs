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
    /// <summary>The node-type identifier for UserActivity nodes.</summary>
    public const string NodeType = "UserActivity";

    /// <summary>
    /// Registers the UserActivity node type on the mesh builder, excluding it from
    /// autocomplete context and wiring up its satellite access rule (access is delegated
    /// to the owning User node).
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder to register the node type on.</param>
    /// <returns>The same builder, to allow fluent chaining.</returns>
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

    /// <summary>
    /// Builds the MeshNode definition for the UserActivity node type, including its
    /// satellite-storage routing to the <c>user_activities</c> table.
    /// </summary>
    /// <returns>The UserActivity node-type definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "User Activity",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        // Declarative storage routing: UserActivity instances persist to the
        // `user_activities` satellite table within their owning partition's schema
        // (the single-sourced replacement for the central _UserActivity→user_activities
        // path-suffix map). See Doc/Architecture/PartitionStorageRouting.md.
        Content = new NodeTypeDefinition { StorageTable = "user_activities" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<UserActivityRecord>())
    };
}
