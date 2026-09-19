using System.Collections.Immutable;
using SkiaSharp;
using Svg.Skia;

namespace Memex.Portal.Shared.Seo;

/// <summary>
/// Turns a node's <c>&lt;svg&gt;</c> mark into PNG bytes, so the per-content favicon reaches the
/// browsers that cannot read an SVG one.
///
/// <para><b>Why this exists (issue #2075, item 3).</b> The SEO head emits the node's own icon as an
/// <c>image/svg+xml</c> <c>data:</c> URI (<see cref="SeoResolver.ResolveIcon"/>). <b>Safari renders
/// no SVG favicon at all</b> — not from a data URI, not from a URL — so on macOS and iOS every node
/// page wore the portal favicon and the whole feature was invisible to those users. A feature a
/// whole browser cannot see is not shipped for them. The cure is the standards-based one: declare
/// the SVG *and* a PNG, and let each browser take the one it can read.</para>
///
/// <para><b>Why an SVG parser, and why authored marks decide it.</b> SkiaSharp draws; it does not
/// parse SVG. The portal's icon population is mixed — generated backplates (structured, drawable
/// directly) and AUTHORED markup (arbitrary) — and a census of the store packages found every one
/// of the 56 marks to be authored inline <c>&lt;svg&gt;</c>, the issue's own <c>Chess</c> board
/// among them. Drawing only the generated half would have produced an endpoint Safari honours that
/// still leaves the reported node showing the portal favicon. Hence <c>Svg.Skia</c> beside
/// <c>SkiaSharp</c>; see Doc/Architecture/ContentFaviconRasterization.</para>
///
/// <para><b>No state, deliberately.</b> Unlike <see cref="OgCardRenderer"/> — which holds a decoded
/// typeface — nothing here survives a call: the parse, the surface and the encode are all per
/// request, so there is no instance to own and no cache to scope to a mesh. Repeat cost is carried
/// by the endpoint's strong ETag and <c>max-age</c> instead of by memory the process never frees
/// (AGENTS.md: no static collections, and an unbounded icon cache keyed by node path is exactly
/// one).</para>
/// </summary>
public static class IconRasterizer
{
    /// <summary>The favicon size a browser actually paints in a tab strip.</summary>
    public const int FaviconSize = 32;

    /// <summary>The <c>apple-touch-icon</c> size — Safari's bookmark / Add-to-Dock tile.</summary>
    public const int AppleTouchSize = 180;

    /// <summary>
    /// The sizes the endpoint will render, smallest first. An ALLOW-LIST rather than a clamped
    /// range: the route is anonymous and cacheable, so an unbounded size parameter is an unbounded
    /// number of distinct renders and cache entries for one node. A constant lookup — written once
    /// at type init, never at runtime.
    /// </summary>
    public static readonly ImmutableArray<int> SupportedSizes =
        [16, FaviconSize, 48, 64, 96, 128, AppleTouchSize, 192, 512];

    /// <summary>Whether <paramref name="size"/> is one this rasterizer will render.</summary>
    public static bool IsSupportedSize(int size) => SupportedSizes.Contains(size);

    /// <summary>
    /// Rasterizes <paramref name="svg"/> into a square PNG of <paramref name="size"/> pixels, or
    /// null when the markup carries nothing drawable.
    ///
    /// <para>The mark is fitted to the square by its own <c>viewBox</c> — scaled by the smaller
    /// axis and centred, so a non-square mark keeps its aspect ratio instead of being stretched.
    /// The ground is left TRANSPARENT: a favicon is painted onto whatever the browser's tab strip
    /// or bookmark bar uses, and every mark reaching here already paints its own plate, so
    /// inventing a background here would draw a border around a mark that already has one.</para>
    ///
    /// <para>🚨 That premise is LOAD-BEARING, and it was false (#4350). It holds because
    /// <see cref="SeoResolver.ResolveIconSvg"/> — the one producer of the markup this method
    /// rasterizes — puts the icon through <c>IconBackplate.Ensure</c>. It does NOT hold of authored
    /// markup in general, and the failure it produces is the invisible kind rather than the loud
    /// one: an icon authored as a <c>currentColor</c> outline has no surrounding text to inherit a
    /// color from here, so it paints in the INITIAL color — black — and the transparent ground
    /// leaves nothing else. Measured: a black hairline, every painted pixel under RGB 40, the rest
    /// of the canvas alpha 0. <see cref="IsFullyTransparent"/> does not catch it (pixels WERE
    /// painted), so it is served behind a 200 and simply cannot be seen on a dark tab strip. The
    /// fix is the plate at the producer, never a background invented here — inventing one would
    /// draw a border around every mark that already has its own.</para>
    ///
    /// <para>Pure: same markup and size → same bytes, which is what makes the endpoint's strong
    /// ETag meaningful.</para>
    /// </summary>
    /// <param name="svg">Inline <c>&lt;svg&gt;</c> markup — the node's icon, byte for byte.</param>
    /// <param name="size">The square edge in pixels; see <see cref="SupportedSizes"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size is not positive.</exception>
    public static byte[]? Render(string svg, int size)
    {
        using var image = RenderImage(svg, size);
        if (image is null)
            return null;
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// The svg drawn into a transparent <paramref name="size"/>-pixel square, as an image the
    /// caller composes onto its own canvas — the share card draws the node's mark this way. Null
    /// for markup that does not parse, has no extent, or paints NOTHING (see <see cref="Render"/>),
    /// so a caller can fall back to a picture of its own rather than draw an invisible one.
    /// </summary>
    internal static SKImage? RenderImage(string svg, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        if (string.IsNullOrWhiteSpace(svg))
            return null;

        using var document = SKSvg.CreateFromSvg(svg);
        var picture = document?.Picture;
        // A zero-extent cull rect would divide by zero below. Svg.Skia never actually produces one
        // — an empty root reports 1×1 — so this is a guard against the arithmetic, NOT the
        // "draws nothing" test; that one is made below, on the pixels, where it can be true.
        if (picture is null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
            return null;

        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var bounds = picture.CullRect;
        var scale = Math.Min(size / bounds.Width, size / bounds.Height);
        canvas.Translate(
            (size - (bounds.Width * scale)) / 2f,
            (size - (bounds.Height * scale)) / 2f);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Flush();

        // 🚨 An svg that PARSES but paints nothing — an empty root, a mark whose whole body was
        // stripped, a viewBox that collapses — encodes a perfectly valid, perfectly invisible PNG.
        // Served, that is a blank tab icon behind a 200, which reads as a working endpoint and is
        // the one failure nobody can see. Answer "no icon" instead and let the caller 404.
        if (IsFullyTransparent(surface))
            return null;

        return surface.Snapshot();
    }

    private static bool IsFullyTransparent(SKSurface surface)
    {
        using var pixels = surface.PeekPixels();
        if (pixels is null)
            return false;   // cannot read the buffer → do not CLAIM it is blank
        var span = pixels.GetPixelSpan();
        // Rgba8888: alpha is the fourth byte of every pixel.
        for (var i = 3; i < span.Length; i += 4)
            if (span[i] != 0)
                return false;
        return true;
    }
}
