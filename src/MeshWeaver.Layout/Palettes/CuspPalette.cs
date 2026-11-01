using System.Collections.Immutable;

namespace MeshWeaver.Layout.Palettes;

/// <summary>
/// The platform's ONE palette engine: generates colour palettes along a curve through each hue's
/// CUSP — the most saturated colour sRGB can show at that hue — after Wijffelaars, van Overveld,
/// van Wijk et al., <i>Generating Color Palettes using Intuitive Parameters</i> (EuroVis 2008), in the
/// OKLCH formulation of David Aerne's CuspHanger (MIT, © David Aerne,
/// https://github.com/meodai/cusphanger), whose curve construction and parameter defaults this
/// follows.
///
/// <para><b>Why a cusp.</b> Per hue the sRGB gamut slice is roughly a triangle: black, white and the
/// cusp. A hand-picked or HSL palette ignores where that cusp sits — yellow's is very light, blue's
/// is dark — so its colours are unevenly bright, unevenly vivid, and out of gamut where the picker
/// guessed wrong. Sampling a smooth curve inside the triangle instead gives every colour in gamut,
/// evenly stepped lightness, and colour that is equally strong <i>for its hue</i>.</para>
///
/// <para><b>The curve.</b> For hue <c>h</c> and saturation <c>s</c>: p0 = black, p2 = white,
/// p1 = cusp; q0 = lerp(p0, p1, s), q2 = lerp(p2, p1, s), q1 = midpoint(q0, q2). The curve is
/// two quadratic Béziers, (p0, q0, q1) then (q1, q2, p2). Lightness is placed on it through the
/// paper's L*(x) = 125 − 125·0.2ˣ with x = (1 − contrast)·brightness + t·contrast.</para>
///
/// <para>Every method is pure and deterministic — the same arguments give the same colours on every
/// process and every deploy, which hashed icon plates and chart series rely on.</para>
/// </summary>
public static class CuspPalette
{
    /// <summary>The default saturation (0 = grey ramp, 1 = through the cusp itself).</summary>
    public const double DefaultSaturation = 0.6;

    /// <summary>The default brightness of a sequential ramp (where its lightness range sits).</summary>
    public const double DefaultBrightness = 0.75;

    /// <summary>The golden angle in degrees — the hue step that keeps any prefix of a categorical palette spread out.</summary>
    public const double GoldenAngle = 137.50776405003785;

    /// <summary>
    /// The default contrast (lightness span) of an <paramref name="count"/>-step ramp: wider for
    /// more steps so neighbours stay distinguishable, capped at 0.88 so the ends are not pure
    /// black and white.
    /// </summary>
    public static double DefaultContrast(int count) => Math.Min(0.88, 0.34 + 0.06 * count);

    /// <summary>
    /// The colour on <paramref name="hue"/>'s cusp curve whose lightness is <paramref name="lightness"/>.
    /// This is the primitive every palette is built from: fix the lightness, let the curve pick the
    /// chroma that hue can carry there.
    /// </summary>
    /// <param name="hue">Hue in degrees.</param>
    /// <param name="lightness">OKLab lightness in [0, 1].</param>
    /// <param name="saturation">0 (grey) to 1 (through the cusp).</param>
    public static OklchColor ColorAt(double hue, double lightness, double saturation = DefaultSaturation)
        => Curve.Of(hue).At(lightness, saturation);

    /// <summary>
    /// One hue's curve with its cusp found ONCE, so an operation that samples many lightnesses of a
    /// fixed hue (a ramp, a contrast walk) pays the cusp search once rather than per sample. It lives
    /// for one call — nothing is cached between operations.
    /// </summary>
    private readonly record struct Curve(double H, double CuspL, double CuspC)
    {
        public static Curve Of(double hue)
        {
            var h = OklchColor.Normalize(hue);
            var (l, c) = OklabGamut.Cusp(h);
            return new Curve(h, l, c);
        }

        public OklchColor At(double lightness, double saturation) => Sample(this, lightness, saturation);
    }

    private static OklchColor Sample(Curve curve, double lightness, double saturation)
    {
        var h = curve.H;
        var s = Math.Clamp(saturation, 0, 1);
        var l = Math.Clamp(lightness, 0, 1);
        var cusp = (L: curve.CuspL, C: curve.CuspC);

        // p0 = (0, 0), p1 = cusp, p2 = (1, 0) in (L, C).
        var q0L = s * cusp.L;
        var q0C = s * cusp.C;
        var q2L = 1 + s * (cusp.L - 1);
        var q2C = s * cusp.C;
        var q1L = (q0L + q2L) / 2;
        var q1C = (q0C + q2C) / 2;

        double chroma;
        if (l <= q1L)
        {
            var u = BezierInverse(0, q0L, q1L, l);
            chroma = Bezier(0, q0C, q1C, u);
        }
        else
        {
            var u = BezierInverse(q1L, q2L, 1, l);
            chroma = Bezier(q1C, q2C, 0, u);
        }

        // The triangle is an inner approximation of the gamut, so this clamp is a guard for the
        // numerically last bit, never a reshaping of the curve.
        chroma = Math.Min(Math.Max(0, chroma), OklabGamut.MaxChroma(l, h));
        return new OklchColor(l, chroma, h);
    }

    /// <summary>
    /// A sequential ramp of <paramref name="count"/> colours on one hue, dark to light, lightness
    /// placed by brightness and contrast (Wijffelaars' parameters).
    /// </summary>
    /// <param name="count">Number of colours.</param>
    /// <param name="hue">Hue in degrees.</param>
    /// <param name="saturation">0 (grey) to 1 (through the cusp).</param>
    /// <param name="brightness">Where the lightness range sits, 0 (dark) to 1 (light).</param>
    /// <param name="contrast">How wide the lightness range is, 0 to 1; defaults to <see cref="DefaultContrast"/>.</param>
    public static ImmutableArray<OklchColor> Sequential(
        int count,
        double hue,
        double saturation = DefaultSaturation,
        double brightness = DefaultBrightness,
        double? contrast = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var c = Math.Clamp(contrast ?? DefaultContrast(count), 0, 1);
        var b = Math.Clamp(brightness, 0, 1);
        var curve = Curve.Of(hue);
        var builder = ImmutableArray.CreateBuilder<OklchColor>(count);
        for (var i = 0; i < count; i++)
        {
            var t = count <= 1 ? 0.5 : (double)i / (count - 1);
            builder.Add(curve.At(LightnessAt((1 - c) * b + t * c), saturation));
        }
        return builder.MoveToImmutable();
    }

    /// <summary>
    /// A sequential ramp spanning an explicit lightness range, dark to light.
    /// </summary>
    /// <param name="count">Number of colours.</param>
    /// <param name="hue">Hue in degrees.</param>
    /// <param name="minLightness">OKLab lightness of the darkest step.</param>
    /// <param name="maxLightness">OKLab lightness of the lightest step.</param>
    /// <param name="saturation">0 (grey) to 1 (through the cusp).</param>
    public static ImmutableArray<OklchColor> SequentialRange(
        int count,
        double hue,
        double minLightness,
        double maxLightness,
        double saturation = DefaultSaturation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        // Map the lightness ends into the paper's x domain so the steps keep its perceptual spacing.
        var x0 = XForLightness(Math.Min(minLightness, maxLightness));
        var x1 = XForLightness(Math.Max(minLightness, maxLightness));
        var curve = Curve.Of(hue);
        var builder = ImmutableArray.CreateBuilder<OklchColor>(count);
        for (var i = 0; i < count; i++)
        {
            var t = count <= 1 ? 0.5 : (double)i / (count - 1);
            builder.Add(curve.At(LightnessAt(x0 + t * (x1 - x0)), saturation));
        }
        return builder.MoveToImmutable();
    }

    /// <summary>
    /// A diverging palette: two sequential arms, <paramref name="hueStart"/> and
    /// <paramref name="hueEnd"/>, dark at the ends and meeting at a light, low-chroma centre
    /// (an odd <paramref name="count"/> gets a centre colour that is the mix of both arms).
    /// </summary>
    public static ImmutableArray<OklchColor> Diverging(
        int count,
        double hueStart,
        double hueEnd,
        double saturation = DefaultSaturation,
        double brightness = DefaultBrightness,
        double? contrast = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            return [];
        // count == 1 takes the general path: side = 0 skips the arm loop (and its division), and the
        // odd-count centre is placed by the caller's brightness and contrast like any other centre.

        var c = Math.Clamp(contrast ?? DefaultContrast(count), 0, 1);
        var b = Math.Clamp(brightness, 0, 1);
        var side = count / 2;
        var start = Curve.Of(hueStart);
        var end = Curve.Of(hueEnd);
        var left = new OklchColor[side];
        var right = new OklchColor[side];
        for (var i = 0; i < side; i++)
        {
            var t = 2.0 * i / (count - 1);
            var l = LightnessAt((1 - c) * b + t * c);
            left[i] = start.At(l, saturation);
            right[i] = end.At(l, saturation);
        }

        var builder = ImmutableArray.CreateBuilder<OklchColor>(count);
        builder.AddRange(left);
        if (count % 2 == 1)
        {
            var l = LightnessAt((1 - c) * b + c);
            builder.Add(Mix(start.At(l, saturation), end.At(l, saturation)));
        }
        for (var i = side - 1; i >= 0; i--)
            builder.Add(right[i]);
        return builder.MoveToImmutable();
    }

    /// <summary>
    /// A categorical palette: <paramref name="count"/> distinct hues at ONE lightness, each with the
    /// chroma its own cusp curve gives it there — so no series shouts louder than another, and
    /// text of one colour reads the same on all of them.
    /// </summary>
    /// <param name="count">Number of colours.</param>
    /// <param name="lightness">OKLab lightness shared by every colour.</param>
    /// <param name="saturation">0 (grey) to 1 (through the cusp).</param>
    /// <param name="startHue">The hue of the first colour.</param>
    /// <param name="hueSpan">The arc the hues are spread over; 360 for the whole wheel.</param>
    public static ImmutableArray<OklchColor> Categorical(
        int count,
        double lightness = 0.62,
        double saturation = 0.9,
        double startHue = 255,
        double hueSpan = 360)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var full = Math.Abs(hueSpan - 360) < 1e-9;
        var step = count <= 1 ? 0 : full ? 360.0 / count : hueSpan / (count - 1);
        var builder = ImmutableArray.CreateBuilder<OklchColor>(count);
        for (var i = 0; i < count; i++)
            builder.Add(ColorAt(startHue + i * step, lightness, saturation));
        return builder.MoveToImmutable();
    }

    /// <summary>
    /// The variant of <paramref name="color"/>'s hue NEAREST in lightness that reaches
    /// <paramref name="minContrast"/> against <paramref name="foreground"/>, judged on the 8-bit
    /// colours actually drawn — the result is already quantized, so its <see cref="OklchColor.ToHex"/>
    /// fill holds the ratio. Lightness is searched both ways, out to pure black and pure white, so a
    /// solution is found whenever one exists; the darker one wins a tie. Used for plates that carry
    /// white or black detail.
    /// </summary>
    /// <returns><c>false</c> (and <c>default</c>) when no lightness of that hue reaches the ratio.</returns>
    public static bool TryEnsureContrast(
        OklchColor color,
        OklchColor foreground,
        double minContrast,
        out OklchColor result,
        double saturation = 0.9)
    {
        var fg = Quantize(foreground);
        var start = Quantize(color);
        if (start.ContrastRatio(fg) >= minContrast)
        {
            result = start;
            return true;
        }

        const double step = 0.0025;
        var curve = Curve.Of(color.H);
        var steps = (int)Math.Ceiling(1 / step);
        for (var k = 1; k <= steps; k++)
        {
            foreach (var direction in (ReadOnlySpan<int>)[-1, 1])
            {
                // Clamped, so the walk reaches the endpoints themselves (L = 0 and L = 1).
                var l = Math.Clamp(color.L + direction * k * step, 0, 1);
                var candidate = Quantize(curve.At(l, saturation));
                if (candidate.ContrastRatio(fg) >= minContrast)
                {
                    result = candidate;
                    return true;
                }
            }
        }

        result = default;
        return false;
    }

    /// <summary>
    /// <see cref="TryEnsureContrast"/>, for a ratio the caller knows is reachable.
    /// </summary>
    /// <exception cref="InvalidOperationException">No lightness of the hue reaches <paramref name="minContrast"/>.</exception>
    public static OklchColor EnsureContrast(OklchColor color, OklchColor foreground, double minContrast, double saturation = 0.9) =>
        TryEnsureContrast(color, foreground, minContrast, out var result, saturation)
            ? result
            : throw new InvalidOperationException(
                $"No lightness of hue {color.H:0.#} reaches {minContrast}:1 against {foreground.ToHex()}.");

    /// <summary>The colour exactly as it is drawn: rounded through its 8-bit <c>#rrggbb</c> form.</summary>
    public static OklchColor Quantize(OklchColor color) => OklchColor.FromHex(color.ToHex());

    /// <summary>Hex strings of <paramref name="colors"/> — the form chart series and SVG fills take.</summary>
    public static string[] ToHex(this IEnumerable<OklchColor> colors) => colors.Select(c => c.ToHex()).ToArray();

    /// <summary>The paper's lightness map for curve parameter <paramref name="x"/> in [0, 1], as OKLab L.</summary>
    internal static double LightnessAt(double x)
    {
        var lStar = 125 - 125 * Math.Pow(0.2, Math.Clamp(x, 0, 1));
        return Math.Clamp(FromLStar(lStar), 0, 1);
    }

    internal static double XForLightness(double lightness)
    {
        var lStar = Math.Clamp(ToLStar(Math.Clamp(lightness, 0, 1)), 0, 100);
        return Math.Log(1 - lStar / 125) / Math.Log(0.2);
    }

    // CIE L* ↔ OKLab L through luminance Y; for neutrals OKLab L = Y^(1/3) exactly.
    private static double FromLStar(double lStar)
    {
        var y = lStar > 8 ? Math.Pow((lStar + 16) / 116, 3) : lStar / 903.2962962962963;
        return Math.Cbrt(y);
    }

    private static double ToLStar(double l)
    {
        var y = l * l * l;
        return y > 216.0 / 24389 ? 116 * Math.Cbrt(y) - 16 : 903.2962962962963 * y;
    }

    private static double Bezier(double b0, double b1, double b2, double t) =>
        (1 - t) * (1 - t) * b0 + 2 * (1 - t) * t * b1 + t * t * b2;

    private static double BezierInverse(double b0, double b1, double b2, double v)
    {
        var d = b0 - 2 * b1 + b2;
        double t;
        if (Math.Abs(d) < 1e-12)
        {
            var span = b2 - b0;
            t = Math.Abs(span) < 1e-12 ? 0 : (v - b0) / span;
        }
        else
        {
            t = (b0 - b1 + Math.Sqrt(Math.Max(0, b1 * b1 - b0 * b2 + d * v))) / d;
        }
        return Math.Clamp(t, 0, 1);
    }

    private static OklchColor Mix(OklchColor a, OklchColor b)
    {
        var ar = a.H * Math.PI / 180;
        var br = b.H * Math.PI / 180;
        var x = (a.C * Math.Cos(ar) + b.C * Math.Cos(br)) / 2;
        var y = (a.C * Math.Sin(ar) + b.C * Math.Sin(br)) / 2;
        var h = OklchColor.Normalize(Math.Atan2(y, x) * 180 / Math.PI);
        var l = (a.L + b.L) / 2;
        return new OklchColor(l, Math.Min(Math.Sqrt(x * x + y * y), OklabGamut.MaxChroma(l, h)), h);
    }
}
