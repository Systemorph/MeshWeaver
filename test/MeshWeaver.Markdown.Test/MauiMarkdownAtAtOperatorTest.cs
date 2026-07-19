#pragma warning disable CS1591

using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Dedicated test for the <c>@@</c> (inline layout-area embed) operator as rendered by the native MAUI
/// client's <c>MarkdownView</c> (<c>src/MeshWeaver.Maui/MauiViewPack.cs</c>). That view replaced the old
/// homebrew Label — which dumped raw markdown verbatim, so <c>@@</c> did nothing — with the OFFICIAL
/// MeshWeaver Markdig pipeline: it builds <see cref="MarkdownExtensions.CreateMarkdownPipeline"/> (which
/// wires the <see cref="LayoutAreaMarkdownExtension"/>) and converts markdown to HTML with
/// <c>Markdig.Markdown.ToHtml(text, pipeline)</c>. This test runs that EXACT pipeline + call and pins the
/// contract: a <c>@@path</c> block is RECOGNISED — it emits a layout-area element (per
/// <see cref="LayoutAreaMarkdownRenderer"/>), NOT the literal text <c>@@path</c>.
/// </summary>
public class MauiMarkdownAtAtOperatorTest
{
    /// <summary>The exact pipeline + render call the MAUI <c>MarkdownView.Render</c> makes.</summary>
    private static string RenderLikeMaui(string markdown, string? nodePath = null)
    {
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(collection: null, currentNodePath: nodePath);
        return Markdig.Markdown.ToHtml(markdown, pipeline);
    }

    [Fact]
    public void AtAt_Path_EmitsLayoutAreaElement_NotLiteralText()
    {
        // @@ is a BLOCK-level operator (LayoutAreaMarkdownParser opens on a line beginning with '@'), so the
        // embed sits on its own line — the form a producing control emits for an inline area injection.
        var html = RenderLikeMaui("@@MyNamespace/MyArea");

        html.Should().Contain($"class='{LayoutAreaMarkdownRenderer.LayoutArea}'",
            "the @@ operator must render a layout-area element via LayoutAreaMarkdownRenderer");
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.RawPath}='MyNamespace/MyArea'",
            "the embed must carry the raw path for runtime resolution");
        html.Should().NotContain("@@MyNamespace/MyArea",
            "the @@ reference must be recognised, not dumped as literal text (the old homebrew Label bug)");
    }

    [Fact]
    public void AtAt_AbsoluteAreaVerb_CarriesAddressAndArea()
    {
        // The absolute keyword form resolves its own address/area even without a node path — the same shape
        // the MarkdownViewLogic tests pin, proven here through the raw MAUI pipeline.
        var html = RenderLikeMaui("@@/Acme/area/Search");

        html.Should().Contain($"class='{LayoutAreaMarkdownRenderer.LayoutArea}'");
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.Address}='Acme'");
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.Area}='Search'");
        html.Should().NotContain("@@/Acme", "the @@ reference must not survive as literal text");
    }

    [Fact]
    public void AtAt_Embed_HidesHeaderByDefault_WithCleanRawPath()
    {
        // @@ embeds hide the embedded node's own header/comments/side menu by default. The flag rides
        // as a SEPARATE data-show-header attribute; data-raw-path stays a clean node path (it feeds
        // IMeshCatalog.ResolvePathAsync — a query there would double/break resolution).
        var html = RenderLikeMaui("@@MyNamespace/MyArea");

        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.RawPath}='MyNamespace/MyArea'",
            "the raw path must stay clean (no ?showHeader query) so it resolves as a node path");
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.ShowHeader}='false'",
            "an @@ embed hides the embedded node's header by default");
    }

    [Fact]
    public void AtAt_Embed_AuthorOptsInToHeader_QueryStrippedFromRawPath()
    {
        var html = RenderLikeMaui("@@MyNamespace/MyArea?showHeader=true");

        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.RawPath}='MyNamespace/MyArea'",
            "the ?showHeader flag is parsed out and carried separately, keeping the raw path clean");
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.ShowHeader}='true'",
            "@@node?showHeader=true opts the embedded node's header back in");
        html.Should().NotContain("MyArea?showHeader",
            "the query must not leak into the resolution raw path");
    }

    [Fact]
    public void AtAt_Embed_ExplicitShowHeaderFalse_HidesAndCleansPath()
    {
        var html = RenderLikeMaui("@@MyNamespace/MyArea?showHeader=false");

        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.RawPath}='MyNamespace/MyArea'");
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.ShowHeader}='false'");
    }

    [Fact]
    public void SingleAt_RendersHyperlink_NotInlineArea()
    {
        // Contrast: a single @ is a hyperlink (ucr-link), NOT an inline area embed — proving the pipeline
        // distinguishes @@ (inline) from @ (link), so the @@ recognition above is meaningful.
        var html = RenderLikeMaui("@MyNamespace/MyArea");

        html.Should().Contain($"class='{LayoutAreaMarkdownRenderer.UcrLink}'",
            "a single @ resolves to a UCR hyperlink");
        html.Should().NotContain($"class='{LayoutAreaMarkdownRenderer.LayoutArea}'",
            "a single @ is a link, not an inline layout-area embed");
    }
}
