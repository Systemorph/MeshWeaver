using System.Globalization;

namespace MeshWeaver.Layout.Palettes;

/// <summary>
/// A colour in OKLCH — OKLab (Björn Ottosson, 2020) in polar form: perceptual lightness
/// <see cref="L"/> in [0, 1], chroma <see cref="C"/> (0 = grey, ≈ 0.32 is the most any sRGB colour
/// reaches) and hue <see cref="H"/> in degrees [0, 360).
///
/// <para>The palette engine (<see cref="CuspPalette"/>) works in this space because equal steps in
/// <see cref="L"/> LOOK equal and a hue keeps its identity as lightness changes — neither is true of
/// HSL, where a yellow and a blue at the same "lightness" differ by a factor of six in luminance.</para>
/// </summary>
/// <param name="L">Perceptual lightness, 0 (black) to 1 (white).</param>
/// <param name="C">Chroma, 0 (neutral) upwards.</param>
/// <param name="H">Hue angle in degrees.</param>
public readonly record struct OklchColor(double L, double C, double H)
{
    /// <summary>
    /// How far outside [0, 1] a LINEAR channel may sit and still count as in gamut. OKLab's sRGB
    /// boundary is not exactly straight near blue: along the black → <c>#0000ff</c> edge the red
    /// channel dips to ≈ −0.0007, so a strict test cuts a sliver off blue's cusp (C 0.313 → 0.264).
    /// 0.001 linear is far below one 8-bit step at the dark end and is clipped away on output.
    /// </summary>
    public const double GamutTolerance = 1e-3;

    /// <summary>The colour as linear-light sRGB channels (unclipped; out-of-gamut colours leave [0, 1]).</summary>
    public (double R, double G, double B) ToLinearSrgb()
    {
        var hr = H * Math.PI / 180.0;
        var a = C * Math.Cos(hr);
        var b = C * Math.Sin(hr);

        var l_ = L + 0.3963377774 * a + 0.2158037573 * b;
        var m_ = L - 0.1055613458 * a - 0.0638541728 * b;
        var s_ = L - 0.0894841775 * a - 1.2914855480 * b;

        var l = l_ * l_ * l_;
        var m = m_ * m_ * m_;
        var s = s_ * s_ * s_;

        return (
            +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    /// <summary>Whether the colour can be shown on an sRGB display without clipping.</summary>
    public bool IsInSrgbGamut
    {
        get
        {
            var (r, g, b) = ToLinearSrgb();
            return r >= -GamutTolerance && r <= 1 + GamutTolerance
                && g >= -GamutTolerance && g <= 1 + GamutTolerance
                && b >= -GamutTolerance && b <= 1 + GamutTolerance;
        }
    }

    /// <summary>WCAG relative luminance (0 = black, 1 = white) of the colour, clipped to sRGB.</summary>
    public double RelativeLuminance
    {
        get
        {
            var (r, g, b) = ToLinearSrgb();
            return 0.2126 * Clip(r) + 0.7152 * Clip(g) + 0.0722 * Clip(b);
        }
    }

    /// <summary>The WCAG contrast ratio (1 to 21) between this colour and <paramref name="other"/>.</summary>
    public double ContrastRatio(OklchColor other)
    {
        var a = RelativeLuminance;
        var b = other.RelativeLuminance;
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>The colour as <c>#rrggbb</c> (lower case), clipped to the sRGB gamut.</summary>
    public string ToHex()
    {
        var (r, g, b) = ToLinearSrgb();
        return string.Create(CultureInfo.InvariantCulture, $"#{Encode(r):x2}{Encode(g):x2}{Encode(b):x2}");
    }

    /// <summary>The colour as a CSS Color 4 <c>oklch(L C H)</c> string, culture-invariant.</summary>
    public string ToCss() =>
        string.Create(CultureInfo.InvariantCulture, $"oklch({L * 100:0.##}% {C:0.####} {Normalize(H):0.##})");

    /// <inheritdoc />
    public override string ToString() => ToHex();

    /// <summary>Parses <c>#rgb</c> or <c>#rrggbb</c> (the leading <c>#</c> optional) into OKLCH.</summary>
    /// <exception cref="FormatException">The text is not a 3- or 6-digit hex colour.</exception>
    public static OklchColor FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        // At most ONE optional '#', and digits only: AllowHexSpecifier admits no sign or inner
        // whitespace, so '##123456' and '# 12345' are refused rather than read as a colour.
        var s = hex.Trim();
        if (s.StartsWith('#'))
            s = s[1..];
        if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
            throw new FormatException($"'{hex}' is not a #rgb or #rrggbb colour.");
        return FromSrgb(((rgb >> 16) & 0xff) / 255.0, ((rgb >> 8) & 0xff) / 255.0, (rgb & 0xff) / 255.0);
    }

    /// <summary>Converts gamma-encoded sRGB channels in [0, 1] into OKLCH.</summary>
    public static OklchColor FromSrgb(double r, double g, double b) =>
        FromLinearSrgb(Decode(r), Decode(g), Decode(b));

    /// <summary>Converts linear-light sRGB channels into OKLCH.</summary>
    public static OklchColor FromLinearSrgb(double r, double g, double b)
    {
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);

        var lightness = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        var a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        var bb = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;

        var chroma = Math.Sqrt(a * a + bb * bb);
        var hue = chroma < 1e-9 ? 0 : Normalize(Math.Atan2(bb, a) * 180.0 / Math.PI);
        return new OklchColor(lightness, chroma, hue);
    }

    internal static double Normalize(double hue)
    {
        var h = hue % 360.0;
        return h < 0 ? h + 360.0 : h;
    }

    private static double Clip(double channel) => Math.Clamp(channel, 0, 1);

    private static int Encode(double linear)
    {
        var c = Clip(linear);
        var gamma = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (int)Math.Round(Math.Clamp(gamma, 0, 1) * 255);
    }

    private static double Decode(double gamma) =>
        gamma <= 0.04045 ? gamma / 12.92 : Math.Pow((gamma + 0.055) / 1.055, 2.4);
}
