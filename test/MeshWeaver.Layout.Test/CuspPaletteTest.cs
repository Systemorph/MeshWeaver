using System.Globalization;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Palettes;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the platform palette engine: the colour maths against published reference values, and the
/// three properties the engine exists to guarantee: every colour in gamut, lightness monotone
/// along a ramp, and one shared lightness across a categorical palette.
/// </summary>
public class CuspPaletteTest
{
    private static readonly OklchColor White = OklchColor.FromHex("#ffffff");
    private static readonly double[] Hues = Enumerable.Range(0, 72).Select(i => i * 5.0).ToArray();

    // Reference OKLCH of the sRGB primaries/secondaries (Ottosson's OKLab, as css-color-4 and culori give them).
    [Theory]
    [InlineData("#ff0000", 0.6280, 0.2577, 29.23)]
    [InlineData("#00ff00", 0.8664, 0.2948, 142.50)]
    [InlineData("#0000ff", 0.4520, 0.3132, 264.05)]
    [InlineData("#ffff00", 0.9680, 0.2110, 109.77)]
    public void FromHex_MatchesReferenceOklch(string hex, double l, double c, double h)
    {
        var color = OklchColor.FromHex(hex);
        Near(l, color.L, 0.0005);
        Near(c, color.C, 0.0005);
        Near(h, color.H, 0.05);
        Assert.Equal(hex, color.ToHex());
    }

    [Theory]
    [InlineData("#ff0000")]
    [InlineData("#00ff00")]
    [InlineData("#0000ff")]
    [InlineData("#ffff00")]
    [InlineData("#00ffff")]
    [InlineData("#ff00ff")]
    public void Cusp_OfAPrimaryHue_IsThatPrimary(string hex)
    {
        // The most saturated colour of a primary/secondary hue IS that primary: one channel 1, one 0.
        var primary = OklchColor.FromHex(hex);
        // Within OklchColor.GamutTolerance's reach: the search may sit a hair past the exact edge.
        var cusp = OklabGamut.Cusp(primary.H);
        Near(primary.L, cusp.L, 0.005);
        Near(primary.C, cusp.C, 0.005);
    }

    [Fact]
    public void FromHex_AcceptsShortFormAndRejectsGarbage()
    {
        Assert.Equal("#ffcc00", OklchColor.FromHex("fc0").ToHex());
        Assert.Throws<FormatException>(() => OklchColor.FromHex("#12345"));
        Assert.Throws<FormatException>(() => OklchColor.FromHex("not a colour"));
    }

    [Fact]
    public void ToCss_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("oklch(62.8% 0.2577 29.23)", OklchColor.FromHex("#ff0000").ToCss());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ColorAt_IsInGamut_ForEveryHueLightnessAndSaturation()
    {
        foreach (var h in Hues)
            for (var l = 0.0; l <= 1.0; l += 0.05)
                foreach (var s in new[] { 0.0, 0.3, 0.6, 1.0 })
                    Assert.True(CuspPalette.ColorAt(h, l, s).IsInSrgbGamut, $"h={h} l={l} s={s}");
    }

    [Fact]
    public void ColorAt_ZeroSaturation_IsNeutral()
    {
        foreach (var h in Hues)
            Assert.Equal(0, CuspPalette.ColorAt(h, 0.5, 0).C, 6);
    }

    [Fact]
    public void ColorAt_FullSaturation_AtTheCuspLightness_ReachesTheCusp()
    {
        foreach (var h in Hues)
        {
            var cusp = OklabGamut.Cusp(h);
            var color = CuspPalette.ColorAt(h, cusp.L, 1);
            Assert.True(color.C >= cusp.C * 0.97, $"h={h}: {color.C} < cusp {cusp.C}");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    public void Sequential_IsDarkToLight_StrictlyMonotone_AndInGamut(int count)
    {
        foreach (var h in Hues)
        {
            var ramp = CuspPalette.Sequential(count, h);
            Assert.Equal(count, ramp.Length);
            for (var i = 1; i < ramp.Length; i++)
                Assert.True(ramp[i].L > ramp[i - 1].L, $"h={h} step {i}");
            Assert.All(ramp, c => Assert.True(c.IsInSrgbGamut));
        }
    }

    [Fact]
    public void SequentialRange_HitsTheRequestedEnds()
    {
        var ramp = CuspPalette.SequentialRange(5, 145, 0.3, 0.95);
        Assert.Equal(0.3, ramp[0].L, 3);
        Assert.Equal(0.95, ramp[^1].L, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    public void Diverging_IsSymmetric_WithTheLightestInTheMiddle(int count)
    {
        var palette = CuspPalette.Diverging(count, 25, 255);
        Assert.Equal(count, palette.Length);
        var side = count / 2;
        for (var i = 0; i < side; i++)
            Near(palette[i].L, palette[count - 1 - i].L, 1e-9);
        for (var i = 1; i < side; i++)
            Assert.True(palette[i].L > palette[i - 1].L, $"arm step {i} not lighter");
        if (count % 2 == 1 && count > 1)
            Assert.True(palette[side].L > palette[side - 1].L, "centre not the lightest");
        Assert.All(palette, c => Assert.True(c.IsInSrgbGamut));
    }

    [Fact]
    public void Categorical_SharesOneLightness_AndSpreadsTheHues()
    {
        var palette = CuspPalette.Categorical(8);
        Assert.All(palette, c => Assert.Equal(0.62, c.L, 6));
        var hues = palette.Select(c => c.H).ToArray();
        Assert.Equal(8, hues.Select(h => Math.Round(h)).Distinct().Count());
        Assert.Equal(255, hues[0], 6);
    }

    [Fact]
    public void Categorical_WithASpan_CoversTheSpanEndToEnd()
    {
        var palette = CuspPalette.Categorical(6, startHue: 40, hueSpan: 110);
        Assert.Equal(40, palette[0].H, 6);
        Assert.Equal(150, palette[^1].H, 6);
    }

    [Fact]
    public void EnsureContrast_ReachesTheRatio_AndKeepsTheHue()
    {
        foreach (var h in Hues)
        {
            var light = CuspPalette.ColorAt(h, 0.8, 0.9);
            var plate = CuspPalette.EnsureContrast(light, White, 4.5);
            Assert.True(plate.ContrastRatio(White) >= 4.5, $"h={h}");
            // The result is 8-bit quantized, which moves the hue by a fraction of a degree.
            if (plate.C > 0.02)
            {
                var drift = Math.Abs(((plate.H - h) % 360 + 540) % 360 - 180);
                Assert.True(drift <= 2, $"h={h}: hue drifted to {plate.H}");
            }
        }
    }

    [Fact]
    public void Diverging_OfOne_HonoursBrightnessAndContrast()
    {
        // Review on #7796: the old single-colour branch always returned white.
        var dark = CuspPalette.Diverging(1, 25, 255, brightness: 0, contrast: 0);
        Near(0, dark[0].L, 1e-9);
        var light = CuspPalette.Diverging(1, 25, 255, brightness: 1, contrast: 0);
        Assert.True(light[0].L > 0.99);
    }

    [Fact]
    public void EnsureContrast_SearchesTowardsTheSideThatCanReachTheRatio()
    {
        // Review on #7796: a neutral L 0.5 on a neutral L 0.55 foreground cannot reach 4.5:1 towards
        // black (max ≈ 4.33) but can towards white (≈ 4.85) — lightness alone picked the wrong side.
        var fg = new OklchColor(0.55, 0, 0);
        var result = CuspPalette.EnsureContrast(new OklchColor(0.5, 0, 0), fg, 4.5, saturation: 0);
        Assert.True(result.ContrastRatio(CuspPalette.Quantize(fg)) >= 4.5);
        Assert.True(result.L > 0.55);
    }

    [Fact]
    public void EnsureContrast_CanStartAtAnEndpoint_AndMovesOnlyAsFarAsNeeded()
    {
        // Review on #7796: white on white used to jump straight to black instead of a mid-grey.
        var white = new OklchColor(1, 0, 0);
        var result = CuspPalette.EnsureContrast(white, white, 4.5, saturation: 0);
        Assert.True(result.ContrastRatio(CuspPalette.Quantize(white)) >= 4.5);
        Assert.True(result.L > 0.4, $"moved further than needed: L={result.L}");
    }

    [Fact]
    public void EnsureContrast_HoldsTheRatio_OnTheHexItReturns()
    {
        // Review on #7796: L 0.568 passed at 4.5016 in floating point but drew as #777777 (4.4781).
        var result = CuspPalette.EnsureContrast(new OklchColor(0.798, 0, 0), White, 4.5, saturation: 0);
        Assert.True(OklchColor.FromHex(result.ToHex()).ContrastRatio(White) >= 4.5, result.ToHex());
        Assert.NotEqual("#777777", result.ToHex());
    }

    [Fact]
    public void EnsureContrast_ReportsAnUnreachableRatio()
    {
        Assert.False(CuspPalette.TryEnsureContrast(new OklchColor(0.5, 0, 0), new OklchColor(0.5, 0, 0), 22, out _));
        Assert.Throws<InvalidOperationException>(() =>
            CuspPalette.EnsureContrast(new OklchColor(0.5, 0, 0), new OklchColor(0.5, 0, 0), 22));
    }

    [Fact]
    public void ChartPalettes_AreGeneratedAndValidHex()
    {
        foreach (var palette in new[] { ChartPalettes.Default, ChartPalettes.Pastel, ChartPalettes.Bold, ChartPalettes.Earth })
        {
            Assert.NotEmpty(palette);
            Assert.Equal(palette.Length, palette.Distinct().Count());
            Assert.All(palette, hex => Assert.Matches("^#[0-9a-f]{6}$", hex));
        }
        Assert.Equal(12, ChartPalettes.Categorical(12).Length);
        Assert.Equal(7, ChartPalettes.Sequential(7).Length);
        Assert.Equal(7, ChartPalettes.Diverging(7).Length);
    }

    private static void Near(double expected, double actual, double tolerance) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected} ± {tolerance}, got {actual}");

    [Fact]
    public void Palettes_AreDeterministic()
    {
        Assert.Equal(CuspPalette.Categorical(10).ToHex(), CuspPalette.Categorical(10).ToHex());
        Assert.Equal(CuspPalette.Diverging(9, 10, 200).ToHex(), CuspPalette.Diverging(9, 10, 200).ToHex());
    }
}
