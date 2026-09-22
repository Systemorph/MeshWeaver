namespace MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 <b>"A node exists at that path" and "a NodeType is REGISTERED at that path" are different
/// questions, and they diverge the moment two things want one name.</b> This seam answers the
/// second one for the boundaries that can only ask the first.
///
/// <para><b>Why it is an interface rather than a static helper.</b> The test needs
/// <c>NodeTypeDefinition</c>, which lives in <c>MeshWeaver.Graph.Contract</c> — and that project
/// references <c>MeshWeaver.Mesh.Contract</c>, never the other way round. So the write boundaries
/// in <c>MeshExtensions</c> and <see cref="NodeTypeResolution"/> cannot name the record they need
/// to test for. The layer that OWNS the record registers the implementation; the layer that owns
/// the rule consumes it. There is exactly one implementation.</para>
///
/// <para>🚨 <b>The test is ONE-SIDED and must stay that way</b> — it may only ever say
/// "definitely NOT a declaration", never "definitely is one". A false positive refuses a write
/// that would have worked, and the mesh has no way back from that; a false negative merely leaves
/// today's behaviour. So every uncertain shape clears, which is why this returns a description
/// rather than a <c>bool</c>: a non-answer and a clean answer are the same value (<c>null</c>),
/// and only a conviction carries words. A probe that is not registered at all convicts nothing,
/// for the same reason.</para>
///
/// <para>The incident: a Store plugin installs its root at the bare path <c>Feedback</c> while its
/// NodeType declaration is <c>Feedback/Feedback</c>. An instance that names the bare
/// <c>Feedback</c> passed the write boundary on the PLUGIN ROOT — persisted, and then refused by
/// activation, which applies this same test. See
/// <c>Doc/Architecture/DanglingNodeTypes</c>.</para>
/// </summary>
public interface INodeTypeDeclarationProbe
{
    /// <summary>
    /// A phrase naming <paramref name="candidate"/> when it is PROVABLY not a NodeType
    /// declaration — e.g. <c>'Feedback' is a 'Store/Plugin' node</c> — and <c>null</c> in every
    /// other case, including every case the probe cannot decide.
    /// </summary>
    /// <param name="candidate">The node occupying the NodeType's path.</param>
    /// <returns>The occupant description, or <c>null</c> when nothing is proven.</returns>
    string? DescribeNonDeclaration(MeshNode candidate);
}
