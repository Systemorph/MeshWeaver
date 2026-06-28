using MeshWeaver.Data.Completion;
using Xunit;

namespace MeshWeaver.Autocomplete.Test;

/// <summary>
/// Pins the <c>@</c>-autocomplete ranking law (<see cref="AutocompleteRelevance"/>):
/// occurrence in the PATH weighs the most, TITLE second, underlying relevance last.
/// Pure-function tests — no mesh, deterministic.
/// </summary>
public class AutocompleteRelevanceTest
{
    [Fact]
    public void PathMatch_OutranksTitleOnlyMatch()
    {
        // Item A: token in PATH only. Item B: token in TITLE only, with a MAX relevance rank.
        var pathOnly = AutocompleteRelevance.Score("acme", path: "acme/report", title: "Quarterly", relevanceRank: 0);
        var titleOnly = AutocompleteRelevance.Score("acme", path: "x/y", title: "Acme Corp", relevanceRank: 9);

        Assert.True(pathOnly > titleOnly,
            $"a path match ({pathOnly}) must outrank a title-only match ({titleOnly}) even when the title item has max relevance");
    }

    [Fact]
    public void PathTiers_AreStrictlyOrdered()
    {
        // exact id (last segment) > exact ancestor segment > id prefix > ancestor prefix > multi > single
        var exactId      = AutocompleteRelevance.PathTier("report", "acme/report");
        var exactSegment = AutocompleteRelevance.PathTier("acme",   "acme/report");
        var idPrefix     = AutocompleteRelevance.PathTier("rep",    "acme/report");
        var segPrefix    = AutocompleteRelevance.PathTier("ac",     "acme/report");
        var single       = AutocompleteRelevance.PathTier("port",   "acme/report");  // substring in 're[port]'

        Assert.True(exactId > exactSegment, $"exact id {exactId} > exact segment {exactSegment}");
        Assert.True(exactSegment > idPrefix, $"exact segment {exactSegment} > id prefix {idPrefix}");
        Assert.True(idPrefix > segPrefix, $"id prefix {idPrefix} > ancestor-segment prefix {segPrefix}");
        Assert.True(segPrefix > single, $"prefix {segPrefix} > single substring {single}");
        Assert.True(single > 0, "a single substring occurrence still scores above zero");
    }

    [Fact]
    public void MoreOccurrencesInPath_RankHigherThanSingle()
    {
        // "report" occurs twice vs once — the multi-occurrence path must rank higher (occurrence weighs most).
        var twice = AutocompleteRelevance.PathTier("re", "report/review");   // 're'… appears in both segments
        var once  = AutocompleteRelevance.PathTier("re", "alpha/review");    // 're' once
        // Both are ancestor-prefix-or-substring matches; the double-occurrence is at least as strong.
        Assert.True(twice >= once);

        // Direct substring band: two occurrences outrank one.
        var multi  = AutocompleteRelevance.PathTier("xy", "axyb/cxyd");      // 'xy' twice, no segment is exact/prefix
        var singleOcc = AutocompleteRelevance.PathTier("xy", "axyb/cdef");   // 'xy' once
        Assert.True(multi > singleOcc, $"two path occurrences ({multi}) must rank above one ({singleOcc})");
    }

    [Fact]
    public void TitleTiers_AreStrictlyOrdered()
    {
        var exact   = AutocompleteRelevance.TitleTier("acme", "acme");
        var prefix  = AutocompleteRelevance.TitleTier("acme", "Acme Corporation");
        var contains = AutocompleteRelevance.TitleTier("corp", "Acme Corporation");

        Assert.True(exact > prefix, $"exact title {exact} > prefix {prefix}");
        Assert.True(prefix > contains, $"prefix {prefix} > contains {contains}");
        Assert.True(contains > 0);
    }

    [Fact]
    public void TitleBreaksTies_WhenPathTierIsEqual()
    {
        // Same path tier (both exact id), title decides: exact-title beats substring-title.
        var titleExact   = AutocompleteRelevance.Score("acme", "x/acme", title: "acme", relevanceRank: 0);
        var titleWeaker  = AutocompleteRelevance.Score("acme", "y/acme", title: "The acme thing", relevanceRank: 9);

        Assert.True(titleExact > titleWeaker,
            $"with equal path tier, the stronger title ({titleExact}) outranks the weaker one ({titleWeaker}) " +
            "even when the weaker has max relevance");
    }

    [Fact]
    public void RelevanceBreaksTies_WhenPathAndTitleAreEqual()
    {
        var hi = AutocompleteRelevance.Score("acme", "x/acme", title: "acme", relevanceRank: 9);
        var lo = AutocompleteRelevance.Score("acme", "y/acme", title: "acme", relevanceRank: 1);
        Assert.True(hi > lo, $"with equal path+title, higher relevance ({hi}) outranks lower ({lo})");
    }

    [Fact]
    public void Score_StaysWithinSortBand()
    {
        // Max possible: path 9, title 9, relevance 9 → 9990; must stay below the 9999 UI clamp.
        var max = AutocompleteRelevance.Score("acme", "acme", "acme", 9);
        Assert.Equal(9 * 1000 + 9 * 100 + 9 * 10, max);
        Assert.True(max <= 9999);
    }

    [Theory]
    [InlineData(null, "acme/report")]
    [InlineData("", "acme/report")]
    [InlineData("acme", null)]
    [InlineData("acme", "")]
    public void EmptyInputs_ScoreZeroTiers(string? query, string? path)
    {
        Assert.Equal(0, AutocompleteRelevance.PathTier(query, path));
    }

    [Fact]
    public void Ranking_IsCaseInsensitive()
    {
        Assert.Equal(
            AutocompleteRelevance.Score("ACME", "X/Acme", "AcMe", 5),
            AutocompleteRelevance.Score("acme", "x/acme", "acme", 5));
    }
}
