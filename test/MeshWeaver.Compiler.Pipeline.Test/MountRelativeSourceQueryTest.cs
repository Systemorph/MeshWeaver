using System.Collections.Generic;
using System.Linq;
using Xunit;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A NodeType's cross-type source reference is authored MOUNT-RELATIVE, and
/// <see cref="CodeQueryResolver.Expand"/> must therefore also ask for the spelling the MOUNT uses
/// — issue #4813.
///
/// <para><b>The defect these cases pin.</b> <c>samples/Graph/Data/Northwind/Product.json</c>
/// declares <c>shared=@Northwind/AnalyticsCatalog/Source/Supplier</c>. That is the right spelling
/// at a root mount (the Monolith, where the type is <c>Northwind/Product</c>) and the wrong one in
/// a statically-imported partition, where the same nodes are served under a prefix
/// (<c>MeshWeaver/samples/Graph/Data/Northwind/…</c>). Resolved verbatim there the entry matches
/// nothing — while the type's OWN <c>namespace:Source scope:subtree</c> IS rebased on <c>$self</c>
/// and does match, so the merged set is non-empty and neither <c>SourceSnapshot</c>'s emptiness
/// check nor <c>PreWarmStatus.NoSources</c> can fire. Roslyn gets a set short of the sibling entity
/// sources and reports <c>CS0246: The type or namespace name 'Supplier' could not be found</c>
/// about code that is fine. Measured on memex-cloud 2026-09-19/20: that exact failure on every pod
/// that attempted the compile, with source discovery reporting 2 matched Code nodes.</para>
///
/// <para><b>Why it is one rule, not two.</b> <c>NodeCompileShaping.AnchorIncludePath</c> already
/// closed the identical failure for <c>@@</c> include paths. These cases call the resolver, which
/// calls that same function, so a source query and an include naming the same node cannot disagree
/// about where it lives.</para>
/// </summary>
public class MountRelativeSourceQueryTest
{
    /// <summary>Where the samples tree is served from in a statically-imported partition.</summary>
    private const string MirroredProduct = "MeshWeaver/samples/Graph/Data/Northwind/Product";

    /// <summary>The same type at a root mount — the Monolith shape, and the control for every
    /// case below that asserts a rewrite.</summary>
    private const string RootMountedProduct = "Northwind/Product";

    /// <summary>The entry as authored in <c>samples/Graph/Data/Northwind/Product.json</c>.</summary>
    private const string AuthoredSharedEntry = "shared=@Northwind/AnalyticsCatalog/Source/Supplier";

    /// <summary>
    /// THE issue: under the mirror prefix the authored entry must also be asked for at the path
    /// where the node actually lives.
    /// </summary>
    [Fact]
    public void MirroredMount_AlsoAsksForThePrefixedPath()
    {
        var expanded = CodeQueryResolver.Expand(AuthoredSharedEntry, MirroredProduct).ToList();

        expanded.Should().Contain(
            "path:MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog/Source/Supplier nodeType:Code",
            "the sibling entity source lives under the mount prefix, and resolving the authored "
            + "spelling verbatim is what produced CS0246 'Supplier could not be found' (#4813)");
        expanded.Should().Contain(
            "namespace:MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog/Source/Supplier "
            + "scope:subtree nodeType:Code",
            "the @ shorthand's folder form is anchored the same way as its exact form");
    }

    /// <summary>
    /// The authored spelling is KEPT alongside the anchored one. Which one resolves is a property
    /// of the mount, not of the entry — the same reason <c>ResolveCodeIncludes</c> tries the
    /// anchored include path first and keeps the authored one as a fallback.
    /// </summary>
    [Fact]
    public void MirroredMount_KeepsTheAuthoredSpellingToo()
    {
        CodeQueryResolver.Expand(AuthoredSharedEntry, MirroredProduct).Should().Contain(
            "path:Northwind/AnalyticsCatalog/Source/Supplier nodeType:Code",
            "anchoring ADDS a spelling; it never replaces the one the author wrote");
    }

    /// <summary>
    /// 🚨 The control on the other side of the change: at a root mount nothing is rewritten, so the
    /// Monolith and every unprefixed deployment keep exactly the queries they had.
    /// </summary>
    [Fact]
    public void RootMount_IsUnchanged()
    {
        CodeQueryResolver.Expand(AuthoredSharedEntry, RootMountedProduct).Should().Equal(
            "path:Northwind/AnalyticsCatalog/Source/Supplier nodeType:Code",
            "namespace:Northwind/AnalyticsCatalog/Source/Supplier scope:subtree nodeType:Code");
    }

    /// <summary>
    /// A genuinely cross-partition reference has no segment to anchor to and must stay verbatim —
    /// the #3903 shape (<c>rbuergi/OperationRequest</c> drawing on <c>Store/Core/Source</c>), where
    /// rewriting the query would turn a working reference into a missing one.
    /// </summary>
    [Fact]
    public void CrossPartitionReference_IsNeverAnchored()
    {
        CodeQueryResolver.Expand("shared=@Store/Core/Source", "rbuergi/OperationRequest")
            .Should().Equal(
                "path:Store/Core/Source nodeType:Code",
                "namespace:Store/Core/Source scope:subtree nodeType:Code");
    }

    /// <summary>
    /// An already-absolute reference is not double-prefixed: its first segment is the mount root,
    /// which sits at index 0 of the self path and is excluded from the anchor walk.
    /// </summary>
    [Fact]
    public void AlreadyAnchoredReference_IsNotPrefixedTwice()
    {
        CodeQueryResolver
            .Expand("shared=@MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog/Source",
                MirroredProduct)
            .Should().Equal(
                "path:MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog/Source nodeType:Code",
                "namespace:MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog/Source "
                + "scope:subtree nodeType:Code");
    }

    /// <summary>
    /// The default own-sources query is rebased on <c>$self</c> and therefore already carries the
    /// mount — it must not gain a duplicate. A second, identical query would double every compile's
    /// discovery fan-out for the one entry nearly every NodeType uses.
    /// </summary>
    [Theory]
    [InlineData(MirroredProduct)]
    [InlineData(RootMountedProduct)]
    public void DefaultOwnSourcesQuery_StaysASingleQuery(string selfPath)
    {
        CodeQueryResolver
            .ExpandAll(null, CodeQueryResolver.DefaultSources, selfPath)
            .Should().Equal($"namespace:{selfPath}/Source scope:subtree nodeType:Code");
    }

    /// <summary>
    /// The whole authored <c>sources</c> list of the failing sample, end to end: the own-sources
    /// entry resolves as before and BOTH sibling entity references now reach the mount.
    /// </summary>
    [Fact]
    public void TheFailingSamplesWholeSourceList_ReachesEverySiblingEntity()
    {
        IReadOnlyList<string> authored =
        [
            "namespace:Source scope:subtree",
            "shared=@Northwind/AnalyticsCatalog/Source/Supplier",
            "shared=@Northwind/AnalyticsCatalog/Source/Category"
        ];

        var expanded = CodeQueryResolver
            .ExpandAll(authored, CodeQueryResolver.DefaultSources, MirroredProduct)
            .ToList();

        expanded.Should().Contain(
            $"namespace:{MirroredProduct}/Source scope:subtree nodeType:Code");
        foreach (var entity in new[] { "Supplier", "Category" })
            expanded.Should().Contain(
                $"path:MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog/Source/{entity} "
                + "nodeType:Code",
                $"'{entity}' is the type Roslyn could not find (#4813)");
    }

    /// <summary>
    /// 🚨 The reader-facing payoff, and the reason the fix belongs in <c>Expand</c> rather than at
    /// one call site: <see cref="SourceCoverage"/> measures per DECLARED ENTRY over the same
    /// expansions, so an entry that now reaches the mount stops being reported as matching nothing.
    /// Before the fix it was — correctly — named as a missing source, which is a true statement
    /// about a mesh whose content is entirely present.
    /// </summary>
    [Fact]
    public void SourceCoverage_NoLongerReportsTheAnchorableEntryAsMissing()
    {
        IReadOnlyList<string> authored =
        [
            "namespace:Source scope:subtree",
            "shared=@Northwind/AnalyticsCatalog/Source/Supplier"
        ];
        string[] matched =
        [
            $"{MirroredProduct}/Source/ProductContent",
            $"{MirroredProduct}/Source/ProductNodeLayoutAreas",
            "MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog/Source/Supplier"
        ];

        SourceCoverage.UnmatchedSourceQueries(authored, MirroredProduct, matched)
            .Should().BeEmpty(
                "every declared entry now has an expansion that reaches the node it names");
    }

    /// <summary>
    /// The negative control for the case above: with the sibling entity genuinely absent, the entry
    /// is still reported. Anchoring must not make a real missing source look satisfied.
    /// </summary>
    [Fact]
    public void SourceCoverage_StillReportsAnEntryWhoseNodeIsGenuinelyAbsent()
    {
        IReadOnlyList<string> authored =
        [
            "namespace:Source scope:subtree",
            "shared=@Northwind/AnalyticsCatalog/Source/Supplier"
        ];
        string[] matched = [$"{MirroredProduct}/Source/ProductContent"];

        SourceCoverage.UnmatchedSourceQueries(authored, MirroredProduct, matched)
            .Should().Equal("shared=@Northwind/AnalyticsCatalog/Source/Supplier");
    }

    /// <summary>
    /// A group whose entries anchor keeps a usable <c>BaseNamespace</c>: a root and its anchored
    /// form are the same root resolved two ways, so the GUI's source listing still relativises the
    /// files it shows instead of falling back to full paths.
    /// </summary>
    [Fact]
    public void GroupRoot_TreatsAnAnchoredRootAsTheSameRoot()
    {
        var groups = CodeQueryResolver.GroupAll(
            ["shared=@Northwind/AnalyticsCatalog/Source"],
            CodeQueryResolver.DefaultSources,
            MirroredProduct,
            CodeQueryResolver.DefaultSourceGroupName);

        groups.Should().ContainSingle().Which.BaseNamespace.Should().Be(
            "MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog/Source",
            "the anchored spelling is the one that resolves, so it is the root to show paths "
            + "relative to");
    }

    /// <summary>
    /// The control for the fold above: two genuinely different roots still disagree, so a mixed
    /// group is still reported as having no single base.
    /// </summary>
    [Fact]
    public void GroupRoot_StillNullForGenuinelyDifferentRoots()
    {
        var groups = CodeQueryResolver.GroupAll(
            ["shared=@Store/Core/Source", "shared=@Store/Licensing/Source"],
            CodeQueryResolver.DefaultSources,
            "rbuergi/OperationRequest",
            CodeQueryResolver.DefaultSourceGroupName);

        groups.Should().ContainSingle().Which.BaseNamespace.Should().BeNull();
    }

    /// <summary>
    /// The anchor walk takes the DEEPEST occurrence of the first segment — the most local reading,
    /// the same rule <c>AnchorIncludePath</c> applies to includes — and it rewrites only the
    /// location token, leaving every <c>scope:</c> / <c>nodeType:</c> filter alone.
    /// </summary>
    [Fact]
    public void AnchoringRewritesOnlyTheLocationToken()
    {
        CodeQueryResolver
            .AnchorToMount("namespace:Northwind/Shared scope:subtree nodeType:Scope",
                "MeshWeaver/samples/Graph/Data/Northwind/Product")
            .Should().Be(
                "namespace:MeshWeaver/samples/Graph/Data/Northwind/Shared scope:subtree "
                + "nodeType:Scope");
    }

    /// <summary>A query with nothing to anchor answers null, so <see cref="CodeQueryResolver.Expand"/>
    /// emits no sibling for it.</summary>
    [Fact]
    public void AnchoringAnswersNullWhenItChangesNothing()
    {
        CodeQueryResolver
            .AnchorToMount("path:Store/Core/Source nodeType:Code", "rbuergi/OperationRequest")
            .Should().BeNull();
    }
}
