using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The built-in <c>WhatsNew</c> node type — a release-note entry (#2539).
///
/// <para><b>Why it exists as a TYPE rather than a folder convention.</b> The What's New feed was a
/// single <c>path:Doc/WhatsNew scope:children</c> listing, and that path lives only in the platform
/// repository. A satellite — Plugins, Education, Reinsurance, SocialMedia, Memex — had no route to
/// file an entry from its own PR, so a user-noticeable fix landing there was simply absent from the
/// changelog. Keying the second listing lane on the node TYPE lets an entry live anywhere in any
/// repo's tree and still reach the one feed, so authorship stays with the change.</para>
///
/// <para>🚨 Registering the type is not ceremony: node creation REFUSES an unregistered node type
/// ("NodeType 'WhatsNew' is not registered"), so without this a satellite could not author an entry
/// at all — which the test for the feature caught immediately.</para>
///
/// <para>The body is markdown and the front matter carries what the feed renders from
/// (<c>Name</c>, <c>Category</c>, <c>Description</c>, <c>Icon</c>, <c>Order</c>) — the same shape
/// the platform's own entries under <c>Doc/WhatsNew</c> use, which keep working unchanged through
/// the other lane.</para>
/// </summary>
public static class WhatsNewNodeType
{
    /// <summary>The NodeType value identifying a release-note entry.</summary>
    public const string NodeType = "WhatsNew";

    /// <summary>Registers the built-in "WhatsNew" MeshNode on the mesh builder.</summary>
    public static TBuilder AddWhatsNewType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>Creates the MeshNode definition for the What's New entry node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "What's New entry",
    };
}
