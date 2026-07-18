#pragma warning disable CS1591

using System.Text.RegularExpressions;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Reproduces issue #502 — "Space Overview catalog (Contents) tiles no longer render" — end to end
/// through the exact stages that run at MOUNT time for an <c>@@("area:Search")</c> catalog embed in a
/// Space's markdown body.
///
/// <para>Commit <c>e03f20fac</c> removed the hardcoded <c>SpaceLayoutAreas.BuildNavigation</c> catalog
/// section and made the catalog depend entirely on the body embed shipped in
/// <c>SpaceNodeType.WelcomeMarkdown</c>. That template shipped the COLON keyword form
/// <c>@@("area:Search")</c>. The markdown pipeline renders it to a mountable
/// <c>&lt;div class='layout-area' data-raw-path='{space}/area:Search' …&gt;</c> — which the issue's
/// pure-pipeline test confirmed healthy. But the Blazor MOUNT (<c>PathBasedLayoutArea</c>) re-resolves
/// that raw path through <c>IPathResolver</c> (matches the longest node prefix = the space, leaving
/// remainder <c>"area:Search"</c>) and then parses the remainder with
/// <see cref="LayoutAreaMarkdownParser.ParseAreaAndId"/>. Pre-fix, that split only on '/', so
/// <c>"area:Search"</c> became an area named literally <c>"area:Search"</c> — a non-existent area that
/// mounts to nothing, rendering the catalog BLANK with no error (the catalog's MeshSearchControl ships
/// <c>emptyMessage=false, loading=false</c>).</para>
///
/// <para>These tests drive the real Markdig pipeline (with the Space's NodePath, as
/// <c>SpaceLayoutAreas.BuildBodyContent</c> sets it) and then simulate the mount's resolver step —
/// strip the resolved space prefix from the emitted raw path and run
/// <see cref="LayoutAreaMarkdownParser.ParseAreaAndId"/> — asserting the mount lands on the real
/// <c>Search</c> catalog area for BOTH the colon form the template shipped and the slash form it was
/// later switched to. Revert the <c>ParseAreaAndId</c> colon-normalisation and the colon case fails
/// with area <c>"area:Search"</c>.</para>
/// </summary>
public class SpaceCatalogEmbedMountTest
{
    private const string SpacePath = "Acme";

    // The layout-area div is emitted single-line by LayoutAreaMarkdownRenderer.GetLayoutAreaDiv, so a
    // targeted attribute match is exact — no HTML parser dependency needed.
    private static readonly Regex RawPathAttr =
        new($"class='{LayoutAreaMarkdownRenderer.LayoutArea}'[^>]*?data-{LayoutAreaMarkdownRenderer.RawPath}='([^']*)'",
            RegexOptions.Compiled);

    /// <summary>
    /// Runs the exact Markdig pipeline the Space body MarkdownControl uses (NodePath = the space path,
    /// per <c>SpaceLayoutAreas.BuildBodyContent</c>) and returns the emitted <c>data-raw-path</c> of the
    /// single embedded layout-area div.
    /// </summary>
    private static string RenderEmbedRawPath(string embedMarkdown)
    {
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(collection: null, currentNodePath: SpacePath);
        var html = Markdig.Markdown.ToHtml(embedMarkdown, pipeline);

        var match = RawPathAttr.Match(html);
        match.Success.Should().BeTrue(
            $"the @@ embed must render a layout-area div carrying a raw path; got:\n{html}");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Simulates the mount-time resolver step: <c>IPathResolver</c> matches the longest existing node
    /// prefix (the space) and hands the tail to <c>PathBasedLayoutArea.ParseRemainder</c> →
    /// <see cref="LayoutAreaMarkdownParser.ParseAreaAndId"/>. The prefix strip is exact here because
    /// the space is the deepest real node on the raw path.
    /// </summary>
    private static (string? Area, string? Id) MountResolve(string rawPath)
    {
        var prefix = SpacePath + "/";
        Assert.StartsWith(prefix, rawPath);
        var remainder = rawPath[prefix.Length..];
        return LayoutAreaMarkdownParser.ParseAreaAndId(remainder);
    }

    [Theory]
    // The COLON form the WelcomeMarkdown template shipped (e03f20fac) — the regression case.
    [InlineData("@@(\"area:Search\")")]
    // The SLASH form the template was later switched to (b0acd44f1) — must keep working.
    [InlineData("@@(\"area/Search\")")]
    // The relative slash form an author may write directly.
    [InlineData("@@area/Search")]
    public void SpaceBodyCatalogEmbed_MountsToTheSearchCatalogArea(string embed)
    {
        var rawPath = RenderEmbedRawPath(embed);

        // The embed must resolve against the space (relative @@ resolved via NodePath), never a dead
        // unaddressed div — the raw path carries the space prefix.
        rawPath.Should().StartWith(SpacePath + "/",
            "the relative @@ embed must resolve against the Space's NodePath");

        var (area, id) = MountResolve(rawPath);

        area.Should().Be(MeshNodeLayoutAreas.SearchArea,
            "the catalog embed must mount to the real 'Search' area — NOT a non-existent area named " +
            "after the raw keyword token (issue #502)");
        id.Should().BeNull();
    }

    [Fact]
    public void SpaceBodyCatalogEmbed_TunedGroupBy_CarriesQueryIntoAreaId()
    {
        // The template documents `@@("area:Search?groupBy=…")` as the tuned catalog — the query must
        // survive the mount and land on the area id, not corrupt the area name.
        var rawPath = RenderEmbedRawPath("@@(\"area:Search?groupBy=type\")");
        var (area, id) = MountResolve(rawPath);

        area.Should().Be(MeshNodeLayoutAreas.SearchArea);
        id.Should().Be("?groupBy=type");
    }
}
