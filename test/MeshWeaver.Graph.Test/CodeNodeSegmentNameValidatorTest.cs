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
///
/// <para>🚨 The validator is STATELESS — no hub, no DI, no mesh — so these cases construct it
/// directly. Deriving the whole class from <c>MonolithMeshTestBase</c> would spin up a full mesh
/// PER CASE; at 13 cases that added ~29 s to MeshWeaver.Graph.Test on a CI shard and pushed a
/// neighbouring 30 s compile-probe assertion over its limit. The one case that genuinely needs a
/// mesh — proving <c>AddGraph()</c> REGISTERS the guard — lives in
/// <see cref="CodeNodeSegmentNameValidatorRegistrationTest"/> below.</para>
/// </summary>
public class CodeNodeSegmentNameValidatorTest
{
    private static readonly CodeNodeSegmentNameValidator Guard = new();

    private static NodeValidationContext Context(MeshNode node) =>
        new() { Operation = NodeOperation.Create, Node = node };

    private static async Task<NodeValidationResult> Validate(string id, string ns, string nodeType) =>
        await Guard.Validate(Context(new MeshNode(id, ns) { NodeType = nodeType, Name = id }))
            .Should().Emit();

    [Theory]
    [InlineData("Source")]
    [InlineData("Test")]
    [InlineData("source")] // routing's segment check is case-insensitive, so the hole is too
    public async Task CodeNodeNamedAfterACodeTableSegment_IsRejected(string id)
    {
        var result = await Validate(id, "Acme/Model", CodeNodeType.NodeType);

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
    public async Task EverythingTheNormalLayoutNeeds_IsAllowed(string id, string ns, string nodeType)
        => (await Validate(id, ns, nodeType)).IsValid.Should().BeTrue();

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

/// <summary>
/// The ONE case that needs a real mesh: a validator that is correct but unwired protects nothing,
/// so this proves <c>AddGraph()</c> actually registers the guard — and that the registered instance
/// rejects the forbidden name. Deliberately a single test (one mesh construction); every other case
/// is pure logic in <see cref="CodeNodeSegmentNameValidatorTest"/>.
/// </summary>
public class CodeNodeSegmentNameValidatorRegistrationTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 30000)]
    public async Task AddGraph_RegistersTheGuard_AndItRejectsTheForbiddenName()
    {
        var guard = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<CodeNodeSegmentNameValidator>()
            .SingleOrDefault();

        guard.Should().NotBeNull("AddGraph() must register the guard — otherwise it protects nothing");

        var node = new MeshNode("Source", "Acme/Model")
        {
            NodeType = CodeNodeType.NodeType,
            Name = "Source"
        };
        var result = await guard!
            .Validate(new NodeValidationContext { Operation = NodeOperation.Create, Node = node })
            .Should().Emit();

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(NodeRejectionReason.InvalidPath);
    }
}
