using System;
using System.Linq;
using Memex.Portal.Shared.Seo;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The raster favicon (issue #2075, item 3). Safari renders no SVG favicon at all, so the
/// per-content favicon — which every node page has emitted server-side since 2026-08-11 — was
/// invisible on macOS and iOS: every tab wore the portal mark.
///
/// <para>🚨 These assert on an AUTHORED mark, not on art the portal generates. That is the whole
/// point of the dependency: the icon population is mixed, and drawing only the generated
/// backplates would have shipped an endpoint Safari honours while leaving the issue's own
/// reproduction case — the <c>Chess</c> package, whose mark is an authored board — still showing
/// the portal favicon. A fix that does not cover its own repro is the shape worth refusing.</para>
/// </summary>
public class IconRasterizerTest
{
    /// <summary>
    /// 🚨 THE ISSUE'S OWN REPRO, byte for byte as <c>Chess/index.json</c> carries it: an authored
    /// board (the <c>#f0d9b5</c> / <c>#b58863</c> squares and the knight path), not something this
    /// test made up to be easy to draw.
    /// </summary>
    private const string AuthoredMark =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'>"
        + "<rect width='48' height='48' rx='10' fill='#f0d9b5'/>"
        + "<rect x='6' y='6' width='18' height='18' fill='#b58863'/>"
        + "<rect x='24' y='24' width='18' height='18' fill='#b58863'/>"
        + "<path d='M17 38h16v-3H17zM18 33h14c0-3-1.5-4.5-3-6.5-1.2-1.6-1.6-4-1-6l2.5 1.5c.8.4 1.8.2 "
        + "2.3-.6.5-.9.2-2-.7-2.5L26 15.5c-.3-2-1.8-3.5-4-3.5-1 0-1.6.4-2.2 1.2l-4.3 6.2c-.6.9-.4 "
        + "2.1.4 2.8l2.6 2.1c.7.6 1.7.6 2.4 0l1.6-1.3c.3 1.7-.2 3.5-1.5 5C19.5 30 18 31 18 33z' "
        + "fill='#2b1c12' stroke='#f7ead2' stroke-width='1.1'/></svg>";

    /// <summary>PNG magic + IHDR width/height, read straight out of the bytes — the same reading
    /// a browser does, and the only one that proves this is a PNG rather than "some bytes came
    /// back".</summary>
    private static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.True(png.Length > 24, "a file that short cannot be a PNG");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        int Be(int at) => (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
        return (Be(16), Be(20));
    }

    /// <summary>
    /// The two sizes the head declares: the 32 px favicon Safari will actually read, and the 180 px
    /// <c>apple-touch-icon</c> — a separate channel Safari does NOT fall back to the favicon for.
    /// </summary>
    [Theory]
    [InlineData(32)]
    [InlineData(180)]
    public void AnAuthoredSvgMark_RastersToARealPngAtThatExactSize(int size)
    {
        var png = IconRasterizer.Render(AuthoredMark, size);

        Assert.NotNull(png);
        var (width, height) = PngSize(png);
        Assert.Equal(size, width);
        Assert.Equal(size, height);
    }

    /// <summary>
    /// A PNG of the right dimensions could still be blank. The mark's own ground is
    /// <c>#f0d9b5</c>, so the centre pixel must be opaque and light — proof the authored markup was
    /// PARSED and drawn, not merely allocated as a transparent canvas.
    /// </summary>
    [Fact]
    public void TheAuthoredMarkIsActuallyDrawn_NotABlankCanvasOfTheRightSize()
    {
        var png = IconRasterizer.Render(AuthoredMark, 64);

        Assert.NotNull(png);
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal(64, bitmap.Width);
        var corner = bitmap.GetPixel(32, 6);      // board ground, above the knight
        Assert.Equal(255, corner.Alpha);
        Assert.Equal(0xF0, corner.Red);
        Assert.Equal(0xD9, corner.Green);
        Assert.Equal(0xB5, corner.Blue);
    }

    /// <summary>
    /// The endpoint serves a strong ETag computed from these bytes, so the same mark at the same
    /// size must render identically — otherwise every refetch is a cache miss and the 304 path is
    /// dead code.
    /// </summary>
    [Fact]
    public void TheSameMarkAtTheSameSize_RendersByteIdentically()
        => Assert.Equal(IconRasterizer.Render(AuthoredMark, 32), IconRasterizer.Render(AuthoredMark, 32));

    /// <summary>A non-square mark keeps its aspect ratio, so the output is square by CANVAS, not by
    /// stretching the artwork.</summary>
    [Fact]
    public void ANonSquareMark_IsFittedAndCentred_NotStretched()
    {
        const string wide =
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 16'>"
            + "<rect width='64' height='16' fill='#2563eb'/></svg>";

        var png = IconRasterizer.Render(wide, 32);

        Assert.NotNull(png);
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        // Scaled by the wider axis (32/64), so the band is 8 px tall and centred: the middle row is
        // painted and the top row is not.
        Assert.Equal(255, bitmap.GetPixel(16, 16).Alpha);
        Assert.Equal(0, bitmap.GetPixel(16, 1).Alpha);
    }

    /// <summary>
    /// 🚨 Markup that PARSES but paints nothing must say "no icon" rather than encode a fully
    /// transparent PNG. A blank icon behind a 200 looks exactly like a working endpoint, so it is
    /// the one failure nobody can see — and it is not hypothetical: an empty svg root parses
    /// cleanly and reports a 1×1 cull rect, so a bounds check alone would pass it straight through.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 0 0'></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'></svg>")]
    [InlineData("<svg></svg>")]
    public void MarkupThatPaintsNothing_YieldsNoIconRatherThanABlankOne(string markup)
        => Assert.Null(IconRasterizer.Render(markup, 32));

    /// <summary>The blank test is on the PIXELS, not on the bounds — a mark that paints only a
    /// speck still counts as an icon.</summary>
    [Fact]
    public void AMarkThatPaintsOnlyASpeck_IsStillAnIcon()
        => Assert.NotNull(IconRasterizer.Render(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>"
            + "<circle cx='12' cy='12' r='0.4' fill='#000'/></svg>", 32));

    /// <summary>
    /// The size set is an ALLOW-LIST, not a clamped range: the route is anonymous and
    /// shared-cacheable, so a free-form size is an unbounded number of distinct renders per node.
    /// </summary>
    [Fact]
    public void TheSupportedSizes_AreAnAllowList_CoveringBothDeclaredLinks()
    {
        Assert.True(IconRasterizer.IsSupportedSize(IconRasterizer.FaviconSize));
        Assert.True(IconRasterizer.IsSupportedSize(IconRasterizer.AppleTouchSize));
        Assert.False(IconRasterizer.IsSupportedSize(33));
        Assert.False(IconRasterizer.IsSupportedSize(4096));
        Assert.False(IconRasterizer.IsSupportedSize(0));
    }

    /// <summary>A non-positive size is a caller bug, not an image — it must raise rather than
    /// produce a degenerate canvas.</summary>
    [Fact]
    public void ANonPositiveSize_Raises()
        => Assert.Throws<ArgumentOutOfRangeException>(() => IconRasterizer.Render(AuthoredMark, 0));
}
