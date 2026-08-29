using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// The platform-side GATE for document comments — the compiled residue that stays in
/// <c>MeshWeaver.Graph</c> when the comment implementation ships as the
/// <c>MeshWeaver.Markdown.Collaboration</c> module.
///
/// <para>🚨 This is deliberately not part of the module. <c>HasComments()</c> is called from
/// IN-MESH source (a node's layout area asking whether to offer a Comments menu entry), which
/// compiles at RUNTIME against whatever assemblies the mesh has loaded. Were the gate to ride the
/// module, delisting the module would turn every such call into a compile error in code no
/// <c>dotnet build</c> can see — the failure mode AGENTS.md calls out for deleted public surface.
/// Here the call keeps compiling and simply answers <c>false</c>.</para>
/// </summary>
public static class CommentsGate
{
    /// <summary>
    /// The <c>Comment</c> NodeType discriminator. Platform-side because satellite ROUTING and
    /// permission delegation must keep working on a mesh without the collaboration module —
    /// the module's <c>CommentNodeType.NodeType</c> is the same string for in-mesh callers.
    /// </summary>
    public const string CommentNodeTypeName = "Comment";

    /// <summary>
    /// The legacy <c>TrackedChange</c> NodeType discriminator. Same reason as
    /// <see cref="CommentNodeTypeName"/>: <c>_Tracking</c> satellites written by older builds must
    /// stay readable and permission-delegating without the module.
    /// </summary>
    public const string TrackedChangeNodeTypeName = "TrackedChange";

    /// <summary>
    /// Marker set on a node hub configuration once the collaboration module has registered
    /// comments on it. Read through <see cref="HasComments"/>.
    /// </summary>
    public record CommentsEnabled;

    /// <summary>
    /// Marks comments as registered on this node hub. Called by the collaboration module's
    /// <c>AddComments()</c>; nothing in the platform sets it.
    /// </summary>
    /// <param name="configuration">The node hub configuration being built.</param>
    /// <returns>The configuration, for chaining.</returns>
    public static MessageHubConfiguration MarkCommentsEnabled(this MessageHubConfiguration configuration)
        => configuration.Set(new CommentsEnabled());

    /// <summary>
    /// Whether comments are registered on this hub — false on every mesh that does not carry the
    /// collaboration module, which is what suppresses the inline comments section and the
    /// Comments menu entry.
    /// </summary>
    /// <param name="configuration">The hub configuration to test.</param>
    /// <returns><c>true</c> when the collaboration module registered comments on this hub.</returns>
    public static bool HasComments(this MessageHubConfiguration configuration)
        => configuration.Get<CommentsEnabled>() != null;
}
