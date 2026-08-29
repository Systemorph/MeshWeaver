using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Mesh = MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// The platform-side DECLARATION of the <c>Comment</c> satellite node type. The comment
/// implementation — views, the create handler, the composer — ships in the
/// <c>MeshWeaver.Markdown.Collaboration</c> module; what stays here is what makes a comment
/// satellite EXIST and READ on any mesh.
///
/// <para>🚨 The declaration cannot ride the module, and CI proved it rather than review: this
/// repository ships <c>_Comment</c> content of its own (<c>samples/Graph/Data/FutuRe/…</c> and
/// <c>Doc/DataMesh/CollaborativeEditing/…</c>), and the content gate installs it on a host that
/// cannot reference a plugins-repo module. With the type undeclared the install failed with
/// <c>NodeType 'Comment' is not registered</c> — the platform could no longer install its own
/// content. The same is true of any deployment holding comments written before the module was
/// delisted.</para>
///
/// <para>So the split is: DECLARATION plus the data source that types the satellite here — the
/// <c>Comment</c> record is <c>MeshWeaver.Mesh.Contract</c>'s — and every VIEW arrives as a
/// node-type-keyed contribution the module registers.</para>
/// </summary>
public static class CommentNodeType
{
    /// <summary>The NodeType value identifying comment nodes.</summary>
    public const string NodeType = "Comment";

    /// <summary>
    /// When true, only the comment author can edit the comment text. Other users can still view
    /// the comment but cannot switch to edit mode.
    /// </summary>
    public const bool AuthorEditOnly = true;

    /// <summary>
    /// Registers the built-in <c>Comment</c> MeshNode on the mesh builder.
    /// </summary>
    /// <typeparam name="TBuilder">The concrete mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder to register on.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder AddCommentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        // The SatelliteAccessRule is registered unconditionally in GraphConfigurationExtensions —
        // a _Comment satellite must keep delegating its permissions to its MainNode whether or not
        // the collaboration module is installed.
        return builder;
    }

    /// <summary>
    /// Creates the MeshNode definition for the <c>Comment</c> node type: the satellite shape, the
    /// typed data source, and the seam through which the module contributes the views.
    /// </summary>
    /// <returns>The node definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Comment",
        Icon = "/static/NodeTypeIcons/comment.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<Mesh.Comment>())
            .ApplyNodeHubContributions(NodeType)
    };
}
