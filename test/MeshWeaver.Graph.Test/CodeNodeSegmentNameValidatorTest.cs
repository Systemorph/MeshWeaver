using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Closes issue #1235's SECOND hole by construction: a <c>Code</c> node whose own last path segment
/// is literally <c>Source</c> or <c>Test</c>.
///
/// <para><b>The hole.</b> Storage routes a node to the per-schema <c>code</c> table on a
/// <c>Source</c>/<c>Test</c> PATH SEGMENT. The batch bake's global fetch reaches that table with
/// NAMESPACE patterns (<c>*/Source</c> OR <c>*/Source/*</c>), and a node's namespace is its path
/// minus its LAST segment — so <c>X/Y/Source</c> has namespace <c>X/Y</c>, matches neither pattern,
/// and is invisible to the whole discovery pass while still living in the <c>code</c> table.</para>
///
/// <para><b>Why it is not merely theoretical.</b> Such a node remains SELECTABLE by a per-type
/// source query: <c>shared=@X/Y/Source</c> expands to <c>path:X/Y/Source</c>, which
/// <see cref="CodeQueryResolver.Matches"/> matches exactly and
/// <c>NodeTypeBatchBake.IsInMemoryMatchable</c> classifies as servable from the global map. The
/// type would then resolve a PARTIAL source set — the exact #1216 failure, and worse than an empty
/// one because the emptiness invariant cannot see it.</para>
///
/// <para><b>Why forbidding rather than widening.</b> No query in the language addresses "everything
/// in the code table"; the only shape that reaches it is a namespace pattern, which is precisely
/// what this node evades. So the union's completeness is secured at the write boundary instead.</para>
/// </summary>
public class CodeNodeSegmentNameValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Resolved from DI, not constructed — that is what proves <c>AddGraph()</c> actually REGISTERS
    /// the guard. A validator that is correct but unwired protects nothing.
    /// </summary>
    private CodeNodeSegmentNameValidator Guard() =>
        Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<CodeNodeSegmentNameValidator>()
            .First();

    private static NodeValidationContext Context(MeshNode node) =>
        new() { Operation = NodeOperation.Create, Node = node };

    private static MeshNode Code(string id, string ns) =>
        new(id, ns) { NodeType = CodeNodeType.NodeType, Name = id };

    [Theory]
    [InlineData("Source")]
    [InlineData("Test")]
    [InlineData("source")] // routing's segment check is case-insensitive, so the hole is too
    public async Task Validate_CodeNodeNamedAfterACodeTableSegment_IsRejected(string id)
    {
        var result = await Guard().Validate(Context(Code(id, "Acme/Model"))).Should().Emit();

        result.IsValid.Should().BeFalse(
            "its namespace would be 'Acme/Model', which matches neither `*/Source` nor `*/Source/*`, "
            + "so the node would sit in the code table invisible to every global source query");
        result.Reason.Should().Be(NodeRejectionReason.InvalidPath);
        result.ErrorMessage.Should().Contain(id);
    }

    /// <summary>
    /// The rule is NARROW on purpose — everything the normal layout depends on stays legal.
    /// </summary>
    [Theory]
    // The ordinary case: code inside a Source folder. Namespace ends in /Source → covered.
    [InlineData("Spine", "Acme/Model/Source", CodeNodeType.NodeType)]
    // Nested below it. Namespace contains /Source/ → covered by the widened pattern.
    [InlineData("Fixtures", "Acme/Model/Source/Helpers", CodeNodeType.NodeType)]
    // A Code node named 'Test' INSIDE a Source folder: namespace 'Acme/Model/Source' → covered.
    [InlineData("Test", "Acme/Model/Source", CodeNodeType.NodeType)]
    // A Source/Test FOLDER is a Group, not Code — the required layout, untouched by the rule.
    [InlineData("Source", "Acme/Model", "Group")]
    [InlineData("Test", "Acme/Model", "Markdown")]
    public async Task Validate_EverythingTheNormalLayoutNeeds_IsAllowed(string id, string ns, string nodeType)
    {
        var node = new MeshNode(id, ns) { NodeType = nodeType, Name = id };

        var result = await Guard().Validate(Context(node)).Should().Emit();

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 🚨 The invariant the guard exists to protect, asserted directly rather than implied: the
    /// forbidden name is EXACTLY the case the global namespace patterns miss. If someone widens the
    /// patterns later, this test says plainly which property they have to preserve.
    /// </summary>
    [Theory]
    // The hole: the ONLY code segment in the path is the last one, so the namespace has nothing
    // for the global patterns to bite on. Forbidden ⇒ the union stays complete.
    [InlineData("Acme/Model/Source", true)]
    [InlineData("Acme/Model/Test", true)]
    // Legal placements — all covered by a global pattern, none forbidden.
    [InlineData("Acme/Model/Source/Spine", false)]
    [InlineData("Acme/Model/Source/Helpers/Deep", false)]
    [InlineData("Acme/Model/Source/Test", false)]   // namespace 'Acme/Model/Source' matches */Source
    public void ForbiddenIsExactlyTheComplementOfWhatTheGlobalPatternsCover(string path, bool forbidden)
    {
        // Exactly what the two GlobalCodeQueries widen to
        // (QueryParser.WidenWildcardNamespacesToSubtree).
        string[] patterns = ["*/Source", "*/Source/*", "*/Test", "*/Test/*"];
        var ns = path[..path.LastIndexOf('/')];
        var covered = patterns.Any(p => QueryWildcard.IsMatch(ns, p));

        CodeNodeSegmentNameValidator.IsInvisibleToGlobalCodeQueries(path).Should().Be(forbidden);
        covered.Should().Be(!forbidden,
            "the guard must forbid PRECISELY the paths the global namespace patterns cannot reach — "
            + "narrower would leave the hole open, wider would ban legitimate content");
    }
}
