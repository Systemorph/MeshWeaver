using MeshWeaver.Graph;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The Contents index of a MULTI-PART document must list its parts — all of them, in order.
///
/// <para>🚨 It did not. The catalog behind that index queried the whole descendant subtree for
/// every render mode, and the item limit was then spent on descendants before the level being
/// displayed was complete. Measured on the 14-lesson <c>AdvancedBusinessRules</c> course:
/// <b>5 of 14 lessons</b> appeared, in no useful order, interleaved with every <c>Quiz</c>,
/// <c>MyExercises</c>, <c>Solution/</c> and <c>Exercise/</c> node — and the result was still
/// truncated, so no amount of scrolling reached the missing nine.</para>
///
/// <para>The failure is silent in the worst way: an index that lists <i>something</i> looks like an
/// index that works. A reader has no way to tell that nine parts of the document they are looking
/// at are simply not on the page.</para>
///
/// <para>Only <see cref="MeshSearchRenderMode.NamespaceTree"/> needs the subtree — it reveals
/// deeper levels lazily. Every other mode shows one level, and depth is reached by the drill-down
/// link each card already carries.</para>
/// </summary>
public class MultiPartContentsIndexTest
{
    [Theory]
    [InlineData(MeshSearchRenderMode.GraphNavigator)]   // the Search area's own default
    [InlineData(MeshSearchRenderMode.Flat)]
    [InlineData(MeshSearchRenderMode.Hierarchical)]
    [InlineData(MeshSearchRenderMode.Grouped)]
    public void A_one_level_catalog_lists_children_not_the_whole_subtree(MeshSearchRenderMode mode)
    {
        MeshNodeLayoutAreas.DefaultIncludeSubtree(mode).Should().BeFalse(
            $"{mode} renders a single level, so a subtree query cannot show more — it only spends "
            + "the item limit on descendants and starves the level being displayed");

        var catalog = MeshNodeLayoutAreas.BuildCatalog(
            "AdvancedBusinessRules",
            new MeshNodeLayoutAreas.CatalogOptions
            {
                Mode = mode,
                IncludeSubtree = MeshNodeLayoutAreas.DefaultIncludeSubtree(mode),
            });

        ((string?)catalog.HiddenQuery).Should().NotContain(
            "scope:subtree",
            "a 14-part course listed this way showed 5 parts and hid the other 9 behind a truncated "
            + "subtree of quizzes, solutions and exercise sources");
    }

    /// <summary>
    /// The namespace tree keeps the subtree — it is the one renderer that descends lazily, and
    /// scoping it to children would stop it at the first level. This is why the fix is per-mode
    /// rather than a blanket change.
    /// </summary>
    [Fact]
    public void The_namespace_tree_still_gets_its_subtree()
    {
        MeshNodeLayoutAreas.DefaultIncludeSubtree(MeshSearchRenderMode.NamespaceTree).Should().BeTrue();

        var tree = MeshNodeLayoutAreas.BuildCatalog(
            "AdvancedBusinessRules",
            new MeshNodeLayoutAreas.CatalogOptions
            {
                Mode = MeshSearchRenderMode.NamespaceTree,
                IncludeSubtree = MeshNodeLayoutAreas.DefaultIncludeSubtree(MeshSearchRenderMode.NamespaceTree),
            });

        ((string?)tree.HiddenQuery).Should().Contain(
            "scope:subtree",
            "the namespace tree reveals deeper levels lazily and would stop at direct children without it");
    }
}
