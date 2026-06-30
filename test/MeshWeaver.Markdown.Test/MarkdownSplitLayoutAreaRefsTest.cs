using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// <see cref="MarkdownViewLogic.SplitLayoutAreaRefs"/> — the native-MAUI segmenter that pulls <c>@@</c>
/// live-area embeds out of the rendered markdown HTML so a view pack can render each as a real native area
/// between WebView chunks (a static WebView can't hydrate the <c>@@</c> placeholder — the
/// <c>NativeMauiRendering.md</c> keystone). Runs the REAL <see cref="MarkdownViewLogic.Render"/> pipeline the
/// MAUI <c>MarkdownView</c> uses, then splits its HTML — so the test pins the actual emitted contract.
/// </summary>
public class MarkdownSplitLayoutAreaRefsTest
{
    private static string Html(string md) =>
        MarkdownViewLogic.Render(md, collection: null, currentNodePath: null).Html;

    [Fact]
    public void Empty_ReturnsEmpty() =>
        MarkdownViewLogic.SplitLayoutAreaRefs("").Should().BeEmpty();

    [Fact]
    public void NoEmbed_IsASingleHtmlSegment()
    {
        var segs = MarkdownViewLogic.SplitLayoutAreaRefs("<p>just prose</p>");
        segs.Should().ContainSingle();
        segs[0].Embed.Should().BeNull();
        segs[0].Html.Should().Be("<p>just prose</p>");
    }

    [Fact]
    public void PreResolvedEmbed_ExtractsAddressAndArea()
    {
        // @@/Acme/area/Search → data-address='Acme' data-area='Search' (the explicit, pre-parsed form).
        var segs = MarkdownViewLogic.SplitLayoutAreaRefs(Html("@@/Acme/area/Search"));

        var embed = segs.Should().ContainSingle(s => s.Embed != null).Which.Embed!.Value;
        embed.Address.Should().Be("Acme");
        embed.Area.Should().Be("Search");
    }

    [Fact]
    public void RawPathEmbed_ExtractsRawPath()
    {
        // @@MyNs/MyArea → data-raw-path='MyNs/MyArea' (the bare form; address/area resolved at render time).
        var segs = MarkdownViewLogic.SplitLayoutAreaRefs(Html("@@MyNs/MyArea"));

        var embed = segs.Should().ContainSingle(s => s.Embed != null).Which.Embed!.Value;
        embed.RawPath.Should().Be("MyNs/MyArea");
    }

    [Fact]
    public void ProseAroundEmbed_IsOrdered_HtmlThenEmbedThenHtml()
    {
        var segs = MarkdownViewLogic.SplitLayoutAreaRefs(Html("Before the area.\n\n@@/Acme/area/Search\n\nAfter the area."));

        // Exactly one embed, with prose html on both sides of it (order preserved).
        segs.Count(s => s.Embed != null).Should().Be(1);
        var embedIdx = segs.ToList().FindIndex(s => s.Embed != null);
        embedIdx.Should().BeGreaterThan(0, "prose precedes the embed");
        embedIdx.Should().BeLessThan(segs.Count - 1, "prose follows the embed");
        segs[embedIdx - 1].Html.Should().Contain("Before the area");
        segs[embedIdx + 1].Html.Should().Contain("After the area");
    }

    [Fact]
    public void TwoEmbeds_BothExtracted()
    {
        var segs = MarkdownViewLogic.SplitLayoutAreaRefs(Html("@@/Acme/area/Search\n\n@@MyNs/MyArea"));
        segs.Count(s => s.Embed != null).Should().Be(2);
    }
}
