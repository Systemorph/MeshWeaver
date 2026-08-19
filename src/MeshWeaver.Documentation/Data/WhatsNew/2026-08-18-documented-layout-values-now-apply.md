---
Name: Documented layout values now actually apply
Category: Fix
Description: A CSS length like "8px" or a lowercase alignment like "center" — both straight from the layout docs — used to be dropped silently; they now bind as written.
Icon: Bug
Order: -20260818
---

# Documented layout values now actually apply

The layout documentation has always shown stack gaps as CSS lengths (`"8px"`, `"1rem"`) and
alignments as lowercase words (`"start"`, `"center"`, `"end"`). Following it did not work: the
client-side conversion parsed a gap as a plain integer and an alignment with an exact-case match, so
both of those documented values failed to convert, were replaced by the default, and left an error
line in the log on every render.

Sizes now accept a CSS unit — `px`, `%`, `rem`, `em` and the rest of the CSS set — and alignment and
orientation words are matched without regard to case. Numbers are read the same way everywhere in the
world: `"1.5"` is one-and-a-half whether or not the server happens to sit in a comma-decimal locale.

A value that genuinely cannot be read no longer throws mid-render. It falls back to the property's
default, which is what a viewer already saw, without tearing down the live binding for the rest of the
page.
