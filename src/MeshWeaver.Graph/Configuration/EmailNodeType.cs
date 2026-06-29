using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Email nodes — a persisted record of mail the portal received or sent.
/// System-managed (excluded from search/create autocomplete).
///
/// <para>Email nodes live in whichever partition owns them: inbound user mail under
/// <c>{username}/_Email/{id}</c>, inbound non-user mail under <c>Admin/Inbox/{id}</c>, outbound under
/// the relevant scope. They therefore use ordinary first-segment partition routing — <b>no</b> global
/// <c>nodeType:Email → Admin</c> rule (that would hide user mail).</para>
/// </summary>
public static class EmailNodeType
{
    /// <summary>The NodeType value used to identify email nodes.</summary>
    public const string NodeType = "Email";

    /// <summary>Per-user namespace segment for a user's stored mail: <c>{username}/_Email</c>.</summary>
    public const string UserEmailSegment = "_Email";

    /// <summary>Admin inbox namespace for non-user mail.</summary>
    public const string AdminInboxNamespace = "Admin/Inbox";

    /// <summary>Registers the built-in "Email" MeshNode on the mesh builder.</summary>
    public static TBuilder AddEmailType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the Email node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Email",
        Icon = "/static/NodeTypeIcons/message.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Email>())
    };
}
