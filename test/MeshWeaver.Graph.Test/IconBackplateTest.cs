using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The backplate policy is structural: an inline-svg icon WITHOUT a full-bleed plate gets a
/// generated one at the render seam; one WITH a plate — every authored store mark, every thread
/// identicon — passes through byte-identical. These pin both halves plus the wrapping mechanics
/// (viewBox preservation, sizing-attr removal, currentColor recolor, determinism), because a wrong
/// answer here renders an invisible icon on one theme and nothing errors.
/// </summary>
public class IconBackplateTest
{
    private const string MonochromeOutline =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"none\" "
        + "stroke=\"currentColor\" stroke-width=\"2\"><path d=\"M4 4h16v16H4z\"/></svg>";

    private const string AuthoredPlate =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>"
        + "<rect width='24' height='24' rx='5' fill='#4338CA'/>"
        + "<path d='M10 5.5V10' stroke='#fff'/></svg>";

    [Fact]
    public void AuthoredPlate_PassesThroughUnchanged()
        => IconBackplate.Ensure(AuthoredPlate).Should().BeSameAs(AuthoredPlate);

    [Fact]
    public void ThreadIdenticon_FullBleedRectOnItsOwnCanvas_PassesThroughUnchanged()
    {
        // ThreadIconGenerator writes a 0 0 100 100 canvas with a full-bleed rect — the plate test
        // must measure against the icon's OWN canvas, not a hardcoded 24.
        var identicon = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\">"
                        + "<rect width=\"100\" height=\"100\" fill=\"#e8f4fd\"/>"
                        + "<rect x=\"20\" y=\"20\" width=\"20\" height=\"20\" fill=\"#0078d4\"/></svg>";
        IconBackplate.Ensure(identicon).Should().BeSameAs(identicon);
    }

    [Fact]
    public void MonochromeOutline_GetsPlate_AndWhiteDetail()
    {
        var plated = IconBackplate.Ensure(MonochromeOutline);
        plated.Should().StartWith("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>"
                                  + "<rect width='24' height='24' rx='5'");
        plated.Should().NotContain("currentColor", "on a plate the glyph must be white, not inherit text color");
        plated.Should().Contain("stroke=\"#fff\"");
        // The original glyph is nested, inset, and keeps its own viewBox so path math is untouched.
        plated.Should().Contain("<svg x='3' y='3' width='18' height='18'");
        plated.Should().Contain("viewBox=\"0 0 24 24\"");
        plated.Should().Contain("<path d=\"M4 4h16v16H4z\"/>");
    }

    [Fact]
    public void PlateHue_IsFromThePalette_AndDeterministic()
    {
        var one = IconBackplate.Ensure(MonochromeOutline);
        var two = IconBackplate.Ensure(MonochromeOutline);
        two.Should().Be(one, "the hue is a stable hash of the markup — same icon, same plate, every render");
        IconBackplate.Palette.Should().Contain(hue => one.Contains($"fill='{hue}'"));
    }

    [Fact]
    public void FirstRectWithFillNone_IsNotAPlate()
    {
        // A full-canvas rect with fill='none' paints nothing — the icon still needs a plate.
        var outline = "<svg viewBox='0 0 24 24'><rect width='24' height='24' fill='none' stroke='currentColor'/></svg>";
        IconBackplate.HasBackplate(outline).Should().BeFalse();
        IconBackplate.Ensure(outline).Should().StartWith("<svg xmlns='http://www.w3.org/2000/svg'");
    }

    [Fact]
    public void SmallOrnamentalRect_IsNotAPlate()
        => IconBackplate.HasBackplate(
                "<svg viewBox='0 0 24 24'><rect x='9' y='9' width='6' height='6' fill='#333'/></svg>")
            .Should().BeFalse();

    [Fact]
    public void PercentWidthRect_CountsAsPlate()
        => IconBackplate.HasBackplate(
                "<svg viewBox='0 0 24 24'><rect width='100%' height='100%' fill='#4338ca'/></svg>")
            .Should().BeTrue();

    [Fact]
    public void FullBleedCircle_CountsAsPlate()
        => IconBackplate.HasBackplate(
                "<svg viewBox='0 0 24 24'><circle cx='12' cy='12' r='12' fill='#0f766e'/></svg>")
            .Should().BeTrue();

    [Fact]
    public void ForeignCanvas_KeepsItsViewBox_InsideThePlate()
    {
        var fortyEight = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'>"
                         + "<path d='M8 8h32v32H8z' fill='currentColor'/></svg>";
        var plated = IconBackplate.Ensure(fortyEight);
        plated.Should().Contain("viewBox='0 0 48 48'", "the glyph's own coordinate system must survive");
        plated.Should().Contain("<svg x='3' y='3' width='18' height='18'");
    }

    [Fact]
    public void NoViewBox_SynthesizedFromAuthoredSize()
    {
        var sized = "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'>"
                    + "<path d='M2 2h16v16H2z' fill='currentColor'/></svg>";
        var plated = IconBackplate.Ensure(sized);
        plated.Should().Contain("viewBox='0 0 20 20'", "without a viewBox the nested svg would clip instead of scale");
        // The authored width/height are the wrapper's to own — they must not remain and fight the inset.
        plated.Should().NotContain("width='20'");
    }

    [Fact]
    public void RootAttributes_OtherThanSizing_ArePreserved()
    {
        var branded = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' "
                      + "stroke='#7c3aed' stroke-width='1.6'><path d='M4 4h16'/></svg>";
        var plated = IconBackplate.Ensure(branded);
        plated.Should().Contain("stroke='#7c3aed'");
        plated.Should().Contain("stroke-width='1.6'", "stroke-width must not be mistaken for a width attribute");
        plated.Should().Contain("fill='none'");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("🎯")]
    [InlineData("Document")]
    [InlineData("/static/NodeTypeIcons/box.svg")]
    public void NonSvgValues_PassThrough(string? value)
        => IconBackplate.Ensure(value).Should().Be(value ?? "");

    [Fact]
    public void MalformedSvg_NeverThrows()
    {
        // Icon values are node content — arbitrary text must degrade, not fault the render path.
        Action act = () => IconBackplate.Ensure("<svg><rect");
        act.Should().NotThrow();
    }

    [Fact]
    public void ResolveRenderable_PlatesInlineSvg_AtTheSeam()
    {
        var resolved = MeshNodeImageHelper.ResolveRenderable(MonochromeOutline);
        resolved.Kind.Should().Be(IconRenderKind.InlineSvg);
        resolved.Value.Should().Contain("<rect width='24' height='24' rx='5'");
    }

    [Fact]
    public void ResolveRenderable_LeavesPlatedSvg_Untouched()
        => MeshNodeImageHelper.ResolveRenderable(AuthoredPlate).Value.Should().BeSameAs(AuthoredPlate);

    [Fact]
    public void FaviconGlyph_SitsOnAPlate()
    {
        var link = MeshNodeImageHelper.IconLinkFor("🎯");
        link.Type.Should().Be(MeshNodeImageHelper.SvgMediaType);
        var svg = Uri.UnescapeDataString(link.Href["data:image/svg+xml,".Length..]);
        svg.Should().Contain("<rect width=\"32\" height=\"32\" rx=\"7\"");
        svg.Should().Contain("🎯");
    }
}
