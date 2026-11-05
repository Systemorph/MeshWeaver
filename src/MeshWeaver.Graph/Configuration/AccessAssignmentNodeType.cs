using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for AccessAssignment nodes in the graph.
/// AccessAssignment nodes grant or deny a role to a subject at a specific scope.
/// </summary>
public static class AccessAssignmentNodeType
{
    /// <summary>
    /// The NodeType value used to identify access assignment nodes.
    /// </summary>
    public const string NodeType = "AccessAssignment";

    /// <summary>
    /// Registers the built-in "AccessAssignment" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddAccessAssignmentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        // The subject pickers' users half (Auth/_GrantSubjects/{scope}) — listed only to a viewer
        // who may grant at the scope (policy user-listing-needs-grant-right). A plain AddSingleton,
        // never TryAdd: providers are an enumerable fan-in.
        builder.ConfigureServices(services => services
            .AddSingleton<IMeshQueryProvider>(sp => new Security.GrantSubjectsQueryProvider(sp)));
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the AccessAssignment node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Access Assignment",
        Icon = "/static/NodeTypeIcons/shield.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .ApplyNodeHubContributions(NodeType)
            .AddMeshDataSource(source => source
                .WithContentType<AccessAssignment>())
    };
}
