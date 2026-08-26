---
Name: The sort and group controls stop overlapping the search box
Category: Fix
Description: On a home or search page, the Group by / Sort by controls beside the search box drew on top of it and cut their own captions mid-word — worst inside a narrow panel. The bar now wraps onto its own line instead of running off the end of the row.
Icon: Options
Order: -20260825
---

# The sort and group controls stop overlapping the search box

The view-options bar — *Group by ▾ · Sort by ▾ · ⚙* — shares one row with the search box. When that
row ran out of width the bar did not wrap and did not shrink: it **overflowed**, painting its
dropdowns across the search input and past the edge of the page, with captions cut mid-word. A
narrow container made it worse, so it was most visible in a side panel, but it was already wrong at
full width.

The cause is a flexbox default that catches everyone once. A flex item's `min-width` is `auto`,
which means "never shrink below your content" — so a row of them cannot give way when space runs
short. Nothing was constraining the bar to the space it had; each piece simply kept its natural
width and drew wherever that landed.

The bar now wraps, and every container inside it may narrow. When the row is wide, nothing changes.
When it is not, the controls drop onto their own line and stay whole and legible, which is what the
layout intended all along.

A guard test pins the four declarations that make this work, checked against the stylesheet itself —
remove any one of them and it fails, naming which.
