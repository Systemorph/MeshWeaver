---
Name: Chip Bars, Breadcrumbs & Field Strips
Category: Documentation
Description: Controls.ChipBar, Controls.Breadcrumb and Controls.FieldStrip — a journey or workflow as a row of navigating chips, a small uppercase trail of links, and a compact label-value line for a card body. Pure compositions over Stack, Badge and Label, theme-aware in light and dark mode.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="9" width="6" height="6" rx="3"/><rect x="16" y="9" width="6" height="6" rx="3"/><path d="M9.5 12h5"/><path d="m13 10 2 2-2 2"/></svg>
---

# Chip Bars, Breadcrumbs & Field Strips

Three small helpers in `MeshWeaver.Layout` that every app can use for the same job: showing where a
record stands, how to get back up, and its key facts in one line. They add no new control and no new
renderer view. Each one composes the platform's existing `Stack`, `Badge` and `Label` controls, so it
renders the same in Blazor, React and React Native. All three are pure functions: the same input
gives the same control tree, and a unit test can check that tree without a host.

```csharp --render ChipBarOverview --show-code
Controls.Stack
    .WithVerticalGap(10)
    .WithView(Controls.Breadcrumb(("Underwriting", "/Doc/GUI"), ("Submissions", "/Doc/GUI"), ("Marine cargo 2027", null)))
    .WithView(Controls.ChipBar(
        [
            new Chip("Received", "/Doc/GUI", ChipTone.Success, Glyph: ChipGlyph.Done),
            new Chip("Triage", "/Doc/GUI", ChipTone.Success, Glyph: ChipGlyph.Done),
            new Chip("Pricing", "/Doc/GUI", ChipTone.Warning, "Technical price pending the cat model", ChipGlyph.Current)
                .WithEmphasis(ChipEmphasis.Strong),
            new Chip("Quote", "/Doc/GUI", Glyph: ChipGlyph.Planned).WithEmphasis(ChipEmphasis.Faint),
        ],
        Breadcrumb.Of(("Workflow", "/Doc/GUI"))))
    .WithView(Controls.FieldStrip(("Cedent", "Contoso Re"), ("Line", "Marine"), ("Broker", null), ("Inception", "1 Jan 2027")))
```

---

# Chip bar

`Controls.ChipBar(chips, prefix)` draws a horizontal, wrapping row of chips with a → between each
pair. A `Chip` is DATA: the text, where it navigates, its tone, a tooltip and a leading glyph. The bar
turns each chip into a navigating badge (`Chip.ToControl()`).

```csharp
public sealed record Chip(string Text, string? Href = null, string Tone = ChipTone.Neutral,
    string? Tooltip = null, string? Glyph = null)
{
    public ChipEmphasis Emphasis { get; init; }   // ChipEmphasis.Normal by default
}
```

🚨 **A chip is a navigating BADGE, never a `Controls.Button`.** A button is a `fluent-button`. Its
height, padding and type ramp live in the component's shadow DOM, where an inline style never
reaches. So a "chip" drawn as a button rendered as a full-height button with the chip's padding added
around it, and a bar of six stood taller than the card that held it (measured on the portal). A linked
badge's root element is its anchor, and the badge's style lands there. The pill is therefore sized by
its style alone (`ChipStyle.Css`), and the badge itself draws transparent.

| Piece | Values | What it does |
|---|---|---|
| `Tone` | `ChipTone.Neutral` · `Info` · `Success` · `Warning` · `Danger`, or any CSS colour | The hue: slate, blue, green, amber, red. A tone IS a colour, so a module passes its own (`"#7c3aed"`, `"var(--accent-fill-rest)"`) in the same place. Nothing resolves a tone, so there is no unknown value and no silent fallback. Every tone is mixed into the theme (see below). |
| `Emphasis` | `ChipEmphasis.Normal` · `Strong` · `Faint` (an enum: a closed set, never persisted) | `Strong` is the step that matters now: a 2px border and bold text. `Faint` is a step that claims nothing yet: dashed, barely tinted, in the hint colour whatever its tone. |
| `Glyph` | `ChipGlyph.Done` ✓ · `Current` ● · `Planned` ○, or your own | Leads the label. |
| `Tooltip` | text | The full label. A chip is capped at `ChipStyle.MaxWidth` (280px) and longer text ends in an ellipsis. |

A bar can lead with a breadcrumb (`prefix`, skipped when it has no visible segment) or with a plain
caption, for a page that shows several groups of chips:

```csharp --render ChipBarCaptioned --show-code
Controls.Stack
    .WithVerticalGap(6)
    .WithView(Controls.ChipBar("Platform rollout:",
        [
            new Chip("Project proposal", "/Doc/GUI", ChipTone.Warning, Glyph: ChipGlyph.Done),
            new Chip("Meeting", "/Doc/GUI", ChipTone.Success, Glyph: ChipGlyph.Current).WithEmphasis(ChipEmphasis.Strong),
        ]))
    .WithView(Controls.ChipBar("Data migration:",
        [
            new Chip("Declined", "/Doc/GUI", ChipTone.Danger, Glyph: ChipGlyph.Done),
            new Chip("Own colour", "/Doc/GUI", "#7c3aed", Glyph: ChipGlyph.Done),
        ]))
```

The bar's areas are `Prefix` (or `Caption`), then `Chip0`, `Arrow1`, `Chip1`, and so on. A test can
check them without rendering anything. The bar sets no `Style` of its own, so a caller can add a
margin with `.WithStyle(...)` without losing the layout.

**Map your domain onto chips in your own module.** The helper knows nothing about journeys, stages or
submissions. A module keeps its semantics as a small mapping function, for example a CRM journey step
(category and status) to `(Tone, Emphasis, Glyph)`, or an underwriting submission status to a tone.
The look then stays the same across every app.

---

# Breadcrumb

`Controls.Breadcrumb(segments)` draws a small uppercase trail separated by ›. A segment with an href
is a navigating badge in the hint colour. A segment without one (usually the current page) is a label
in the foreground colour. Blank segments are skipped. The same trail, as data (`Breadcrumb.Of(...)`),
is what a chip bar takes as its prefix.

```csharp --render BreadcrumbTrail --show-code
Controls.Breadcrumb(("Docs", "/Doc"), ("GUI", "/Doc/GUI"), ("Chip bars", null))
```

---

# Field strip

`Controls.FieldStrip(fields)` is a compact one-line "label value · label value" strip for a card
body. A field with a blank value is skipped together with its label, so a card never shows
"Broker: —". Each field is its own small row that never wraps, and the strip wraps only between
fields, so a label always stays with its value. It is built from `Label` controls: use it instead
of a two-column markdown table or a hand-built HTML line.

```csharp --render FieldStripCard --show-code
Controls.FieldStrip(("Premium", "CHF 1.2m"), ("Share", "15 %"), ("Broker", null), ("Expiry", "31 Dec 2027"))
```

---

# Colours that follow the theme

Every colour these helpers render is an INLINE style, and an inline style carries no media query and
no class. A literal hex is therefore a colour for exactly ONE theme: a light-mode palette drawn on the
dark theme gives pale blobs on black and slate-on-black text nobody can read. `light-dark()` does not
help either. It keys off `color-scheme`, which the portal never sets (the portal flips its theme
through attributes and classes on the root element), so it resolves to the light value in dark mode.

What does flip with the theme is the design tokens. So `ThemePalette` mixes a HUE into a token:

- a fill is a tint of the surface the chip sits on: `ThemePalette.Fill(hue)` is
  `color-mix(in srgb, <hue> 16%, var(--neutral-layer-1,#ffffff))`;
- a border is a stronger mix of the hue and the surface (`Stroke`);
- text is the theme's own foreground pulled toward the hue (`Text`), so the contrast comes from the
  theme and only the tint comes from the state.

The result is dark ink on a pale tint in the light theme and pale ink on a deep tint in the dark one,
from one expression, and amber still means "in progress" in both. Use `ThemePalette` for any colour
of your own as well. `ThemePalette.IsThemeAware(css)` is the guard a test walks over every colour a
view renders.

---

# Text and links

Every text (a chip's label, a tooltip, a crumb, a field) travels as a control's data, which the
renderer encodes. Never escape it, and never build markup from it. Every href is held to
`LinkHrefs`, as a `LinkControl` is: a mesh path navigates inside the portal, an external address opens
with `rel="noopener noreferrer"`, and any other scheme draws the badge without a link. The labels are
user-facing, so pass localized strings (`host.Localize(...)`). The separators (→ › ·) and the glyphs
need no translation.

See also: [Badges, Icons & Status](../DisplayControls) for the badge itself, and
[Message Bars & Links](../MessageBarAndLink) for inline links.
