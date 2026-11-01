using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace MeshWeaver.Layout.Palettes;

/// <summary>Light or dark — the two modes every token set is generated for.</summary>
public enum ThemeMode
{
    /// <summary>Dark text on light surfaces.</summary>
    Light,

    /// <summary>Light text on dark surfaces.</summary>
    Dark,
}

/// <summary>One CSS custom property of the platform design system: <c>--mw-…</c> and its value.</summary>
/// <param name="Name">The property name, including the leading <c>--</c>.</param>
/// <param name="Value">The CSS value.</param>
public sealed record DesignToken(string Name, string Value);

/// <summary>
/// The platform's design tokens — the <c>--mw-*</c> custom properties every renderer styles from —
/// GENERATED from one accent colour and a <see cref="ThemeMode"/> by <see cref="CuspPalette"/>,
/// rather than taken from a component library's theme.
///
/// <para><b>What is generated.</b> Neutrals (surfaces, borders, text) tinted faintly towards the
/// accent hue; the accent fill with its hover and pressed states, the text drawn on it, and accent
/// text for links on every surface; a nine-step accent ramp; success / warning / danger / info,
/// each as fill, text and subtle background; an eight-colour categorical palette for data; and the
/// shape scale. Every text-on-ground pair is SOLVED for its WCAG ratio on the 8-bit colours actually
/// drawn — body text 7:1 (AAA) on every surface, muted text, accent text and text on fills 4.5:1
/// (AA) — so an accent that would fail (a yellow under white text) is moved in lightness until it
/// passes rather than shipped failing.</para>
///
/// <para><b>Fluent.</b> <see cref="FluentAliases"/> maps Fluent UI's own custom properties onto
/// these tokens, so components still rendered by Fluent take the platform's look while they are
/// being replaced. <see cref="Stylesheet"/> writes both.</para>
/// </summary>
public static class DesignTokens
{
    /// <summary>The accent used when none is chosen — the platform blue.</summary>
    public static readonly OklchColor DefaultAccent = OklchColor.FromHex("#2563eb");

    /// <summary>Minimum contrast of body text on every surface (WCAG AAA).</summary>
    public const double BodyTextContrast = 7.0;

    /// <summary>Minimum contrast of muted text, accent text and text on fills (WCAG AA).</summary>
    public const double TextContrast = 4.5;

    /// <summary>Minimum contrast of a strong border or control outline against its surface (WCAG 1.4.11).</summary>
    public const double UiContrast = 3.0;

    private const double NeutralTint = 0.08;

    private static readonly (string Name, double Hue)[] Statuses =
    [
        ("success", 150),
        ("warning", 75),
        ("danger", 27),
        ("info", 250),
    ];

    /// <summary>The <c>--mw-*</c> tokens for <paramref name="accent"/> in <paramref name="mode"/>.</summary>
    public static ImmutableArray<DesignToken> For(OklchColor accent, ThemeMode mode)
    {
        var dark = mode == ThemeMode.Dark;
        var h = accent.H;
        var tokens = ImmutableArray.CreateBuilder<DesignToken>();
        void Add(string name, OklchColor c) => tokens.Add(new DesignToken($"--mw-{name}", c.ToHex()));
        void AddRaw(string name, string value) => tokens.Add(new DesignToken($"--mw-{name}", value));

        // ── Neutrals: faintly tinted towards the accent hue.
        OklchColor Neutral(double l) => CuspPalette.Quantize(CuspPalette.ColorAt(h, l, NeutralTint));
        OklchColor[] surfaces = dark
            ? [Neutral(0.19), Neutral(0.225), Neutral(0.26), Neutral(0.30)]
            : [Neutral(0.975), Neutral(1.0), Neutral(0.955), Neutral(0.93)];
        for (var i = 0; i < surfaces.Length; i++)
            Add($"surface-{i}", surfaces[i]);

        var text = Solve(Neutral(dark ? 0.94 : 0.23), surfaces, BodyTextContrast, NeutralTint);
        var muted = Solve(Neutral(dark ? 0.76 : 0.48), surfaces, TextContrast, NeutralTint);
        Add("text", text);
        Add("text-muted", muted);
        Add("text-disabled", Neutral(dark ? 0.55 : 0.66));
        Add("border", Neutral(dark ? 0.33 : 0.89));
        Add("border-strong", Solve(Neutral(dark ? 0.50 : 0.62), surfaces[..2], UiContrast, NeutralTint));

        // ── Accent.
        AddFill("accent", h, 0.9, dark, surfaces[0], Add);
        Add("accent-text", Solve(CuspPalette.ColorAt(h, dark ? 0.78 : 0.5, 0.9), surfaces, TextContrast, 0.9));
        Add("accent-subtle", CuspPalette.ColorAt(h, dark ? 0.29 : 0.94, 0.45));
        Add("focus", Solve(CuspPalette.ColorAt(h, dark ? 0.78 : 0.5, 0.9), surfaces, UiContrast, 0.9));

        var ramp = CuspPalette.Sequential(9, h, 0.8);
        for (var i = 0; i < ramp.Length; i++)
            Add($"accent-{i + 1}", ramp[ramp.Length - 1 - i]);   // 1 = lightest … 9 = darkest

        // ── Status.
        foreach (var (name, hue) in Statuses)
        {
            AddFill(name, hue, 0.9, dark, surfaces[0], Add);
            Add($"{name}-text", Solve(CuspPalette.ColorAt(hue, dark ? 0.8 : 0.48, 0.9), surfaces, TextContrast, 0.9));
            Add($"{name}-subtle", CuspPalette.ColorAt(hue, dark ? 0.29 : 0.95, 0.5));
        }

        // ── Data: one shared lightness, starting at the accent hue.
        var categorical = CuspPalette.Categorical(8, dark ? 0.72 : 0.58, 0.9, h);
        for (var i = 0; i < categorical.Length; i++)
            Add($"cat-{i + 1}", categorical[i]);

        // ── Shape.
        AddRaw("radius-s", "4px");
        AddRaw("radius-m", "8px");
        AddRaw("radius-l", "12px");
        AddRaw("radius-pill", "999px");
        AddRaw("shadow", dark
            ? "0 1px 2px rgb(0 0 0 / 0.5), 0 4px 16px rgb(0 0 0 / 0.35)"
            : "0 1px 2px rgb(15 23 42 / 0.08), 0 4px 16px rgb(15 23 42 / 0.08)");
        AddRaw("color-scheme", dark ? "dark" : "light");

        return tokens.ToImmutable();
    }

    /// <summary>
    /// Fluent UI's custom properties and the <c>--mw-*</c> token each one now reads. Fluent writes
    /// its tokens at the document root AND inline on individual elements, so <see cref="Stylesheet"/>
    /// applies these on both with <c>!important</c>; the values are fixed per theme, so the
    /// flattening loses only Fluent's per-container recalculation, which the platform look does not
    /// use. A value that is not a <c>var(…)</c> is written as is (Fluent's radii are unitless).
    /// </summary>
    public static readonly ImmutableArray<(string Fluent, string Value)> FluentAliases =
    [
        ("--accent-base-color", "var(--mw-accent)"),
        ("--accent-fill-rest", "var(--mw-accent)"),
        ("--accent-fill-hover", "var(--mw-accent-hover)"),
        ("--accent-fill-active", "var(--mw-accent-active)"),
        ("--accent-fill-focus", "var(--mw-accent)"),
        ("--accent-foreground-rest", "var(--mw-accent-text)"),
        ("--accent-foreground-hover", "var(--mw-accent-text)"),
        ("--accent-foreground-active", "var(--mw-accent-text)"),
        ("--accent-foreground-focus", "var(--mw-accent-text)"),
        ("--accent-stroke-control-rest", "var(--mw-accent-active)"),
        ("--accent-stroke-control-hover", "var(--mw-accent-active)"),
        ("--accent-stroke-control-active", "var(--mw-accent)"),
        ("--accent-stroke-control-focus", "var(--mw-accent-active)"),
        ("--foreground-on-accent-rest", "var(--mw-accent-on)"),
        ("--foreground-on-accent-hover", "var(--mw-accent-on)"),
        ("--foreground-on-accent-active", "var(--mw-accent-on)"),
        ("--foreground-on-accent-focus", "var(--mw-accent-on)"),
        ("--neutral-base-color", "var(--mw-surface-0)"),
        ("--fill-color", "var(--mw-surface-0)"),
        ("--neutral-layer-1", "var(--mw-surface-0)"),
        ("--neutral-layer-2", "var(--mw-surface-2)"),
        ("--neutral-layer-3", "var(--mw-surface-3)"),
        ("--neutral-layer-4", "var(--mw-surface-3)"),
        ("--neutral-layer-card-container", "var(--mw-surface-1)"),
        ("--neutral-layer-floating", "var(--mw-surface-1)"),
        ("--neutral-fill-rest", "var(--mw-surface-1)"),
        ("--neutral-fill-hover", "var(--mw-surface-2)"),
        ("--neutral-fill-active", "var(--mw-surface-3)"),
        ("--neutral-fill-focus", "var(--mw-surface-1)"),
        ("--neutral-fill-input-rest", "var(--mw-surface-1)"),
        ("--neutral-fill-input-hover", "var(--mw-surface-1)"),
        ("--neutral-fill-input-active", "var(--mw-surface-1)"),
        ("--neutral-fill-input-focus", "var(--mw-surface-1)"),
        ("--neutral-fill-secondary-rest", "var(--mw-surface-2)"),
        ("--neutral-fill-secondary-hover", "var(--mw-surface-3)"),
        ("--neutral-fill-secondary-active", "var(--mw-surface-3)"),
        ("--neutral-fill-stealth-rest", "transparent"),
        ("--neutral-fill-stealth-hover", "var(--mw-surface-2)"),
        ("--neutral-fill-stealth-active", "var(--mw-surface-3)"),
        ("--neutral-fill-stealth-focus", "var(--mw-surface-2)"),
        ("--neutral-fill-strong-rest", "var(--mw-border-strong)"),
        ("--neutral-fill-strong-hover", "var(--mw-text-muted)"),
        ("--neutral-fill-strong-active", "var(--mw-text-muted)"),
        ("--neutral-fill-strong-focus", "var(--mw-border-strong)"),
        ("--neutral-foreground-rest", "var(--mw-text)"),
        ("--neutral-foreground-hover", "var(--mw-text)"),
        ("--neutral-foreground-active", "var(--mw-text)"),
        ("--neutral-foreground-focus", "var(--mw-text)"),
        ("--neutral-foreground-hint", "var(--mw-text-muted)"),
        ("--neutral-stroke-rest", "var(--mw-border)"),
        ("--neutral-stroke-hover", "var(--mw-border-strong)"),
        ("--neutral-stroke-active", "var(--mw-border-strong)"),
        ("--neutral-stroke-focus", "var(--mw-border-strong)"),
        ("--neutral-stroke-divider-rest", "var(--mw-border)"),
        ("--neutral-stroke-layer-rest", "var(--mw-border)"),
        ("--neutral-stroke-input-rest", "var(--mw-border-strong)"),
        ("--neutral-stroke-strong-rest", "var(--mw-border-strong)"),
        ("--focus-stroke-outer", "var(--mw-focus)"),
        ("--focus-stroke-inner", "var(--mw-surface-0)"),
        ("--error", "var(--mw-danger)"),
        ("--control-corner-radius", "6"),
        ("--layer-corner-radius", "10"),
    ];

    /// <summary>
    /// The whole stylesheet: a token block per (accent, mode), selected by
    /// <c>:root[data-theme][data-accent]</c> (the first accent is also the default when no
    /// <c>data-accent</c> is set), and the Fluent alias block on the root and on every element that
    /// carries an inline custom property.
    /// </summary>
    /// <param name="accents">Accent colours as <c>#rrggbb</c>; the first is the default.</param>
    public static string Stylesheet(IReadOnlyList<string> accents)
    {
        ArgumentNullException.ThrowIfNull(accents);
        var list = accents.Count == 0 ? [DefaultAccent.ToHex()] : accents.Select(a => OklchColor.FromHex(a).ToHex()).Distinct().ToList();
        var css = new StringBuilder();
        css.AppendLine("/* Generated by MeshWeaver.Layout.Palettes.DesignTokens — do not edit by hand. */");
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            var theme = mode == ThemeMode.Dark ? "dark" : "light";
            for (var i = 0; i < list.Count; i++)
            {
                var selector = $":root[data-theme=\"{theme}\"][data-accent=\"{list[i]}\"]";
                if (i == 0)
                    selector = $":root[data-theme=\"{theme}\"]:not([data-accent]), {selector}";
                css.Append(selector).AppendLine(" {");
                foreach (var token in For(OklchColor.FromHex(list[i]), mode))
                    css.Append("  ").Append(token.Name).Append(": ").Append(token.Value).AppendLine(";");
                css.AppendLine("}");
            }
        }
        css.AppendLine(":root { color-scheme: var(--mw-color-scheme); }");
        css.AppendLine(":root, :root [style*=\"--\"] {");
        foreach (var (fluent, value) in FluentAliases)
            css.Append("  ").Append(fluent).Append(": ").Append(value).AppendLine(" !important;");
        css.AppendLine("}");
        return css.ToString();
    }

    /// <summary>
    /// A fill (<c>--mw-{name}</c>, <c>-hover</c>, <c>-active</c>) with the text drawn on it
    /// (<c>--mw-{name}-on</c>). Light mode: a mid-dark fill under white. Dark mode: a lighter fill
    /// under the page colour. The fill is moved in lightness until its text reaches 4.5:1.
    /// </summary>
    private static void AddFill(string name, double hue, double saturation, bool dark, OklchColor page, Action<string, OklchColor> add)
    {
        var on = dark ? page : OklchColor.FromHex("#ffffff");
        var fill = CuspPalette.EnsureContrast(CuspPalette.ColorAt(hue, dark ? 0.72 : 0.55, saturation), on, TextContrast, saturation);
        var step = dark ? 0.05 : -0.05;
        add(name, fill);
        add($"{name}-hover", CuspPalette.EnsureContrast(CuspPalette.ColorAt(hue, fill.L + step, saturation), on, TextContrast, saturation));
        add($"{name}-active", CuspPalette.EnsureContrast(CuspPalette.ColorAt(hue, fill.L + 2 * step, saturation), on, TextContrast, saturation));
        add($"{name}-on", on);
    }

    /// <summary>Moves <paramref name="color"/> until it reaches <paramref name="ratio"/> against EVERY ground.</summary>
    private static OklchColor Solve(OklchColor color, IReadOnlyList<OklchColor> grounds, double ratio, double saturation)
    {
        var c = CuspPalette.Quantize(color);
        for (var pass = 0; pass < 4; pass++)
        {
            var failing = -1;
            for (var i = 0; i < grounds.Count && failing < 0; i++)
                if (c.ContrastRatio(grounds[i]) < ratio)
                    failing = i;
            if (failing < 0)
                return c;
            c = CuspPalette.EnsureContrast(c, grounds[failing], ratio, saturation);
        }
        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
            $"No lightness of hue {color.H:0.#} reaches {ratio}:1 on every surface."));
    }
}
