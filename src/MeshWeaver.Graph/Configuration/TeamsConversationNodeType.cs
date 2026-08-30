using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration for <b>TeamsConversation</b> nodes — the link between an agent thread and the Microsoft
/// Teams conversation that spawned it (so the reply sender can post the agent's answer back into Teams).
/// System-managed: excluded from search/create; written by the Teams inbound processor.
/// </summary>
public static class TeamsConversationNodeType
{
    /// <summary>The NodeType value used to identify Teams-conversation link nodes.</summary>
    public const string NodeType = "TeamsConversation";

    /// <summary>Satellite segment under a thread: <c>{threadPath}/_TeamsConversation</c>.</summary>
    public const string Segment = "_TeamsConversation";

    /// <summary>Registers the built-in "TeamsConversation" MeshNode on the mesh builder.</summary>
    public static TBuilder AddTeamsConversationType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        // 🚨 The discriminator has to be known to EVERY hub, not only to the per-node hub the
        // data source above configures (MeshWeaver#2729). A reader elsewhere in the mesh whose
        // TypeRegistry lacks it gets a raw JsonElement and therefore a SILENT null: the value
        // renders empty and reactive waits time out, with no exception anywhere to grep for.
        builder.ConfigureHub(config => config.WithType<TeamsConversation>(nameof(TeamsConversation)));
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the TeamsConversation node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Teams Conversation",
        Icon = "/static/NodeTypeIcons/chat.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<TeamsConversation>())
    };
}
