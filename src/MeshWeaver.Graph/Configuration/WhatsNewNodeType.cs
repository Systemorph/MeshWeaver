using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

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
        builder.AddMeshNodes(CreateMeshNode(), CreatePolicy());
        return builder;
    }

    /// <summary>
    /// The partition policy for <c>WhatsNew</c>: readable by everyone, writable by nobody.
    ///
    /// <para>🚨 The registration is a TOP-LEVEL node, so it is its own partition, and a partition
    /// with no policy is private. A declared node is row-filtered like a stored one, so without this
    /// policy every viewer but an admin lost the type from a query — while release notes are written
    /// for everyone. Read access is DECLARED here, the way <c>Doc</c> and <c>_Setting</c> declare
    /// theirs, never granted by an exemption in the row filter.</para>
    /// </summary>
    /// <returns>The read-only, publicly readable <c>WhatsNew/_Policy</c> node.</returns>
    // Satellite(): MainNode = the partition root, not the node's own path — see #2383.
    public static MeshNode CreatePolicy() =>
        MeshNode.Satellite("_Policy", NodeType) with
        {
            NodeType = PartitionAccessPolicyNodeType.NodeType,
            Name = "Access Policy",
            Content = new PartitionAccessPolicy
            {
                PublicRead = true,
                Create = false,
                Update = false,
                Delete = false,
                Comment = false,
                Thread = false
            }
        };

    /// <summary>Creates the MeshNode definition for the What's New entry node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "What's New entry",
    };
}
