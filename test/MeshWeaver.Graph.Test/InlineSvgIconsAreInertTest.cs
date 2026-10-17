using System.Text.RegularExpressions;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 AN ICON IS CONTENT, AND CONTENT NEVER BECOMES PAGE MARKUP.
///
/// <para>A node's <c>Icon</c> is a free string that anyone allowed to update the node can set, and
/// it is drawn for OTHER viewers — on node pages, in previews, pickers and lists. Inline SVG can
/// carry a script element and event-handler attributes. Every raw-HTML render path in this assembly
/// used to inject the (plated, sized) authored markup into the page verbatim. Each now emits an
/// <c>&lt;img&gt;</c> whose source is the icon as a percent-encoded <c>data:</c> URI: an image-loaded SVG
/// runs no script and fires no handler, and nothing in the icon can close the attribute.</para>
///
/// <para>Each test drives the ACTUAL renderer with an icon carrying both kinds of active content and
/// reads the markup the page would receive. <see cref="TheDetector_SeesActiveMarkup"/> is the
/// negative control: the same checks fail on the hostile icon injected as it stands, so a pass
/// below is not a pass by construction.</para>
/// </summary>
public class InlineSvgIconsAreInertTest
{
    /// <summary>An icon with a script element, a handler on the root and a handler on a child. It
    /// paints no plate of its own, so it also exercises the plating path.</summary>
    private const string Hostile =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" onload=\"window.__mw=1\">"
        + "<script>window.__mw=2</script>"
        + "<image href=\"missing.png\" onerror=\"window.__mw=3\"/>"
        + "<path d=\"M4 4h16v16H4z\" stroke=\"currentColor\"/></svg>";

    /// <summary>A tag the page would parse as an element, carrying an event-handler attribute.</summary>
    private static readonly Regex HandlerAttribute =
        new(@"<[a-zA-Z][^>]*\son[a-zA-Z]+\s*=", RegexOptions.CultureInvariant);

    /// <summary>Quoted attribute values — text INSIDE one is data, never an attribute of its own.</summary>
    private static readonly Regex QuotedValue = new("\"[^\"]*\"|'[^']*'", RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="markup"/> parses to an element with an <c>on*</c> attribute:
    /// quoted values are blanked first, so an encoded quote that stays inside its value is not
    /// mistaken for a break-out.</summary>
    private static bool HasHandler(string markup) =>
        HandlerAttribute.IsMatch(QuotedValue.Replace(markup, "\"\""));

    /// <summary>Asserts <paramref name="markup"/> — what the page receives — holds no executable
    /// markup, and that the icon still reaches the page, as an inert image.</summary>
    private static void ShouldBeInert(string markup, string because)
    {
        markup.ToLowerInvariant().Should().NotContain("<script", because);
        HasHandler(markup).Should().BeFalse(because + " — no element may carry an on* handler");
        markup.ToLowerInvariant().Should().NotContain("<svg",
            because + " — the icon must not be injected as an svg element at all");
        markup.Should().Contain("<img src=\"data:image/svg+xml,", because + " — the icon is an image");
        RenderedSvg.Decoded(markup).Should().Contain("M4 4h16v16H4z",
            "the icon itself still renders — inside the image, where none of its active content runs");
    }

    private static string Html(UiControl control) =>
        Assert.IsType<HtmlControl>(control).Data?.ToString() ?? "";

    [Fact]
    public void TheDetector_SeesActiveMarkup()
    {
        // Negative control: injected as authored — what every path below did before — both checks fire.
        Hostile.Should().Contain("<script");
        HasHandler(Hostile).Should().BeTrue();
        HasHandler($"<div>{IconBackplate.Ensure(Hostile)}</div>").Should().BeTrue(
            "plating alone never removed anything — it wraps the markup, handlers and all");
    }

    [Fact]
    public void TheRawHtmlSizer_EmitsAnInertImage()
        => ShouldBeInert(MeshNodeImageHelper.SizeInlineSvg(Hostile, 48), "SizeInlineSvg");

    [Fact]
    public void TheImageHelper_EmitsAnInertImage()
        => ShouldBeInert(MeshNodeImageHelper.InlineSvgImage(Hostile, 20), "InlineSvgImage");

    [Fact]
    public void TheOverviewTitleIcon_IsInert()
        => ShouldBeInert(OverviewLayoutArea.RenderNodeIconHtml(Hostile), "the overview title row");

    [Fact]
    public void TheNodePageIconTile_IsInert()
        => ShouldBeInert(
            Html(MeshNodeLayoutAreas.BuildClickableIcon(null!, null, Hostile, Hostile, false)),
            "the node page's icon tile");

    [Fact]
    public void TheContentSelfReferenceIcon_IsInert()
        => ShouldBeInert(
            MeshNodeLayoutAreas.RenderNodeIconHtml(new MeshNode("Page", "Space") { Icon = Hostile }),
            "a node referencing itself in its content");

    [Fact]
    public void TheCreateFormPreview_IsInert()
        => ShouldBeInert(Html(CreateLayoutArea.BuildIconPreview(Hostile)), "the create-form preview");

    [Fact]
    public void TheIconPickerTile_IsInert()
        => ShouldBeInert(Html(NodeIconPickerDialog.BuildPreviewTile(Hostile, Hostile)), "the icon picker tile");

    /// <summary>The URL form of the same field: a quote in a <c>data:</c>/URL icon must not close
    /// <c>src="…"</c> and open an attribute of its own.</summary>
    [Fact]
    public void AnImageUrlIcon_CannotBreakOutOfItsAttribute()
    {
        const string url = "data:image/png;base64,AAAA\" onerror=\"window.__mw=4";

        var tile = Html(MeshNodeLayoutAreas.BuildClickableIcon(null!, null, url, url, false));
        var picker = Html(NodeIconPickerDialog.BuildPreviewTile(url, url));

        HasHandler(tile).Should().BeFalse("the node page tile encodes the icon URL");
        HasHandler(picker).Should().BeFalse("the picker tile encodes the icon URL");
    }

    /// <summary>An image-loaded svg is parsed as XML and renders nothing without the SVG namespace,
    /// which inline HTML never needed — the data URI supplies it when the author left it out.</summary>
    [Fact]
    public void ADataUri_CarriesTheSvgNamespace()
    {
        const string bare = "<svg viewBox='0 0 24 24'><rect width='24' height='24' fill='#123456'/><use xlink:href='#a'/></svg>";

        var document = Uri.UnescapeDataString(MeshNodeImageHelper.SvgDataUri(bare)["data:image/svg+xml,".Length..]);

        document.Should().StartWith("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=");
        MeshNodeImageHelper.AsSvgDocument(document).Should().Be(document, "declarations already present are left alone");
    }

    /// <summary>
    /// The root tag is read quote-aware: a <c>&gt;</c> inside an attribute value is not the end of the
    /// tag. Read naively, the declaration that IS present looked absent and a second <c>xmlns</c> was
    /// inserted — a document no XML parser (and so no image) accepts. Each case parses the decoded
    /// document as XML, which is exactly what a browser does to an image-loaded SVG.
    /// </summary>
    [Theory]
    [InlineData("<svg aria-label=\"A > B\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><rect width=\"24\" height=\"24\" fill=\"#123456\"/></svg>")]
    [InlineData("<svg aria-label='xmlns=x' viewBox='0 0 24 24'><rect width='24' height='24' fill='#123456'/></svg>")]
    [InlineData("<svg viewBox='0 0 24 24'><rect width='24' height='24' fill='#123456'/><defs><path id='m' d='M0 0h4v4z'/></defs><use xlink:href='#m'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><rect width='24' height='24' fill='#123456'/><use xlink:href='#m'/></svg>")]
    public void TheDataUriDocument_IsWellFormedXml(string svg)
    {
        var document = Uri.UnescapeDataString(MeshNodeImageHelper.SvgDataUri(svg)["data:image/svg+xml,".Length..]);

        var parsed = System.Xml.Linq.XDocument.Parse(document);

        parsed.Root!.Name.Should().Be(System.Xml.Linq.XName.Get("svg", "http://www.w3.org/2000/svg"));
    }

    /// <summary>
    /// The same, through the WHOLE image path — plating included. A plateless icon is nested inside a
    /// generated plate, and the backplate's own root-tag reader must be quote-aware too: read naively,
    /// <c>aria-label="A &gt; B"</c> hid the authored <c>viewBox</c> and a duplicate was inserted.
    /// </summary>
    [Theory]
    [InlineData("<svg aria-label=\"A > B\" viewBox=\"0 0 24 24\"><path d=\"M4 4h16v16H4z\"/></svg>")]
    [InlineData("<svg aria-label=\"A > B\" width=\"24\" height=\"24\"><path d=\"M4 4h16v16H4z\"/></svg>")]
    public void ThePlatedImageDocument_IsWellFormedXml(string svg)
    {
        var src = MeshNodeImageHelper.InlineSvgImageSource(svg);
        var document = Uri.UnescapeDataString(src["data:image/svg+xml,".Length..]);

        var parsed = System.Xml.Linq.XDocument.Parse(document);

        parsed.Root!.Name.Should().Be(System.Xml.Linq.XName.Get("svg", "http://www.w3.org/2000/svg"));
        document.Should().Contain("M4 4h16v16H4z", "the authored glyph survives plating");
    }

    /// <summary>The colour <c>currentColor</c> resolves against, walked up from the first path the way a
    /// renderer inherits it: the nearest <c>color</c> attribute wins.</summary>
    private static string? ResolvedColor(string document)
    {
        var parsed = System.Xml.Linq.XDocument.Parse(document);
        var element = parsed.Descendants().First(e => e.Name.LocalName == "path");
        for (var at = element; at is not null; at = at.Parent)
            if (at.Attribute("color") is { } color)
                return color.Value;
        return null;
    }

    /// <summary>
    /// An explicit colour is supplied as the document's INHERITED DEFAULT — a root <c>color</c> — and
    /// never by rewriting paint: an image-loaded svg cannot inherit from the page, but a colour the icon
    /// declares itself must keep winning, exactly as it did inline.
    /// </summary>
    [Theory]
    // an icon that paints its own plate, no colour of its own: the requested colour applies
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><rect width='24' height='24' fill='#0e7490'/><path d='M6 12h12' stroke='currentColor'/></svg>", "#ff0000")]
    // a colour on a child element wins over the requested one
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><rect width='24' height='24' fill='#0e7490'/><g color='blue'><path d='M6 12h12' stroke='currentColor'/></g></svg>", "blue")]
    // a colour on the root wins over the requested one
    [InlineData("<svg color='blue' xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><rect width='24' height='24' fill='#0e7490'/><path d='M6 12h12' stroke='currentColor'/></svg>", "blue")]
    // a plateless icon is plated first: the plate root takes the colour, the glyph is the plate's white
    [InlineData("<svg viewBox='0 0 24 24' fill='none'><path d='M6 12h12' stroke='currentColor'/></svg>", "#ff0000")]
    public void AnExplicitColor_IsTheInheritedDefault_NeverAPaintRewrite(string svg, string resolved)
    {
        var src = MeshNodeImageHelper.InlineSvgImageSource(svg, "#ff0000");
        var decoded = Uri.UnescapeDataString(src["data:image/svg+xml,".Length..]);

        ResolvedColor(decoded).Should().Be(resolved);
        decoded.Should().NotContain("stroke='#ff0000'", "paint values are never rewritten");
    }

    /// <summary>An alpha colour in the slash form (<c>rgb(255 0 0 / 50%)</c>) is a plain colour and is
    /// applied; a value with a quote is still refused.</summary>
    [Theory]
    [InlineData("rgb(255 0 0 / 50%)", "rgb(255 0 0 / 50%)")]
    [InlineData("hsl(0 100% 50% / .5)", "hsl(0 100% 50% / .5)")]
    [InlineData("red'/><x a='", null)]
    public void AnAlphaColour_IsApplied_AQuotedOneIsNot(string color, string? resolved)
    {
        const string ownPlate = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>"
                                + "<rect width='24' height='24' fill='#0e7490'/><path d='M6 12h12' stroke='currentColor'/></svg>";

        var src = MeshNodeImageHelper.InlineSvgImageSource(ownPlate, color);

        ResolvedColor(Uri.UnescapeDataString(src["data:image/svg+xml,".Length..])).Should().Be(resolved);
    }
}
