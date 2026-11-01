using MeshWeaver.Layout.Palettes;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the platform design tokens: for every accent a user can pick (Fluent's Office colours, a
/// hard yellow included) and in both modes, every text-on-ground pair meets its WCAG ratio ON THE
/// HEX THAT SHIPS, and the stylesheet carries the default selector and the Fluent alias block.
/// </summary>
public class DesignTokensTest
{
    // Fluent's OfficeColor accents — the values the Appearance picker offers today.
    public static TheoryData<string, ThemeMode> Accents()
    {
        var data = new TheoryData<string, ThemeMode>();
        string[] hexes =
        [
            "#2563eb", "#a4373a", "#00a99d", "#0078d4", "#217346", "#00bcf2", "#d83b01", "#7719aa",
            "#0f6cbd", "#31752f", "#742774", "#f2c811", "#b7472a", "#077568", "#bc1948", "#008272",
            "#6264a7", "#3955a3", "#2b579a", "#106ebe", "#ffff00", "#808080",
        ];
        foreach (var hex in hexes)
        {
            data.Add(hex, ThemeMode.Light);
            data.Add(hex, ThemeMode.Dark);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Accents))]
    public void EveryTextOnGroundPair_MeetsItsRatio(string accent, ThemeMode mode)
    {
        var t = DesignTokens.For(OklchColor.FromHex(accent), mode).ToDictionary(x => x.Name, x => x.Value);
        OklchColor C(string name) => OklchColor.FromHex(t["--mw-" + name]);
        string[] surfaces = ["surface-0", "surface-1", "surface-2", "surface-3"];

        foreach (var s in surfaces)
        {
            AtLeast(C("text"), C(s), DesignTokens.BodyTextContrast, $"text on {s}");
            AtLeast(C("text-muted"), C(s), DesignTokens.TextContrast, $"text-muted on {s}");
            AtLeast(C("accent-text"), C(s), DesignTokens.TextContrast, $"accent-text on {s}");
        }
        AtLeast(C("border-strong"), C("surface-0"), DesignTokens.UiContrast, "border-strong on surface-0");
        AtLeast(C("focus"), C("surface-0"), DesignTokens.UiContrast, "focus ring on surface-0");

        foreach (var fill in new[] { "accent", "success", "warning", "danger", "info" })
        {
            foreach (var state in new[] { "", "-hover", "-active" })
                AtLeast(C(fill + "-on"), C(fill + state), DesignTokens.TextContrast, $"text on {fill}{state}");
            if (fill != "accent")
                foreach (var s in surfaces)
                    AtLeast(C(fill + "-text"), C(s), DesignTokens.TextContrast, $"{fill}-text on {s}");
        }
    }

    [Theory]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void Surfaces_StepAwayFromThePage_InTheModesDirection(ThemeMode mode)
    {
        var t = DesignTokens.For(DesignTokens.DefaultAccent, mode).ToDictionary(x => x.Name, x => x.Value);
        var page = OklchColor.FromHex(t["--mw-surface-0"]).L;
        var hover = OklchColor.FromHex(t["--mw-surface-2"]).L;
        var pressed = OklchColor.FromHex(t["--mw-surface-3"]).L;
        if (mode == ThemeMode.Dark)
            Assert.True(page < hover && hover < pressed, "dark: hover and pressed lift towards light");
        else
            Assert.True(page > hover && hover > pressed, "light: hover and pressed sink towards dark");
    }

    [Fact]
    public void Ramp_RunsLightToDark_AndCategoricalStartsAtTheAccentHue()
    {
        var accent = OklchColor.FromHex("#217346");
        var t = DesignTokens.For(accent, ThemeMode.Light).ToDictionary(x => x.Name, x => x.Value);
        for (var i = 2; i <= 9; i++)
            Assert.True(OklchColor.FromHex(t[$"--mw-accent-{i}"]).L < OklchColor.FromHex(t[$"--mw-accent-{i - 1}"]).L, $"accent-{i}");
        var first = OklchColor.FromHex(t["--mw-cat-1"]);
        Assert.True(Math.Abs(((first.H - accent.H) % 360 + 540) % 360 - 180) <= 2);
    }

    [Fact]
    public void Stylesheet_HasAModeBlockPerAccent_TheDefaultSelector_AndTheFluentAliases()
    {
        var css = DesignTokens.Stylesheet(["#2563eb", "#217346"]);
        Assert.Contains(":root[data-theme=\"light\"]:not([data-accent]), :root[data-theme=\"light\"][data-accent=\"#2563eb\"] {", css);
        Assert.Contains(":root[data-theme=\"dark\"][data-accent=\"#217346\"] {", css);
        Assert.DoesNotContain(":root[data-theme=\"dark\"]:not([data-accent]), :root[data-theme=\"dark\"][data-accent=\"#217346\"]", css);
        Assert.Contains(":root, :root [style*=\"--\"] {", css);
        foreach (var (fluent, value) in DesignTokens.FluentAliases)
            Assert.Contains($"{fluent}: {value} !important;", css);
        Assert.Equal(css, DesignTokens.Stylesheet(["#2563eb", "#217346"]));
    }

    [Fact]
    public void Stylesheet_ForAnAddedAccent_WritesOnlyItsBlocks()
    {
        var css = DesignTokens.Stylesheet(["#ff8800"], includeDefaultAndAliases: false);
        Assert.Contains(":root[data-theme=\"light\"][data-accent=\"#ff8800\"] {", css);
        Assert.Contains(":root[data-theme=\"dark\"][data-accent=\"#ff8800\"] {", css);
        Assert.DoesNotContain(":not([data-accent])", css);
        Assert.DoesNotContain("!important", css);
    }

    [Fact]
    public void EveryAliasTarget_IsADefinedToken()
    {
        var names = DesignTokens.For(DesignTokens.DefaultAccent, ThemeMode.Light).Select(t => t.Name).ToHashSet();
        foreach (var (fluent, value) in DesignTokens.FluentAliases)
        {
            if (!value.StartsWith("var(", StringComparison.Ordinal))
                continue;
            var target = value["var(".Length..^1];
            Assert.True(names.Contains(target), $"{fluent} reads {target}, which no token defines");
        }
    }

    private static void AtLeast(OklchColor fg, OklchColor bg, double ratio, string what) =>
        Assert.True(fg.ContrastRatio(bg) >= ratio, $"{what}: {fg.ToHex()} on {bg.ToHex()} = {fg.ContrastRatio(bg):0.00} < {ratio}");
}
