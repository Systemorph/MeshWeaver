using FluentAssertions;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

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
        PathMatcher.ShouldNotify("/ACME/Project/", "/ACME/Project/", QueryScope.Exact)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_PathsWithDifferentCasing_MatchesCaseInsensitively()
    {
        PathMatcher.ShouldNotify("acme/project/task", "ACME/PROJECT", QueryScope.Descendants)
            .Should().BeTrue();
    }

    #endregion
}
