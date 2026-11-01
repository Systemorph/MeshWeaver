namespace MeshWeaver.Layout.Palettes;

/// <summary>
/// The sRGB gamut in OKLCH: how much chroma a hue can carry at a lightness, and where its CUSP —
/// the most saturated displayable colour of that hue — sits.
///
/// <para>Both are found numerically against the exact OKLab → linear sRGB transform rather than
/// from a lookup table: the gamut slice of one hue is convex in (L, C) with a single peak, so a
/// bisection on chroma and a golden-section search on lightness converge to well below one 8-bit
/// step. Each call costs a few thousand arithmetic operations — microseconds — so nothing is
/// cached and the type holds no state.</para>
/// </summary>
public static class OklabGamut
{
    private const int Iterations = 48;

    /// <summary>The largest chroma that stays inside sRGB at <paramref name="lightness"/> and <paramref name="hue"/>.</summary>
    public static double MaxChroma(double lightness, double hue)
    {
        if (lightness <= 0 || lightness >= 1)
            return 0;
        double lo = 0, hi = 0.5;
        for (var i = 0; i < Iterations; i++)
        {
            var mid = (lo + hi) / 2;
            if (new OklchColor(lightness, mid, hue).IsInSrgbGamut)
                lo = mid;
            else
                hi = mid;
        }
        return lo;
    }

    /// <summary>The cusp of <paramref name="hue"/>: the (lightness, chroma) of its most saturated sRGB colour.</summary>
    public static (double L, double C) Cusp(double hue)
    {
        const double phi = 0.6180339887498949;
        double a = 0, b = 1;
        var x1 = b - phi * (b - a);
        var x2 = a + phi * (b - a);
        var f1 = MaxChroma(x1, hue);
        var f2 = MaxChroma(x2, hue);
        for (var i = 0; i < Iterations; i++)
        {
            if (f1 < f2)
            {
                a = x1; x1 = x2; f1 = f2;
                x2 = a + phi * (b - a);
                f2 = MaxChroma(x2, hue);
            }
            else
            {
                b = x2; x2 = x1; f2 = f1;
                x1 = b - phi * (b - a);
                f1 = MaxChroma(x1, hue);
            }
        }
        var l = (a + b) / 2;
        return (l, MaxChroma(l, hue));
    }
}
