---
Name: Design Tokens
Category: Documentation
Description: The platform's own design system. The --mw-* CSS custom properties (surfaces, text, accent, status, data colours, shape) are generated from the user's accent by CuspPalette, contrast-solved for WCAG, and mapped onto Fluent UI's properties while Fluent components are being replaced.
Icon: <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="3.5"/><rect x="3" y="14" width="7" height="7" rx="3.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/></svg>
---

# Design Tokens

The portal is moving away from Fluent UI's look, and later from its components. The first step is
a design system of our own: a set of `--mw-*` CSS custom properties that every stylesheet and
component styles from. `DesignTokens` in `MeshWeaver.Layout.Palettes` **generates** them from one
accent colour and a mode (light or dark), using the palette engine ([Colour Palettes](ColorPalettes)).
Nothing is hand-picked, and every accent a user can choose gets a complete, legible theme.

## The tokens

| Group | Tokens | What they are for |
|---|---|---|
| Surfaces | `--mw-surface-0` … `-3` | The page, cards and panels, hover, pressed/selected. Neutral, faintly tinted towards the accent hue. |
| Text | `--mw-text`, `-muted`, `-disabled` | Body text (7:1 on every surface), secondary text (4.5:1), disabled. |
| Lines | `--mw-border`, `--mw-border-strong`, `--mw-focus` | Dividers; control outlines and the focus ring (3:1). |
| Accent | `--mw-accent`, `-hover`, `-active`, `-on`, `-text`, `-subtle` | Primary buttons and the text on them (4.5:1), links on any surface (4.5:1), tinted backgrounds. |
| Accent ramp | `--mw-accent-1` … `-9` | Lightest to darkest, for charts, heat and progress. |
| Status | `--mw-{success,warning,danger,info}`, plus `-hover`, `-active`, `-on`, `-text`, `-subtle` | The same shape as the accent, at fixed hues. |
| Data | `--mw-cat-1` … `-8` | Categorical series colours at one lightness, starting at the accent hue. |
| Shape | `--mw-radius-{s,m,l,pill}`, `--mw-shadow` | 4 / 8 / 12 px and pill; one elevation shadow per mode. |

**Every contrast is solved, not hoped for.** If an accent would fail a ratio, for example a yellow
under white text, the generator moves its lightness until the ratio holds. The check runs on the
8-bit hex that ships, not on the floating-point colour. `DesignTokensTest` covers every Office
accent the Appearance picker offers, plus pure yellow and grey, in both modes.

## Writing CSS

Read the tokens; never write a colour literal:

```css
.my-panel {
  background: var(--mw-surface-1);
  color: var(--mw-text);
  border: 1px solid var(--mw-border);
  border-radius: var(--mw-radius-m);
}
.my-panel a { color: var(--mw-accent-text); }
```

## How the stylesheet is selected

`DesignTokens.Stylesheet(accents)` writes one block per accent and mode, selected by
`:root[data-theme="light|dark"][data-accent="#rrggbb"]`. The first accent is also the default
when no `data-accent` is set. The portal stamps both attributes on `<html>` before first paint.

## Fluent, during the transition

Fluent UI computes its own colour variables in JavaScript and writes them in two places: once at
the document root, and inline on individual elements. `DesignTokens.FluentAliases` maps each
Fluent variable to a token. The stylesheet applies the aliases with `!important` on `:root` and on
every element carrying an inline custom property (`[style*="--"]`), which beats both of Fluent's
writes. This was verified against the running portal: a Fluent accent button resolves
`--accent-fill-rest` to the generated accent in both modes.

The only thing lost is Fluent's per-container recalculation. The platform look does not use it.
Once the last Fluent component is gone, the alias block is deleted.
