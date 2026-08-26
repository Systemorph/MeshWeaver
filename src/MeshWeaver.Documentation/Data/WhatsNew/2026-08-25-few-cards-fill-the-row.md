---
Name: A short list of cards fills the row instead of huddling on the left
Category: Fix
Description: When a search or card grid had only a couple of results on a wide screen, the cards stayed at their minimum width and left the rest of the row empty. They now spread out to use it.
Icon: Grid
Order: -20260825
---

# A short list of cards fills the row instead of huddling on the left

Two results on a wide screen looked wrong: two narrow cards on the left and a large empty space to
their right, as though something had failed to load. With a full row of results the same grid looked
fine.

The grid was keeping the columns it did not need. A card grid is told how many columns fit and then
lays them out; it can either hold every column open, empty ones included — which squeezes the real
cards down to their minimum width — or drop the empty ones and let the cards share what is left. The
stylesheet said to drop them and had said so for a long time, but the width the component wrote at
render time overrode that and said to keep them.

Both now agree. Few results spread across the row; a full row is unchanged, and any column limit set
on the grid still holds.
