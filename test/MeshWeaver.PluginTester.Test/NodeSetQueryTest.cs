#pragma warning disable CS1591

using System.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// Unit pins for the mesh-free source-query evaluator (#1763). The
/// <see cref="BakeEquivalenceTest"/> proves the two bake paths agree end-to-end; these pin the
/// individual rules that agreement rests on, so a regression names the RULE instead of surfacing
/// as "the bundles differ".
/// </summary>
public class NodeSetQueryTest
{
    private static readonly NodeSet Nodes = NodeSet.Create(
    [
        Code("Type"),
        Code("Type/Source"),
        Code("Type/Source/A"),
        Code("Type/Source/Sub/B"),
        Code("Other/Source/C"),
        new MeshNode("Scoped", "Type/Source") { NodeType = "Scope" },
    ]);

    private static MeshNode Code(string path)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(
            slash < 0 ? path : path[(slash + 1)..],
            slash < 0 ? "" : path[..slash])
        {
            NodeType = CodeConventions.CodeNodeType,
        };
    }

    private static string[] Match(params string[] sources) => Nodes
        .ResolveSources(sources, tests: [], "Type")
        .Sources.Select(n => n.Path).ToArray();

    /// <summary>
    /// 🚨 The rule that is easiest to get wrong and costs the most: <c>namespace:X scope:subtree</c>
    /// DEGRADES to descendants, because a namespace names a namespace and never the node at X. The
    /// DEFAULT source query is exactly this shape, so a resolver that kept "subtree" would fold a
    /// node literally at <c>{Type}/Source</c> into every compile.
    /// </summary>
    [Fact]
    public void NamespaceSubtree_ExcludesTheNodeAtTheNamespaceItself()
        => Assert.Equal(
            ["Type/Source/A", "Type/Source/Sub/B"],
            Match("namespace:Source scope:subtree"));

    /// <summary><c>path:X scope:subtree</c>, by contrast, KEEPS self.</summary>
    [Fact]
    public void PathSubtree_KeepsSelf()
        => Assert.Equal(
            ["Type/Source", "Type/Source/A", "Type/Source/Sub/B"],
            Match("path:Type/Source scope:subtree"));

    /// <summary><c>namespace:X</c> with no scope is CHILDREN, not the subtree.</summary>
    [Fact]
    public void BareNamespace_IsDirectChildrenOnly()
        => Assert.Equal(["Type/Source/A"], Match("namespace:Source"));

    /// <summary>
    /// The <c>@</c> shorthand expands to an exact match PLUS a subtree walk, and it crosses package
    /// boundaries — the <c>shared=</c> case a per-package resolver would silently answer with
    /// nothing.
    /// </summary>
    [Fact]
    public void AtShorthand_ResolvesExactPlusSubtree_AcrossPackages()
        => Assert.Equal(["Other/Source/C"], Match("shared=@Other/Source"));

    /// <summary>
    /// The implicit <c>nodeType:Code</c> filter excludes a <c>// NodeType: Scope</c>-headered
    /// source, exactly as the mesh query does.
    /// </summary>
    [Fact]
    public void ImplicitCodeFilter_ExcludesForeignNodeTypes()
        => Assert.DoesNotContain("Type/Source/Scoped", Match("namespace:Source scope:subtree"));

    /// <summary>An explicitly-typed query is honoured as authored — the filter is a DEFAULT, not
    /// an override.</summary>
    [Fact]
    public void ExplicitNodeType_IsHonoured()
        => Assert.Equal(
            ["Type/Source/Scoped"],
            Match("namespace:Source scope:subtree nodeType:Scope"));

    /// <summary>
    /// 🚨 THE FAIL-SAFE. A query this evaluator cannot answer must make the resolution
    /// UNESTABLISHED — never silently match less. A short source set compiles into completely
    /// genuine-looking CS0246s about code that is fine (#1218), and the resulting bundle is adopted
    /// by every portal without a murmur.
    /// </summary>
    [Theory]
    [InlineData("laptop nodeType:Code")]              // free text → vector search on a real mesh
    [InlineData("namespace:*/Source nodeType:Code")]  // wildcard
    [InlineData("path:A|B nodeType:Code")]            // alternation
    [InlineData("state:Active nodeType:Code")]        // a selector outside the grammar
    [InlineData("namespace:Source scope:ancestors")]  // a scope walk with no source meaning
    public void AnUnsupportedQuery_MakesTheResolutionUnestablished(string query)
    {
        var resolution = Nodes.ResolveSources([query], tests: [], "Type");
        Assert.False(resolution.IsEstablished);
        Assert.NotNull(resolution.UnestablishedReason);
    }

    /// <summary>A supported query set is established, so the caller may compile it.</summary>
    [Fact]
    public void ASupportedQuerySet_IsEstablished()
        => Assert.True(Nodes.ResolveSources(null, null, "Type").IsEstablished);

    /// <summary>
    /// The assembly name is part of the emitted bytes, so the tree bake and the runtime must derive
    /// it identically — that is why the rule moved into the toolchain (#1763).
    /// </summary>
    [Theory]
    [InlineData("Widget/Thing", "Widget_Thing")]
    [InlineData("A//B", "A_B")]
    [InlineData("1Thing", "Node_1Thing")]
    [InlineData("/Leading", "Leading")]
    public void SanitizeNodeName_MatchesTheRuntimeRule(string path, string expected)
        => Assert.Equal(expected, CodeConventions.SanitizeNodeName(path));
}
