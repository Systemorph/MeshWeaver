using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// The named hues a <see cref="Chip"/> is drawn in. A tone IS a CSS colour — these are the platform's
/// five, and a module passes its own hue (<c>"#7c3aed"</c>, <c>"var(--accent-fill-rest)"</c>) in the
/// same place. Nothing resolves a tone, so there is no value it does not know and no fallback to take:
/// what the chip says is whatever colour the caller chose, MIXED into the reader's theme
/// (<see cref="ThemePalette"/>), never painted as is.
/// </summary>
public static class ChipTone
{
    /// <summary>No verdict — waiting, terminal, unknown. Slate; mixed a little stronger, so a grey chip still reads as a chip.</summary>
    public const string Neutral = ThemePalette.Slate;

    /// <summary>Open, live, informational. Blue.</summary>
    public const string Info = ThemePalette.Blue;

    /// <summary>Done, won, engaged, accepted. Green.</summary>
    public const string Success = ThemePalette.Green;

    /// <summary>In progress, needs attention, prospecting. Amber.</summary>
    public const string Warning = ThemePalette.Amber;

    /// <summary>Lost, declined, failed, blocked. Red.</summary>
    public const string Danger = ThemePalette.Red;
}

/// <summary>
/// How loudly a <see cref="Chip"/> speaks. A closed set, handled exhaustively by
/// <see cref="ChipStyle.For"/> and never persisted or serialised (a chip renders to a badge whose
/// style is already resolved), so it is an enum rather than an open vocabulary.
/// </summary>
public enum ChipEmphasis
{
    /// <summary>A plain chip: a 1px solid border in the tone's hue.</summary>
    Normal,

    /// <summary>The chip that matters NOW (the current step): a 2px border and bold text.</summary>
    Strong,

    /// <summary>A chip that claims nothing yet (a planned step): dashed, barely tinted, in the
    /// theme's hint colour whatever its tone.</summary>
    Faint,
}

/// <summary>The status glyphs a step chip conventionally leads with.</summary>
public static class ChipGlyph
{
    /// <summary>A completed step.</summary>
    public const string Done = "✓";

    /// <summary>The current step.</summary>
    public const string Current = "●";

    /// <summary>A planned step.</summary>
    public const string Planned = "○";
}

/// <summary>
/// Colours for INLINE styles that survive the dark theme. An inline style carries no media query and
/// no class, so a literal hex is a colour for exactly one theme. What does flip with the theme is the
/// design tokens, so every colour here is a HUE mixed into a token: a fill is a tint of the surface the
/// control already sits on, and text is the theme's own foreground pulled toward the hue. Light theme →
/// dark ink on a pale tint; dark theme → pale ink on a deep tint; one expression, and the hue still
/// means the same thing in both.
///
/// <para>🚨 <c>light-dark()</c> does NOT work for this: it keys off <c>color-scheme</c>, which the
/// portal never sets (it flips its theme through attributes and classes on the root element), so it
/// would resolve to the light value in dark mode.</para>
/// </summary>
public static class ThemePalette
{
    /// <summary>The surface a chip sits on — the theme's own layer. The light value is the fallback,
    /// so a missing token degrades to a light rendering, never to an invalid colour.</summary>
    public const string Surface = "var(--neutral-layer-1,#ffffff)";

    /// <summary>The theme's body text colour.</summary>
    public const string Foreground = "var(--neutral-foreground-rest,#1f2937)";

    /// <summary>The theme's secondary text colour — captions, separators, hints.</summary>
    public const string Hint = "var(--neutral-foreground-hint,#64748b)";

    /// <summary>Slate — <see cref="ChipTone.Neutral"/>.</summary>
    public const string Slate = "#94a3b8";

    /// <summary>Blue — <see cref="ChipTone.Info"/>.</summary>
    public const string Blue = "#3b82f6";

    /// <summary>Green — <see cref="ChipTone.Success"/>.</summary>
    public const string Green = "#16a34a";

    /// <summary>Amber — <see cref="ChipTone.Warning"/>.</summary>
    public const string Amber = "#f59e0b";

    /// <summary>Red — <see cref="ChipTone.Danger"/>.</summary>
    public const string Red = "#dc2626";

    /// <summary>A CSS <c>color-mix()</c> of <paramref name="hue"/> into <paramref name="into"/>. Pure.</summary>
    /// <param name="hue">The colour to mix in.</param>
    /// <param name="percent">How much of <paramref name="hue"/>, 0–100.</param>
    /// <param name="into">The token it is mixed into.</param>
    /// <returns>The CSS expression.</returns>
    public static string Mix(string hue, int percent, string into) =>
        $"color-mix(in srgb, {hue} {percent}%, {into})";

    /// <summary>A tint of <paramref name="hue"/> over the current surface — a chip's fill. Pure.</summary>
    /// <param name="hue">The hue.</param>
    /// <param name="percent">How much hue.</param>
    /// <returns>The CSS expression.</returns>
    public static string Fill(string hue, int percent = 16) => Mix(hue, percent, Surface);

    /// <summary>A border: enough hue to name the state, mixed with the surface so it reads as an edge. Pure.</summary>
    /// <param name="hue">The hue.</param>
    /// <param name="percent">How much hue.</param>
    /// <returns>The CSS expression.</returns>
    public static string Stroke(string hue, int percent = 60) => Mix(hue, percent, Surface);

    /// <summary>Text: the theme's foreground pulled toward the hue, so the CONTRAST comes from the
    /// theme and only the tint from the state. Pure.</summary>
    /// <param name="hue">The hue.</param>
    /// <param name="percent">How much hue.</param>
    /// <returns>The CSS expression.</returns>
    public static string Text(string hue, int percent = 32) => Mix(hue, percent, Foreground);

    /// <summary>True when <paramref name="css"/> resolves against the reader's theme (it references a
    /// design token) rather than naming one theme's value. Pure — a test walks every colour a view
    /// renders with it.</summary>
    /// <param name="css">A colour or a style.</param>
    /// <returns>Whether it is theme-aware.</returns>
    public static bool IsThemeAware(string? css) =>
        !string.IsNullOrWhiteSpace(css) && css.Contains("var(--", StringComparison.Ordinal);
}

/// <summary>
/// The look of one chip, as data: the rendering rules of a <see cref="Chip"/>, resolved from its tone
/// and its emphasis. Every colour is a <see cref="ThemePalette"/> mix, so it follows the theme.
/// </summary>
/// <param name="Background">The fill.</param>
/// <param name="Border">The border colour.</param>
/// <param name="Text">The text colour.</param>
/// <param name="BorderWidth">The border width in px.</param>
/// <param name="Dashed">Whether the border is dashed.</param>
/// <param name="Bold">Whether the text is bold.</param>
public sealed record ChipStyle(string Background, string Border, string Text, int BorderWidth, bool Dashed, bool Bold)
{
    /// <summary>The widest a chip draws, in px. A label is often free text (a node's name), so without
    /// a ceiling one long name stretches its bar past the card that holds it; past this it ends in an
    /// ellipsis and the tooltip carries all of it.</summary>
    public const int MaxWidth = 280;

    /// <summary>The style of a chip of <paramref name="tone"/> at <paramref name="emphasis"/>.
    /// <see cref="ChipEmphasis.Faint"/> ignores the tone; <see cref="ChipTone.Neutral"/> is mixed a
    /// little stronger, so a grey chip still reads as a chip. Pure.</summary>
    /// <param name="tone">A CSS colour — one of <see cref="ChipTone"/>, or a module's own.</param>
    /// <param name="emphasis">The emphasis.</param>
    /// <returns>The style.</returns>
    public static ChipStyle For(string tone, ChipEmphasis emphasis) => emphasis switch
    {
        ChipEmphasis.Faint => new(ThemePalette.Fill(ThemePalette.Slate, 8), ThemePalette.Stroke(ThemePalette.Slate, 38),
            ThemePalette.Hint, 1, Dashed: true, Bold: false),
        ChipEmphasis.Strong => Solid(tone, 2, bold: true),
        ChipEmphasis.Normal => Solid(tone, 1, bold: false),
        _ => throw new ArgumentOutOfRangeException(nameof(emphasis), emphasis, "not a ChipEmphasis"),
    };

    private static ChipStyle Solid(string tone, int width, bool bold) =>
        string.Equals(tone, ChipTone.Neutral, StringComparison.OrdinalIgnoreCase)
            ? new(ThemePalette.Fill(tone, 20), ThemePalette.Stroke(tone, 55), ThemePalette.Text(tone, 22), width, false, bold)
            : new(ThemePalette.Fill(tone), ThemePalette.Stroke(tone), ThemePalette.Text(tone), width, false, bold);

    /// <summary>The inline style the chip carries. A linked badge's ROOT is its anchor and the badge's
    /// style lands there, so the pill is sized by this style alone — every dimension a reader sees is
    /// named here. Pure.</summary>
    public string Css =>
        $"background:{Background};border:{BorderWidth}px {(Dashed ? "dashed" : "solid")} {Border};"
        + $"color:{Text};border-radius:999px;padding:{(BorderWidth > 1 ? 2 : 3)}px 10px;font-size:12.5px;line-height:1.35;"
        + $"font-weight:{(Bold ? 700 : 600)};white-space:nowrap;display:inline-block;max-width:{MaxWidth}px;"
        + "overflow:hidden;text-overflow:ellipsis;vertical-align:middle;text-decoration:none;box-sizing:border-box;";
}

/// <summary>
/// One chip of a <see cref="Controls.ChipBar(IEnumerable{Chip}, Breadcrumb)"/> — a step, a status, a
/// related node — as DATA. <see cref="ToControl"/> draws it as a NAVIGATING BADGE.
///
/// <para>🚨 A chip is a badge with <see cref="BadgeControl.Href"/>, never a <c>Controls.Button</c>: a
/// button is a <c>fluent-button</c> whose height, padding and type ramp live in its shadow DOM, where
/// an inline style never reaches, so a "chip" drew as a full-height button with the chip's padding
/// added around it (measured on the portal). A linked badge's root is its anchor and its style lands
/// there, so the pill is sized by <see cref="ChipStyle.Css"/> alone.</para>
/// </summary>
/// <param name="Text">The visible label — free text the renderer encodes. User-facing: pass a localized string.</param>
/// <param name="Href">Where the chip navigates (an absolute mesh path or an http(s) URL); a plain pill when null.</param>
/// <param name="Tone">The hue — one of <see cref="ChipTone"/>, or any CSS colour of a module's own.</param>
/// <param name="Tooltip">The hover text — typically the full label when <see cref="ChipStyle.MaxWidth"/> cuts it.</param>
/// <param name="Glyph">A leading glyph (<see cref="ChipGlyph"/>), or null for none.</param>
public sealed record Chip(
    string Text, string? Href = null, string Tone = ChipTone.Neutral, string? Tooltip = null, string? Glyph = null)
{
    /// <summary>The emphasis; <see cref="ChipEmphasis.Normal"/> by default.</summary>
    public ChipEmphasis Emphasis { get; init; } = ChipEmphasis.Normal;

    /// <summary>Returns a copy at <paramref name="emphasis"/>.</summary>
    /// <param name="emphasis">The emphasis.</param>
    /// <returns>A new <see cref="Chip"/>.</returns>
    public Chip WithEmphasis(ChipEmphasis emphasis) => this with { Emphasis = emphasis };

    /// <summary>The text the chip shows: the glyph, a space and the text — or the text alone. Pure.</summary>
    public string Label => string.IsNullOrEmpty(Glyph) ? Text : $"{Glyph} {Text}";

    /// <summary>The resolved look. Pure.</summary>
    public ChipStyle Style => ChipStyle.For(Tone, Emphasis);

    /// <summary>The chip as the platform's navigating badge. Pure.</summary>
    /// <returns>A <see cref="BadgeControl"/>.</returns>
    public BadgeControl ToControl() =>
        ChipBarLayout.Pill(Label, Href, Style.Css, Tooltip);
}

/// <summary>One segment of a <see cref="Breadcrumb"/>.</summary>
/// <param name="Label">The visible text. User-facing: pass a localized string.</param>
/// <param name="Href">Where it navigates; null for an unlinked segment (usually the current page).</param>
public sealed record BreadcrumbSegment(string Label, string? Href = null);

/// <summary>
/// A small uppercase trail of links separated by ›, as DATA — the lead of a chip bar ("JOURNEY"), or a
/// page's way back up ("CRM › PORTFOLIO › FABRIKAM"). Linked segments draw in the hint colour,
/// unlinked ones in the foreground; blank segments are skipped.
/// </summary>
/// <param name="Segments">The segments, outermost first.</param>
public sealed record Breadcrumb(ImmutableList<BreadcrumbSegment> Segments)
{
    /// <summary>A breadcrumb of <paramref name="segments"/>, outermost first. Pure.</summary>
    /// <param name="segments">(label, href) pairs; a null href is an unlinked segment.</param>
    /// <returns>A new <see cref="Breadcrumb"/>.</returns>
    public static Breadcrumb Of(params (string Label, string? Href)[] segments) =>
        new(segments.Select(s => new BreadcrumbSegment(s.Label, s.Href)).ToImmutableList());

    /// <summary>The segments that draw — those with a non-blank label. Pure.</summary>
    public IEnumerable<BreadcrumbSegment> Visible =>
        Segments.Where(s => !string.IsNullOrWhiteSpace(s.Label));

    /// <summary>The breadcrumb as a horizontal stack of badges and labels. Pure.</summary>
    /// <returns>A <see cref="StackControl"/>.</returns>
    public StackControl ToControl() => ChipBarLayout.Trail(this);
}

/// <summary>
/// The compositions behind <see cref="Controls.ChipBar(IEnumerable{Chip}, Breadcrumb)"/>,
/// <see cref="Controls.Breadcrumb"/> and <see cref="Controls.FieldStrip"/> — pure functions over the
/// platform's Stack, Badge and Label controls. No markup is built here: every piece of text travels as
/// a control's data, which the renderer encodes.
/// </summary>
public static class ChipBarLayout
{
    /// <summary>The separator between chips.</summary>
    public const string ChipSeparator = "→";

    /// <summary>The separator between breadcrumb segments.</summary>
    public const string CrumbSeparator = "›";

    /// <summary>The separator between strip fields.</summary>
    public const string FieldSeparator = "·";

    /// <summary>A chip separator's style.</summary>
    public const string ArrowStyle = $"color:{ThemePalette.Hint};";

    /// <summary>A breadcrumb segment's style; the colour is appended per segment.</summary>
    public const string CrumbStyle =
        "font-size:11px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;text-decoration:none;"
        + "white-space:nowrap;";

    /// <summary>A captioned bar's caption style.</summary>
    public const string CaptionStyle =
        $"font-size:12px;font-weight:600;color:{ThemePalette.Foreground};margin-right:2px;";

    /// <summary>A strip field's label style.</summary>
    public const string FieldLabelStyle = $"font-size:12px;color:{ThemePalette.Hint};";

    /// <summary>A strip field's value style.</summary>
    public const string FieldValueStyle = $"font-size:12.5px;font-weight:600;color:{ThemePalette.Foreground};";

    /// <summary>A strip separator's style.</summary>
    public const string FieldSeparatorStyle = $"font-size:12px;color:{ThemePalette.Hint};";

    /// <summary>Every style here that names a colour — what a theme-awareness guard walks.</summary>
    public static IReadOnlyList<string> ColouredStyles =>
        [ArrowStyle, CaptionStyle, FieldLabelStyle, FieldValueStyle, FieldSeparatorStyle,
         CrumbStyle + $"color:{ThemePalette.Hint};", CrumbStyle + $"color:{ThemePalette.Foreground};"];

    // The gap is a NUMBER (px): the Blazor stack binds it to an int, the React one reads a number
    // as px. No Style is set, so a caller may add its own (a margin) without losing the layout.
    private static StackControl Row(int gap) =>
        Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWrap(true)
            .WithHorizontalGap(gap)
            .WithVerticalAlignment(VerticalAlignment.Center);

    // A row NESTED in another row sizes to its content — a stack is full-width by default, which
    // would put every nested trail or field on a line of its own.
    private static StackControl InlineRow(int gap) => Row(gap).WithWidth("auto");

    /// <summary>A bar of chips, each separated by →, led by <paramref name="prefix"/> when it has a
    /// visible segment. Areas: <c>Prefix</c>, then <c>Chip0</c>, <c>Arrow1</c>, <c>Chip1</c>, … Pure.</summary>
    /// <param name="chips">The chips, in order; null draws an empty bar.</param>
    /// <param name="prefix">An optional breadcrumb before the first chip.</param>
    /// <returns>A horizontal, wrapping <see cref="StackControl"/>.</returns>
    public static StackControl Bar(IEnumerable<Chip>? chips, Breadcrumb? prefix = null)
    {
        var bar = Row(7);
        if (prefix is not null && prefix.Visible.Any())
            bar = bar.WithView(prefix.ToControl(), "Prefix");
        return WithChips(bar, chips);
    }

    /// <summary>A bar of chips led by a plain caption ("Platform rollout:"). Areas: <c>Caption</c>,
    /// then the chips as in <see cref="Bar"/>. Pure.</summary>
    /// <param name="caption">The caption. User-facing: pass a localized string.</param>
    /// <param name="chips">The chips, in order.</param>
    /// <returns>A horizontal, wrapping <see cref="StackControl"/>.</returns>
    public static StackControl CaptionedBar(string caption, IEnumerable<Chip>? chips) =>
        WithChips(Row(7).WithView(Controls.Label(caption).WithStyle(CaptionStyle), "Caption"), chips);

    private static StackControl WithChips(StackControl bar, IEnumerable<Chip>? chips)
    {
        var i = 0;
        foreach (var chip in chips ?? [])
        {
            if (i > 0)
                bar = bar.WithView(Controls.Label(ChipSeparator).WithStyle(ArrowStyle), $"Arrow{i}");
            bar = bar.WithView(chip.ToControl(), $"Chip{i}");
            i++;
        }
        return bar;
    }

    /// <summary>A breadcrumb trail. Areas: <c>Crumb0</c>, <c>Sep1</c>, <c>Crumb1</c>, … Pure.</summary>
    /// <param name="breadcrumb">The trail.</param>
    /// <returns>A horizontal, wrapping <see cref="StackControl"/>.</returns>
    public static StackControl Trail(Breadcrumb breadcrumb)
    {
        var trail = InlineRow(4);
        var i = 0;
        foreach (var segment in breadcrumb.Visible)
        {
            if (i > 0)
                trail = trail.WithView(Controls.Label(CrumbSeparator).WithStyle(CrumbStyle + $"color:{ThemePalette.Hint};"), $"Sep{i}");
            trail = trail.WithView(Crumb(segment), $"Crumb{i}");
            i++;
        }
        return trail;
    }

    private static UiControl Crumb(BreadcrumbSegment segment) =>
        string.IsNullOrWhiteSpace(segment.Href)
            ? Controls.Label(segment.Label).WithStyle(CrumbStyle + $"color:{ThemePalette.Foreground};")
            : Pill(segment.Label, segment.Href, CrumbStyle + $"color:{ThemePalette.Hint};", null);

    /// <summary>
    /// A compact one-line "label value · label value" strip. A field whose value is blank is skipped;
    /// a field with a blank label shows its value alone. Each field is its own area (<c>Field0</c>,
    /// <c>Sep1</c>, <c>Field1</c>, …) holding a <c>Label</c> and a <c>Value</c>, so a wrap never splits
    /// a label from its value. Pure.
    /// </summary>
    /// <param name="fields">(label, value) pairs, in order.</param>
    /// <returns>A horizontal, wrapping <see cref="StackControl"/>.</returns>
    public static StackControl Strip(IEnumerable<(string Label, string? Value)>? fields)
    {
        var strip = Row(6);
        var i = 0;
        foreach (var (label, value) in fields ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (i > 0)
                strip = strip.WithView(Controls.Label(FieldSeparator).WithStyle(FieldSeparatorStyle), $"Sep{i}");
            strip = strip.WithView(Field(label, value), $"Field{i}");
            i++;
        }
        return strip;
    }

    /// <summary>
    /// One field of a strip: a <c>Label</c> area (omitted when <paramref name="label"/> is blank) and a
    /// <c>Value</c> area, sized to its content. 🚨 The field itself NEVER wraps — the strip wraps
    /// BETWEEN fields — so a label cannot land on one line and its value on the next. Pure.
    /// </summary>
    /// <param name="label">The label, or blank for a value alone.</param>
    /// <param name="value">The value.</param>
    /// <returns>A horizontal, non-wrapping <see cref="StackControl"/>.</returns>
    public static StackControl Field(string? label, string value)
    {
        var field = InlineRow(4).WithWrap(false);
        if (!string.IsNullOrWhiteSpace(label))
            field = field.WithView(Controls.Label(label).WithStyle(FieldLabelStyle), "Label");
        return field.WithView(Controls.Label(value).WithStyle(FieldValueStyle), "Value");
    }

    /// <summary>
    /// A styled pill: a badge whose <paramref name="style"/> is the whole look, with a navigating
    /// <c>Href</c> when <paramref name="href"/> is given, its width capped at
    /// <see cref="ChipStyle.MaxWidth"/> with an ellipsis. The badge itself draws transparent and in
    /// the colour the style sets. The renderer refuses an unsafe scheme (<see cref="LinkHrefs"/>) and
    /// draws the badge without a link. Pure.
    /// </summary>
    /// <param name="text">The label — free text the renderer encodes.</param>
    /// <param name="href">The address, or null for a plain pill.</param>
    /// <param name="style">The inline style.</param>
    /// <param name="tooltip">The hover text, or null.</param>
    /// <returns>A <see cref="BadgeControl"/>.</returns>
    public static BadgeControl Pill(string text, string? href, string style, string? tooltip)
    {
        var badge = Controls.Badge(text)
            .WithMaxWidth($"{ChipStyle.MaxWidth}px")
            .WithBackgroundColor("transparent")
            .WithColor("inherit")
            .WithStyle(style);
        if (!string.IsNullOrWhiteSpace(href))
            badge = badge.WithHref(href);
        return string.IsNullOrWhiteSpace(tooltip) ? badge : badge.WithTooltip(tooltip);
    }
}
