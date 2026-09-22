using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The one implementation of <see cref="INodeTypeDeclarationProbe"/> — the layer that owns
/// <see cref="NodeTypeDefinition"/> answering the question the write boundaries in
/// <c>MeshWeaver.Mesh.Contract</c> cannot phrase (see that interface for the layering).
///
/// <para>🚨 <b>The test is deliberately one-sided</b>: it may only ever say "definitely not a
/// declaration", never "definitely is one". A false positive refuses a write that would have
/// worked, so every uncertain shape falls through — which is why BOTH of these must hold:</para>
/// <list type="bullet">
///   <item>the node DECLARES a NodeType that is not <see cref="MeshNode.NodeTypePath"/> — a
///     null/empty <c>NodeType</c> proves nothing (several built-in declarations, e.g. Role and
///     Group, leave it unset), and one that says <c>NodeType</c> is a declaration by
///     construction;</item>
///   <item>and its content does not convert to a <see cref="NodeTypeDefinition"/>. Untyped JSON
///     deserialises into one happily, so this branch is only reached by content typed as something
///     else entirely — <c>PluginContent</c> in the incident.</item>
/// </list>
///
/// <para>🚨 <b>No logger reaches <c>ContentAs</c> here, on purpose.</b> A non-convertible
/// candidate is this method's NORMAL INPUT — it is what the probe exists to find — so reporting
/// it as a conversion fault publishes a bare, subject-free
/// <c>As&lt;NodeTypeDefinition&gt; for Feedback: value is PluginContent</c> line at <c>Error</c>
/// that names neither the instance nor the reason. That line WAS the only evidence
/// Systemorph/MeshWeaver#2231 ever had, and it is what this replaces: the caller reports the
/// collision once, with both sides named.</para>
/// </summary>
/// <param name="hub">The hub asking. Its <see cref="IMessageHub.JsonSerializerOptions"/> carry the
/// TypeRegistry that resolves a stored <c>$type</c>, which is why this is scoped to a hub rather
/// than shared: a probe holding another hub's registry answers about another hub's world.</param>
internal sealed class NodeTypeDeclarationProbe(IMessageHub hub) : INodeTypeDeclarationProbe
{
    /// <inheritdoc />
    public string? DescribeNonDeclaration(MeshNode candidate) =>
        IsProvablyNotADeclaration(candidate, hub.JsonSerializerOptions)
            ? $"'{candidate.Path}' is a '{candidate.NodeType}' node"
            : null;

    /// <summary>
    /// The predicate itself, static so the activation boundary
    /// (<c>NodeTypeEnrichmentHelpers.ProbeCollision</c>) applies the SAME one rather than a second
    /// copy — two copies of this predicate is how a write that accepts and an activation that
    /// refuses drift apart, which is the defect being fixed.
    /// </summary>
    /// <param name="candidate">The node occupying the NodeType's path.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <returns>True only when the candidate is PROVABLY not a NodeType declaration.</returns>
    internal static bool IsProvablyNotADeclaration(
        MeshNode candidate, JsonSerializerOptions options) =>
        !string.IsNullOrEmpty(candidate.NodeType)
        && !string.Equals(candidate.NodeType, MeshNode.NodeTypePath, StringComparison.OrdinalIgnoreCase)
        && candidate.ContentAs<NodeTypeDefinition>(options) is null;
}
