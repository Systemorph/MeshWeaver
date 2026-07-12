---
Name: The React frontend looks and works like the portal
Category: What's New
Description: The new React frontend got a full styling and completeness overhaul — node icons show everywhere, pages match the classic portal's look, breadcrumbs are back, and embedded catalogs and dashboard regions load reliably.
Icon: PaintBrush
---

The React frontend (the "Try the new frontend" experience) now looks and behaves like the classic
portal. Node icons — the SVG, emoji and image icons your nodes carry — render everywhere they
should: in navigation, search results, catalogs and cards, with a clean initial-letter placeholder
when a node has none. Pages use the same typography and spacing as the classic portal, including
proper headings, separators and hint text that used to render invisibly.

Navigation got its breadcrumb bar back (Home › your page, with real node names), search results
show the familiar icon-and-description rows, and dashboard grids lay out in responsive columns
instead of squeezing everything into one.

Under the hood, this also fixes why embedded content — a space's Contents catalog, doc-page embeds,
home dashboard regions — would sometimes stay empty: reconnecting or reloading a tab could silently
cut off the live updates for everything loaded afterwards. That connection handling is now robust,
so embedded areas load dependably.
