---
Name: Content blocks no longer ignore the width you set
Category: Fix
Description: A block of ready-made content — a chart, a diagram, an embedded image — silently discarded any width, spacing or styling its author gave it, so a chart asked to fill its column could still render as a sliver a few percent wide.
Icon: Sparkle
Order: -20260813
---

# Content blocks no longer ignore the width you set

Most things on a page are built from the portal's own building blocks — a table, a button, a stack
of cards — and each of those has always honoured the size and styling its author asks for. There is
one more building block for content that is already finished and just needs placing on the page: a
chart drawn elsewhere, a diagram, an embedded picture, a formatted note.

That one quietly threw the styling away. An author could set a width, a margin or a colour on it and
nothing at all would happen — no warning, no error, no hint that the instruction had been dropped.
The most visible consequence was charts: a chart placed in a column and told to fill it would
instead shrink to whatever size it happened to be drawn at, sometimes only a few percent of the
space, squeezed into a corner of an otherwise empty column. The obvious fix — telling it to be full
width — was exactly the instruction being discarded, so the problem looked unfixable rather than
merely broken.

These blocks now carry their styling like everything else, so a width or a margin set on one takes
effect. Blocks whose author set no styling are placed exactly as before: that matters because such a
block is dropped into the page as-is, and quietly boxing every one of them would have shifted
existing layouts — turning side-by-side items into stacked ones, or adding line breaks around short
pieces of inline text. Nothing changes unless the author asked for it.
