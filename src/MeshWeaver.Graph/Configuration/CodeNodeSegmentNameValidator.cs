using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Keeps the batch bake's source-discovery union COMPLETE by construction: a <c>Code</c> node may
/// not be named after a code-table routing segment (<c>Source</c> / <c>Test</c>).
///
/// <para><b>The hole this closes (issue #1235, part 2).</b> Storage routes a node to the per-schema
/// <c>code</c> table when its PATH contains a <c>Source</c> or <c>Test</c> segment
/// (<see cref="SatelliteTableMapping.Defaults"/>). The batch bake's global fetch
/// (<c>NodeTypeBatchBake.GlobalCodeQueries</c>) reaches that table with two NAMESPACE patterns per
/// segment — <c>namespace:*/Source scope:subtree</c> widening to <c>*/Source</c> OR
/// <c>*/Source/*</c>. A node's namespace is its path minus its LAST segment, so those patterns see
/// every Code node whose <em>ancestor</em> is a <c>Source</c> folder — but not one whose OWN last
/// segment is the word: <c>X/Y/Source</c> has namespace <c>X/Y</c>, which contains no <c>Source</c>
/// segment at all. Such a node sits in the <c>code</c> table and is invisible to the entire pass.</para>
///
/// <para><b>Why it matters, and why widening is not the alternative.</b> The node is still
/// SELECTABLE by a per-type source query — <c>shared=@X/Y/Source</c> expands to
/// <c>path:X/Y/Source</c>, which <c>CodeQueryResolver.Matches</c> matches exactly, and
/// <c>NodeTypeBatchBake.IsInMemoryMatchable</c> classifies as servable from the global map. The
/// type would therefore resolve a PARTIAL source set: its other sources present, this one silently
/// absent, and Roslyn emitting a thoroughly convincing <c>CS0103</c> that the bake gate reads as an
/// image regression. That is exactly the #1216 production failure, and a partial set is strictly
/// worse than an empty one because the emptiness invariant cannot see it. Widening the union is not
/// available: no query in the language addresses "everything in the code table" — the only shape
/// that reaches it at all is a namespace pattern, which is precisely what this node evades. So the
/// claim is made true at the write boundary instead of being left as a latent hole.</para>
///
/// <para><b>Narrow by design.</b> Only <c>nodeType:Code</c>, and only the node's own last segment.
/// A <c>Source</c>/<c>Test</c> FOLDER (a Group node) is the normal, required layout and is
/// untouched; a Code node named <c>Test</c> INSIDE a Source folder
/// (<c>MyType/Source/Test</c>, namespace <c>MyType/Source</c>) is covered by the union and equally
/// untouched. The comparison is case-insensitive because the routing check
/// (<c>PathContainsSegment</c>) is: a node named <c>source</c> lands in the same table and would
/// have the same hole.</para>
/// </summary>
public sealed class CodeNodeSegmentNameValidator : INodeValidator
{
    /// <summary>Create + Update — the two surfaces that place a node at a path.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Create, NodeOperation.Update];

    /// <summary>
    /// The path segments that route a node to the <c>code</c> table, taken from the storage
    /// mapping itself so the rule cannot drift from the routing it protects.
    /// </summary>
    private static IEnumerable<string> CodeTableSegments =>
        SatelliteTableMapping.Defaults
            .Where(m => m.Table.Equals("code", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Segment);

    /// <summary>
    /// True when a Code node at <paramref name="path"/> would be INVISIBLE to the batch bake's
    /// global source fetch: the path routes to the <c>code</c> table, yet its NAMESPACE carries no
    /// code-table segment for the global namespace patterns to match on.
    ///
    /// <para>Because a namespace is the path minus its last segment, that is exactly the case where
    /// the ONLY code segment in the path is the last one. A Code node named <c>Test</c> that sits
    /// INSIDE a Source folder (<c>X/Y/Source/Test</c>, namespace <c>X/Y/Source</c>) is therefore
    /// perfectly fine — <c>*/Source</c> matches its namespace — and this predicate says so. Getting
    /// that wrong in the first draft is what the "everything the normal layout needs" test caught.</para>
    ///
    /// <para>Exposed so the batch bake's completeness claim can be asserted directly in tests.</para>
    /// </summary>
    /// <param name="path">The node's full mesh path.</param>
    public static bool IsInvisibleToGlobalCodeQueries(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        var isCodeSegment = (string s) =>
            CodeTableSegments.Any(seg => s.Equals(seg, StringComparison.OrdinalIgnoreCase));

        // Last segment is the routing segment, and nothing above it is — so the namespace has
        // nothing for `*/Source` / `*/Source/*` (or their Test twins) to bite on.
        return isCodeSegment(segments[^1])
               && !segments[..^1].Any(isCodeSegment);
    }

    /// <summary>
    /// Rejects a create/update that would place a <c>Code</c> node at a path whose last segment is
    /// a code-table routing segment.
    /// </summary>
    /// <param name="context">The validation context describing the node and operation.</param>
    /// <returns>An observable emitting the validation result.</returns>
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var node = context.Node;
        if (!string.Equals(node.NodeType, CodeNodeType.NodeType, StringComparison.OrdinalIgnoreCase)
            || !IsInvisibleToGlobalCodeQueries(node.Path))
            return Observable.Return(NodeValidationResult.Valid());

        return Observable.Return(NodeValidationResult.Invalid(
            $"A Code node may not be named '{node.Id}': "
            + $"'{string.Join("' / '", CodeTableSegments)}' are path segments that route content to the "
            + "code store, and a Code node carrying one as its OWN name is unreachable by the "
            + "source-discovery queries that collect code across partitions — it would silently drop "
            + "out of every NodeType's source set. Put the code INSIDE the folder instead "
            + $"(e.g. '{node.Path}/{node.Id}Helpers') or give the node a different name.",
            NodeRejectionReason.InvalidPath));
    }
}
