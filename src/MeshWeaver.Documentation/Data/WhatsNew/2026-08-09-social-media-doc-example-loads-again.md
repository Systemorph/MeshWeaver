---
Name: The Social Media documentation example loads again
Category: Fix
Description: The example's post list failed to build, which left its pages blank and could hold back a portal from starting.
Icon: Sparkle
Order: -20260809
---

# The Social Media documentation example loads again

The Social Media example in the documentation stopped building. Its post list referred to the
surrounding page in a place where that page was not available, so the whole example failed to
compile and its pages came up blank.

Because an example that will not build also counts against a portal's start-up checks, this could
hold a portal back from starting rather than simply showing one broken page.

The list now receives the page it belongs to, so the example builds and its pages render again.
