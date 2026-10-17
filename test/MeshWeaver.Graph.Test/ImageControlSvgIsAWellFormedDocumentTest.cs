using System;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 An image-loaded SVG is parsed as XML: without the SVG namespace it renders NOTHING, and an
/// undeclared <c>xlink:</c> prefix makes it invalid. Inline HTML tolerated both, so icons authored
/// that way are common. The raw-HTML seams normalised them (<c>MeshNodeImageHelper.AsSvgDocument</c>);
/// once the node page's icon surfaces moved to <see cref="ImageControl"/>, the image control's shared
/// SVG path (<see cref="ImageSources.SvgDataUri"/>, which every renderer draws svg through) has to
/// do the same — or those icons render blank on the node page, the previews and the picker.
///
/// <para>Each case drives a CONVERTED surface and parses the document the renderer would load,
/// exactly as a browser does. Negative control: against the percent-encode-only
/// <c>SvgDataUri</c> every case fails to parse or parses outside the SVG namespace.</para>
/// </summary>
public class ImageControlSvgIsAWellFormedDocumentTest
{
    /// <summary>An icon that paints its own plate (so plating adds nothing) but declares no namespace.</summary>
    private const string PlatedNoNamespace =
        "<svg viewBox='0 0 24 24'><rect width='24' height='24' fill='#123456'/></svg>";

    /// <summary>The same, using an undeclared <c>xlink:</c> prefix.</summary>
    private const string PlatedUndeclaredXlink =
        "<svg viewBox='0 0 24 24'><rect width='24' height='24' fill='#123456'/>"
        + "<defs><path id='m' d='M0 0h4v4z'/></defs><use xlink:href='#m'/></svg>";

    [Theory]
    [InlineData(PlatedNoNamespace, "tile")]
    [InlineData(PlatedNoNamespace, "preview")]
    [InlineData(PlatedNoNamespace, "picker")]
    [InlineData(PlatedNoNamespace, "title")]
    [InlineData(PlatedUndeclaredXlink, "tile")]
    [InlineData(PlatedUndeclaredXlink, "preview")]
    [InlineData(PlatedUndeclaredXlink, "picker")]
    [InlineData(PlatedUndeclaredXlink, "title")]
    public void TheDocumentAnImageControlLoads_IsWellFormedSvg(string svg, string surface)
    {
        UiControl control = surface switch
        {
            "tile" => MeshNodeLayoutAreas.BuildClickableIcon(null!, null, svg, svg, false),
            "preview" => CreateLayoutArea.BuildIconPreview(svg),
            "picker" => NodeIconPickerDialog.BuildPreviewTile(svg, svg),
            _ => OverviewLayoutArea.TitleIcon(OverviewLayoutArea.TitleIconSource(svg)!, "Space/Page"),
        };
        var markup = RenderedImage.SvgMarkup(control, surface);
        var start = markup.IndexOf("data:image/svg+xml,", StringComparison.Ordinal) + "data:image/svg+xml,".Length;
        var document = Uri.UnescapeDataString(markup[start..markup.IndexOf('"', start)]);

        var parsed = System.Xml.Linq.XDocument.Parse(document);

        parsed.Root!.Name.Should().Be(System.Xml.Linq.XName.Get("svg", "http://www.w3.org/2000/svg"),
            $"the {surface} must load a standalone SVG document, or the icon renders blank");
        document.Should().Contain("fill='#123456'", "the authored icon is otherwise untouched");
    }

    /// <summary>A document that already declares both is passed through byte-identical.</summary>
    [Fact]
    public void ADeclaredDocument_IsLeftAlone()
    {
        const string declared = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 24 24\"><use xlink:href=\"#a\"/></svg>";
        ImageSources.AsSvgDocument(declared).Should().Be(declared);
        ImageSources.AsSvgDocument("<svg aria-label='xmlns=x' viewBox='0 0 1 1'/>").Should()
            .Contain("xmlns=\"http://www.w3.org/2000/svg\"", "text inside a value is not a declaration");
    }
}
