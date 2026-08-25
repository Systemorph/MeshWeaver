#pragma warning disable CS1591

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// 🚨 <b>The view-options bar must be able to WRAP, and its fields must be able to SHRINK.</b>
///
/// <para>The bar (<c>Group by ▾ · Sort by ▾ · ⚙</c>) shares one header row with the search box on
/// desktop. Every piece of it is a flex container, and a flex item's default <c>min-width: auto</c>
/// refuses to shrink below its content — so with no <c>flex-wrap</c> and no <c>min-width: 0</c> the
/// bar cannot give way when the row runs out of space. It does not clip and it does not wrap: it
/// OVERFLOWS, drawing its selects on top of the search input and past the container's edge, with the
/// labels cut mid-word (#2216: "Type" rendered as "pe", an empty select overlapping "Last accessed").
/// It was wrong at full width and worse in a narrow pane.</para>
///
/// <para>This asserts the four rules that let the row give way, against the stylesheet itself. A
/// rendering test would need a browser and a viewport; the defect, however, is entirely in these
/// declarations — remove any one of them and the overflow returns.</para>
/// </summary>
public class SearchViewBarWrapTest
{
    private static string Stylesheet()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName,
            "src", "MeshWeaver.Blazor.Views", "Components", "MeshSearchView.razor.css");
        Assert.True(File.Exists(path), $"stylesheet not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The declarations of one CSS rule, by EXACT selector — the rule's own block only.
    ///
    /// <para>Anchored at the start of a line, because <c>.mesh-search-viewbar</c> is also the tail
    /// of <c>.mesh-search-header .mesh-search-viewbar</c>: an unanchored match reads the descendant
    /// rule's body and answers a question about the wrong rule. Caught by this test failing after
    /// the fix had already landed in the rule it was meant to check.</para>
    /// </summary>
    private static string RuleBody(string css, string selector)
    {
        var match = Regex.Match(
            css,
            $@"^{Regex.Escape(selector)}\s*(,[^{{]*)?\{{(?<body>[^}}]*)\}}",
            RegexOptions.Multiline);
        Assert.True(match.Success, $"no rule found for '{selector}'");
        return match.Groups["body"].Value;
    }

    [Fact]
    public void ViewBar_Wraps_RatherThanOverflowing()
    {
        var body = RuleBody(Stylesheet(), ".mesh-search-viewbar");
        Assert.Matches(@"flex-wrap:\s*wrap", body);
    }

    [Fact]
    public void ViewBar_CanShrink_WithinTheHeaderRow()
    {
        // In the header the bar sat at `flex: 0 0 auto` — it could neither shrink nor wrap, so the
        // search box kept its 320px basis and the bar ran off the end.
        var body = RuleBody(Stylesheet(), ".mesh-search-header .mesh-search-viewbar");
        Assert.Matches(@"flex:\s*0\s+1\s+auto", body);
        Assert.Matches(@"min-width:\s*0", body);
    }

    [Fact]
    public void ViewBarField_CanShrinkBelowItsContent()
    {
        // Each `label` is itself a flex container holding a caption and a select. Without
        // `min-width: 0` its default `min-width: auto` keeps it at content width, and the caption
        // is what gets painted over.
        var body = RuleBody(Stylesheet(), ".mesh-search-viewbar-field");
        Assert.Matches(@"min-width:\s*0", body);
    }

    [Fact]
    public void ViewBarSelects_CanShrink()
    {
        // The selects carry the longest content ("Last accessed"). They must be allowed to narrow;
        // otherwise the field shrinks to nothing and the select still overflows it.
        var body = RuleBody(Stylesheet(), ".mesh-search-sortby");
        Assert.Matches(@"min-width:\s*0", body);
    }
}
