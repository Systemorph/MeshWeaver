using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The <b>CompletionMemory</b> node — a user's completion acceptance history, one singleton node
/// per user at <c>{userId}/_Settings/Completions</c> (see
/// <see cref="CompletionMemoryStore.PathFor"/>). System-managed and user-owned, exactly like the
/// notification settings next to it: excluded from search / create / autocomplete, written only by
/// the editor's acceptance path.
/// </summary>
public static class CompletionMemoryNodeType
{
    /// <summary>The NodeType value used to identify completion-memory nodes.</summary>
    public const string NodeType = "CompletionMemory";

    /// <summary>Registers the built-in "CompletionMemory" MeshNode on the mesh builder.</summary>
    public static TBuilder AddCompletionMemoryType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<CompletionMemory>(nameof(CompletionMemory)));
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the CompletionMemory node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Completion Memory",
        Icon = "/static/NodeTypeIcons/code.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<CompletionMemory>())
    };
}
