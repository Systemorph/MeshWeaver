using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for <c>Feedback</c> nodes in the graph. Feedback nodes are standalone
/// (NOT satellites): one node per submission, filed by the <c>/feedback</c> skill into the dedicated
/// top-level <c>Feedback</c> space. Access is governed by that space's grants (the space grants the
/// <c>Public</c> subject a create-capable role, so any authenticated user can contribute) — there is
/// no bespoke satellite access rule.
/// </summary>
public static class FeedbackNodeType
{
    /// <summary>The NodeType value used to identify feedback nodes.</summary>
    public const string NodeType = "Feedback";

    /// <summary>Registers the built-in "Feedback" MeshNode on the mesh builder.</summary>
    public static TBuilder AddFeedbackType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>Creates the MeshNode type-definition for the Feedback node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Feedback",
        Icon = "📣",
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Feedback>())
    };
}
