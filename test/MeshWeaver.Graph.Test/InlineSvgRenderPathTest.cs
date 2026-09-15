using System.Text.RegularExpressions;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 EVERY RENDER PATH IN THIS REPO PLATES — driven, not asserted about.
///
/// <para><b>The defect (#4350).</b> The backplate policy existed, was correct, and was tested; what
/// nothing tested was whether the surfaces that draw an icon actually go through it. Six did not.
/// An icon in the ordinary house form — a <c>currentColor</c> outline with no plate of its own —
/// took the surrounding text color and rendered INVISIBLY on one of the two themes: measured
/// 2026-09-14 on ~20 nodes of one Space, and before that as the AppleMusic mark on 2026-08-22,
/// which is the incident <c>IconBackplate</c> was written for.</para>
///
/// <para><b>Why these tests and not a test of the helper.</b> <c>IconBackplateTest</c> already pins
/// <c>Ensure</c>, and it passed throughout — the helper was never the broken part. So each test
/// here drives the ACTUAL renderer with the exact icon shape that vanished and reads the markup it
/// produced. The pairing matters: <c>InlineSvgEmissionBackplateGuard</c> proves no surface goes
/// around the seams, and these prove the seams plate; either alone would be satisfiable by code
/// that renders an invisible icon.</para>
/// </summary>
public class InlineSvgRenderPathTest
{
    /// <summary>The shape that vanished: an outline that inherits the surrounding text color, with
    /// no ground of its own. This is what node icons are normally authored as.</summary>
    private const string MonochromeOutline =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"none\" "
        + "stroke=\"currentColor\" stroke-width=\"1.8\"><path d=\"M4 4h16v16H4z\"/></svg>";

    /// <summary>An icon that paints its own full-bleed plate — every authored store mark, every
    /// thread identicon. The policy must leave these exactly as they are.</summary>
    private const string AuthoredPlate =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>"
        + "<rect width='24' height='24' rx='5' fill='#0e7490'/>"
        + "<path d='M6 12h12' stroke='#fff'/></svg>";

    /// <summary>The markup an <c>HtmlControl</c> hands to the page.</summary>
    private static string Html(UiControl control) =>
        Assert.IsType<HtmlControl>(control).Data?.ToString() ?? "";

    /// <summary>
    /// What "plated" means on the page, checked on the RENDERED markup rather than on a helper's
    /// return value: a generated full-bleed plate is there, the glyph survived it, and no
    /// <c>currentColor</c> is left to inherit a theme's text color.
    /// </summary>
    private static void ShouldBePlated(string markup, string because)
    {
        markup.Should().Contain("<rect width='24' height='24' rx='5'", because);
        markup.Should().NotContain("currentColor",
            "a glyph on a plate must be painted white, never inherit the surrounding text color");
        markup.Should().Contain("M4 4h16v16H4z", "the authored glyph itself must survive plating");
    }

    // ── The two seams every surface reaches for ───────────────────────────────────────────────

    [Fact]
    public void TheRawHtmlSizer_Plates()
    {
        var sized = MeshNodeImageHelper.SizeInlineSvg(MonochromeOutline, 48);

        ShouldBePlated(sized, "SizeInlineSvg is what a raw-HTML surface calls, so it is a render seam");
        sized.Should().StartWith("<svg style=\"width: 48px; height: 48px; display: block;\"",
            "the size must still land on the outermost element — the plate");
    }

    [Fact]
    public void TheFillItsBoxSizer_Plates()
        => ShouldBePlated(
            OverviewLayoutArea.SizeInlineSvg(MonochromeOutline),
            "the overview title icon reaches for its own 100%-sizing variant");

    [Fact]
    public void TheClassifyingSeam_Plates()
    {
        var renderable = MeshNodeImageHelper.ResolveRenderable(MonochromeOutline);

        renderable.Kind.Should().Be(IconRenderKind.InlineSvg);
        ShouldBePlated(renderable.Value, "ResolveRenderable is the seam the view components classify through");
    }

    // ── The five surfaces that were drawing the authored markup as it stood ───────────────────

    [Fact]
    public void TheOverviewTitleIcon_Plates()
        => ShouldBePlated(
            OverviewLayoutArea.RenderNodeIconHtml(MonochromeOutline),
            "the title row draws the node's icon beside its name");

    [Fact]
    public void TheNodePageIconTile_Plates()
        => ShouldBePlated(
            Html(MeshNodeLayoutAreas.BuildClickableIcon(
                host: null!, node: null, iconValue: MonochromeOutline, rawIcon: MonochromeOutline, canEdit: false)),
            "the 56px tile at the head of every node page");

    [Fact]
    public void TheContentSelfReferenceIcon_Plates()
    {
        var markup = Html(MeshNodeLayoutAreas.RenderNodeIcon(
            new MeshNode("Page", "Space") { Icon = MonochromeOutline }, ""));

        ShouldBePlated(markup, "a node that references itself in its own content draws its icon");
        markup.Should().Contain("width: 24px; height: 24px; display: block;",
            "injected bare, a viewBox-only svg renders at the browser's ~300x150 default in a 24px box");
    }

    [Fact]
    public void TheCreateFormIconPreview_Plates()
        => ShouldBePlated(
            Html(CreateLayoutArea.BuildIconPreview(MonochromeOutline)),
            "the preview has to show what the portal will actually draw");

    [Fact]
    public void TheIconPickerPreviewTile_Plates()
        => ShouldBePlated(
            Html(NodeIconPickerDialog.BuildPreviewTile(MonochromeOutline, MonochromeOutline)),
            "choosing an icon means seeing the icon you are choosing");

    // ── The other half of the policy: what already works must not change ──────────────────────

    /// <summary>
    /// An icon that paints its own plate passes through every path byte-identical — one plate, the
    /// authored colors, nothing added. This is what makes the fix safe to apply everywhere at once:
    /// no store mark and no thread identicon renders differently than it did.
    /// </summary>
    [Theory]
    [InlineData("sizer")]
    [InlineData("tile")]
    [InlineData("preview")]
    [InlineData("picker")]
    public void AnIconWithItsOwnPlate_PassesThroughUntouched(string path)
    {
        var markup = path switch
        {
            "sizer" => MeshNodeImageHelper.SizeInlineSvg(AuthoredPlate, 48),
            "tile" => Html(MeshNodeLayoutAreas.BuildClickableIcon(null!, null, AuthoredPlate, AuthoredPlate, false)),
            "preview" => Html(CreateLayoutArea.BuildIconPreview(AuthoredPlate)),
            _ => Html(NodeIconPickerDialog.BuildPreviewTile(AuthoredPlate, AuthoredPlate)),
        };

        markup.Should().Contain("fill='#0e7490'", "the authored plate keeps its own hue");
        markup.Should().Contain("M6 12h12", "the authored glyph is untouched");
        Regex.Matches(markup, "<rect ").Count.Should().Be(1,
            "an icon that already has a plate must not be wrapped in a second one");
    }
}
