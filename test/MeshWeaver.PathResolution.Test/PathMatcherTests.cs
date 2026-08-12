using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.PathResolution.Test;

public class PathMatcherTests
{
    #region Exact Scope Tests

    [Fact]
    public void ShouldNotify_ExactScope_ExactMatch_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME/Project", "ACME/Project", QueryScope.Exact)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_ExactScope_CaseInsensitive_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("acme/project", "ACME/Project", QueryScope.Exact)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_ExactScope_DifferentPath_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("ACME/Other", "ACME/Project", QueryScope.Exact)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_ExactScope_ChildPath_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("ACME/Project/Task", "ACME/Project", QueryScope.Exact)
            .Should().BeFalse();
    }

    #endregion

    #region NextLevel Scope Tests

    // NextLevel re-queries the frontier, so any subtree change must notify (over-notify is correct:
    // a new nearer node collapses the frontier, a delete reopens it — the re-query recomputes).
    [Fact]
    public void ShouldNotify_NextLevelScope_DirectChild_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME/Project", "ACME", QueryScope.NextLevel)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_NextLevelScope_DeepDescendant_ReturnsTrue()
    {
        // A deep node (a/b/node-style) changing must re-trigger — it could be the frontier itself.
        PathMatcher.ShouldNotify("ACME/a/b/node", "ACME", QueryScope.NextLevel)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_NextLevelScope_Self_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("ACME", "ACME", QueryScope.NextLevel)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_NextLevelScope_Unrelated_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("OTHER/x", "ACME", QueryScope.NextLevel)
            .Should().BeFalse();
    }

    #endregion

    #region Children Scope Tests

    [Fact]
    public void ShouldNotify_ChildrenScope_DirectChild_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME/Project", "ACME", QueryScope.Children)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_ChildrenScope_GrandChild_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("ACME/Project/Task", "ACME", QueryScope.Children)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_ChildrenScope_Self_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("ACME", "ACME", QueryScope.Children)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_ChildrenScope_RootChildren_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME", "", QueryScope.Children)
            .Should().BeTrue();
    }

    #endregion

    #region Descendants Scope Tests

    [Fact]
    public void ShouldNotify_DescendantsScope_DirectChild_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME/Project", "ACME", QueryScope.Descendants)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_DescendantsScope_GrandChild_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME/Project/Task", "ACME", QueryScope.Descendants)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_DescendantsScope_Self_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("ACME", "ACME", QueryScope.Descendants)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_DescendantsScope_FromRoot_FindsAll()
    {
        PathMatcher.ShouldNotify("ACME", "", QueryScope.Descendants)
            .Should().BeTrue();
        PathMatcher.ShouldNotify("ACME/Project", "", QueryScope.Descendants)
            .Should().BeTrue();
    }

    #endregion

    #region Ancestors Scope Tests

    [Fact]
    public void ShouldNotify_AncestorsScope_Parent_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME", "ACME/Project", QueryScope.Ancestors)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_AncestorsScope_Grandparent_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME", "ACME/Project/Task", QueryScope.Ancestors)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_AncestorsScope_Self_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("ACME/Project", "ACME/Project", QueryScope.Ancestors)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_AncestorsScope_Root_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("", "ACME/Project", QueryScope.Ancestors)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_AncestorsScope_Child_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("ACME/Project/Task", "ACME/Project", QueryScope.Ancestors)
            .Should().BeFalse();
    }

    #endregion

    #region Subtree Scope Tests

    [Fact]
    public void ShouldNotify_SubtreeScope_Self_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME/Project", "ACME/Project", QueryScope.Subtree)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_SubtreeScope_Child_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME/Project/Task", "ACME/Project", QueryScope.Subtree)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_SubtreeScope_Parent_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("ACME", "ACME/Project", QueryScope.Subtree)
            .Should().BeFalse();
    }

    #endregion

    #region AncestorsAndSelf Scope Tests

    [Fact]
    public void ShouldNotify_AncestorsAndSelfScope_Self_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME/Project", "ACME/Project", QueryScope.AncestorsAndSelf)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_AncestorsAndSelfScope_Parent_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME", "ACME/Project", QueryScope.AncestorsAndSelf)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_AncestorsAndSelfScope_Child_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("ACME/Project/Task", "ACME/Project", QueryScope.AncestorsAndSelf)
            .Should().BeFalse();
    }

    #endregion

    #region Hierarchy Scope Tests

    [Fact]
    public void ShouldNotify_HierarchyScope_Self_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME/Project", "ACME/Project", QueryScope.Hierarchy)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_HierarchyScope_Parent_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME", "ACME/Project", QueryScope.Hierarchy)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_HierarchyScope_Child_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("ACME/Project/Task", "ACME/Project", QueryScope.Hierarchy)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_HierarchyScope_Sibling_ReturnsFalse()
    {
        PathMatcher.ShouldNotify("ACME/Other", "ACME/Project", QueryScope.Hierarchy)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_HierarchyScope_Root_ReturnsTrue()
    {
        PathMatcher.ShouldNotify("", "ACME/Project", QueryScope.Hierarchy)
            .Should().BeTrue();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ShouldNotify_EmptyChangedPath_HandlesCorrectly()
    {
        PathMatcher.ShouldNotify("", "ACME", QueryScope.Ancestors)
            .Should().BeTrue();

        PathMatcher.ShouldNotify("", "ACME", QueryScope.Exact)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_EmptyBasePath_HandlesCorrectly()
    {
        PathMatcher.ShouldNotify("ACME", "", QueryScope.Exact)
            .Should().BeFalse();

        PathMatcher.ShouldNotify("ACME", "", QueryScope.Descendants)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_PathsWithLeadingTrailingSlashes_NormalizesCorrectly()
    {
        PathMatcher.ShouldNotify("/Software/Project/", "/Software/Project/", QueryScope.Exact)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_PathsWithDifferentCasing_MatchesCaseInsensitively()
    {
        PathMatcher.ShouldNotify("acme/project/task", "ACME/Project", QueryScope.Descendants)
            .Should().BeTrue();
    }

    #endregion

    #region Wildcard Namespace Relevance — the change-feed gate (#1235)

    /// <summary>
    /// <c>ShouldNotifyForQuery</c> is the live relevance gate for the Postgres / Snowflake / Cosmos
    /// change feeds: it decides whether a CRUD event should refresh a synced query. For a
    /// path-less wildcard query the namespace patterns are the ONLY thing it can judge on, so a
    /// pattern that matches nothing means the query never refreshes — a node created below a
    /// <c>Source</c> folder simply never appears.
    ///
    /// <para>🚨 The two-wildcard pattern <c>*/Source/*</c> — which <c>scope:subtree</c> produces
    /// (#1232) — used to be the broken one: <c>GlobMatch</c> split on the FIRST wildcard and took
    /// the whole remainder as a literal suffix, giving <c>EndsWith("/Source/*")</c>. Nothing ends
    /// with that, so the nested case below silently never fired.</para>
    /// </summary>
    [Theory]
    // Direct child of a Source folder — matched by the FIRST pattern.
    [InlineData("acme/SampleData/Source/Spine", true)]
    // Nested one level deeper — matched ONLY by the two-wildcard second pattern.
    [InlineData("acme/SampleData/Source/Fixtures/MtplClaimFixtures", true)]
    // Deeper still.
    [InlineData("acme/SampleData/Source/A/B/C/Leaf", true)]
    // No Source segment anywhere — must NOT wake the query up.
    [InlineData("acme/SampleData/Other/Thing", false)]
    // 'Sources' is a different segment: the literal after the wildcard is matched verbatim.
    [InlineData("acme/Sources/Thing", false)]
    public void ShouldNotifyForQuery_WildcardNamespaceSubtree_CoversNestedNamespaces(
        string changedPath, bool expected)
    {
        // Exactly what QueryParser emits for `namespace:*/Source scope:subtree`.
        var patterns = new[] { "*/Source", "*/Source/*" };

        PathMatcher.ShouldNotifyForQuery(changedPath, queryBasePath: "", QueryScope.Exact, patterns)
            .Should().Be(expected);
    }

    /// <summary>
    /// A single-wildcard satellite pattern keeps working — the surrounding literals, including the
    /// leading underscore of <c>_Thread</c>, are matched verbatim.
    /// </summary>
    [Theory]
    [InlineData("rbuergi/_Thread/abc", true)]
    [InlineData("rbuergi/_ThreadMessage/abc", false)]
    [InlineData("other/_Thread/abc", false)]
    public void NamespaceInScope_SingleWildcardSatellitePattern_StillMatchesVerbatimLiterals(
        string changedPath, bool expected)
    {
        PathMatcher.NamespaceInScope(PathMatcher.NamespaceOf(changedPath), ["rbuergi/*_Thread"])
            .Should().Be(expected);
    }

    /// <summary>
    /// 🚨 <c>%</c> is NOT a wildcard here. This matcher used to accept it, to compensate for the
    /// parser rewriting <c>*</c>→<c>%</c> for namespaces; the parser no longer does that, and
    /// tolerating both spellings is precisely what let the two vocabularies drift apart without
    /// anyone noticing. Keeping it literal makes a re-introduction fail loudly.
    /// </summary>
    [Fact]
    public void NamespaceInScope_PercentIsALiteral_NotAWildcard()
    {
        PathMatcher.NamespaceInScope("acme/SampleData/Source", ["*/Source"])
            .Should().BeTrue("`*` is the one wildcard vocabulary of the AST");
        PathMatcher.NamespaceInScope("acme/SampleData/Source", ["%/Source"])
            .Should().BeFalse("`%` is SQL dialect and never travels in a ParsedQuery");
    }

    /// <summary>
    /// A pattern with NO wildcard keeps its "this namespace or anything under it" reading — the
    /// glob change must not narrow the concrete case.
    /// </summary>
    [Theory]
    [InlineData("acme/Docs", true)]
    [InlineData("acme/Docs/Sub", true)]
    [InlineData("acme/DocsOther", false)]
    public void NamespaceInScope_ConcretePattern_MatchesSelfOrBelow(string ns, bool expected)
    {
        PathMatcher.NamespaceInScope(ns, ["acme/Docs"]).Should().Be(expected);
    }

    #endregion
}
