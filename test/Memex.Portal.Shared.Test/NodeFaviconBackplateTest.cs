using System;
using System.Collections.Immutable;
using System.Linq;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 THE NODE FAVICON GOES THROUGH THE BACKPLATE POLICY — and here it is not a matter of taste:
/// it is the difference between an icon you can see and a black hairline on nothing, served
/// behind a 200 so that nothing reports it (#4350).
///
/// <para><b>The chain.</b> <see cref="SeoResolver.ResolveIconSvg"/> produces the markup; two
/// consumers render it on a ground this process does not control — the
/// <c>data:image/svg+xml</c> href in the page head, and
/// <see cref="IconRasterizer.Render"/> behind <c>/api/icon/{node}.png</c>, which Safari needs
/// because it reads no SVG favicon at all.</para>
///
/// <para><b>What went wrong.</b> That producer handed the AUTHORED markup on unchanged. An icon in
/// the ordinary house form — a <c>currentColor</c> outline with no plate — has no color of its own,
/// and in a <c>data:</c> URI or a rasterizer there is no surrounding text to inherit one from, so
/// it paints in the initial color: black. The rasterizer, in turn, leaves its ground TRANSPARENT
/// precisely BECAUSE its doc comment was told every mark paints its own plate. A black hairline on
/// a transparent ground is the result — invisible in a dark tab strip, invisible on a dark
/// link-preview card, and served behind a 200 so nothing reports it. The plate at the producer is
/// what makes the rasterizer's premise true.</para>
/// </summary>
public class NodeFaviconBackplateTest
{
    /// <summary>The shape node icons are normally authored in — and the shape that vanished.</summary>
    private const string MonochromeOutline =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' "
        + "stroke='currentColor' stroke-width='1.8'><path d='M4 4h16v16H4z'/></svg>";

    private static MeshNode Node(string? icon) =>
        new("Ifrs17", "Store") { NodeType = "Store/Plugin", Icon = icon };

    [Fact]
    public void APlatelessOutline_IsPlatedBeforeItLeavesTheResolver()
    {
        var svg = SeoResolver.ResolveIconSvg(Node(MonochromeOutline));

        Assert.NotNull(svg);
        Assert.Contains("<rect width='24' height='24' rx='5'", svg);
        Assert.DoesNotContain("currentColor", svg);
        Assert.Contains("M4 4h16v16H4z", svg); // the authored glyph itself survives
    }

    /// <summary>The head's <c>data:</c> URI carries exactly what the rasterizer rasterizes — one
    /// resolution, two consumers, so the tab icon and the PNG can never be different pictures.</summary>
    [Fact]
    public void TheDataUriInTheHead_CarriesThatSamePlatedMarkup()
    {
        var icon = SeoResolver.ResolveIcon(Node(MonochromeOutline));

        Assert.NotNull(icon);
        Assert.StartsWith("data:image/svg+xml,", icon.Href);
        Assert.Equal(
            SeoResolver.ResolveIconSvg(Node(MonochromeOutline)),
            Uri.UnescapeDataString(icon.Href["data:image/svg+xml,".Length..]));
    }

    /// <summary>
    /// 🚨 THE USER-VISIBLE HALF, read off the pixels rather than off the markup: the icon now
    /// rasterizes to an OPAQUE, coloured tile — something a tab strip of either shade can show —
    /// instead of the black hairline the next test pins. The rasterizer is two steps downstream of
    /// the resolver, so asserting on the resolver's string alone would not have measured it.
    /// </summary>
    [Fact]
    public void ThatPlatedMarkup_RasterizesToAVisibleIcon()
    {
        var png = IconRasterizer.Render(SeoResolver.ResolveIconSvg(Node(MonochromeOutline))!, 64);

        Assert.NotNull(png);
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        var corner = bitmap.GetPixel(6, 6); // inside the plate, outside the inset glyph
        Assert.Equal(255, corner.Alpha);
        Assert.True(corner.Red + corner.Green + corner.Blue > 60,
            $"the plate is a mid-tone hue a dark tab strip can show, not near-black; found {corner}");
    }

    /// <summary>
    /// 🚨 The hazard itself, pinned so the reason for the plate cannot be optimised away — and
    /// pinned on what actually happens rather than on what is easy to assume. The unplated icon
    /// does NOT fail to render: it renders, and that is the problem. <c>currentColor</c> in a
    /// standalone document has no surrounding text to inherit from, so it resolves to the initial
    /// color — BLACK — and the rasterizer's deliberately transparent ground leaves nothing else.
    /// A black hairline on nothing is invisible in a dark tab strip and in any dark link-preview
    /// card, and it comes back behind a 200, so nothing anywhere reports a problem.
    /// </summary>
    [Fact]
    public void TheSameIconUnplated_RasterizesToABlackHairlineOnNothing()
    {
        var png = IconRasterizer.Render(MonochromeOutline, 64);

        Assert.NotNull(png);
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        var painted = Enumerable
            .Range(0, bitmap.Width)
            .SelectMany(x => Enumerable.Range(0, bitmap.Height).Select(y => bitmap.GetPixel(x, y)))
            .Where(pixel => pixel.Alpha > 0)
            .ToImmutableArray();

        Assert.NotEmpty(painted);
        Assert.All(painted, p => Assert.True(p.Red < 40 && p.Green < 40 && p.Blue < 40,
            $"unplated, every painted pixel is near-black; found {p}"));
        Assert.Equal(0, bitmap.GetPixel(32, 32).Alpha); // the ground a plate would paint is absent
    }

    /// <summary>An icon that paints its own full-bleed plate reaches the head byte-identical — the
    /// promise the resolver's doc comment makes, and the reason this change is safe for every mark
    /// already in the store.</summary>
    [Fact]
    public void AnIconWithItsOwnPlate_IsUnchanged()
    {
        const string authored =
            "<svg viewBox='0 0 24 24'><rect width='24' height='24' fill='#4c1d95'/></svg>";

        Assert.Equal(authored, SeoResolver.ResolveIconSvg(Node(authored)));
    }
}
