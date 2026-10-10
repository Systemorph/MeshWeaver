using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary><see cref="IMeshNodeQueryMatcher"/> over the in-memory <see cref="QueryEvaluator"/> —
/// the predicate the static and storage-adapter providers already apply.</summary>
public sealed class QueryEvaluatorMatcher : IMeshNodeQueryMatcher
{
    private readonly QueryEvaluator evaluator = new();

    /// <inheritdoc />
    public bool Matches(MeshNode node, ParsedQuery query) => evaluator.Matches(node, query);
}
