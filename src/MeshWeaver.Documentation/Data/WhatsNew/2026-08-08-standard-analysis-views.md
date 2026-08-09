---
Name: Standard analysis views ship as controls
Category: Feature
Description: KPI strips, excess-of-loss towers and paired comparison bars are framework controls now — declare typed rows, get the rendering.
Icon: Sparkle
Order: -20260808
---

# Standard analysis views ship as controls

Analysis pages kept reaching for the same three shapes, and every page drew them
again by hand. They are framework controls now, next to `DataGrid`: you declare
typed row records and the framework renders them, themes them, and translates
their empty states.

- **`Controls.KpiStrip(items)`** — a wrapping row of headline figures.
- **`Controls.Tower(bands, currency)`** — the vertical excess-of-loss stack, with
  consecutive layers touching, the taken share as the solid part of each band,
  and the retention as the base the tower stands on. Bands can be links.
- **`Controls.ComparisonBars(pairs)`** — two series on one shared scale.

The comparison view is careful about one thing in particular: a side with no
value renders as words, never as a zero-length bar. A missing figure and a
reported zero look identical drawn that way and mean opposite things.

Both the Blazor portal and the React frontend draw all three from the same
framework-owned geometry, so they stay the same picture — and improving how a
tower reads is now one change instead of one per page.
