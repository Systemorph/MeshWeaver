---
Name: Views that change shape now redraw correctly
Category: What's New
Description: A view whose child set changes between renders no longer leaves a duplicated or stale panel behind, and swapping an embedded area in place no longer freezes the page.
Icon: Sparkle
---

# Views that change shape now redraw correctly

A view that renders a different set of panels from one moment to the next — a Back
button that appears once you have moved past the first step, a body that switches
from one named section to three — could leave the previous render on screen. You
would see the same panel twice, or a panel that belonged to the step you had
already left, while the rest of the page moved on.

Two things caused it, and both are fixed.

A panel that a re-render **removed** was never actually taken out of the page's
content: each render was merged onto the previous one, and a merge can add and
replace but can never delete. The removed panel therefore stayed available
forever, and anything still pointing at it kept drawing the old content. Renders
now apply their deletions, so a panel that is gone is gone.

Panels were also matched up **by position** rather than by name. Inserting a panel
at the front shifted every later panel by one, and the view that had been drawing
"All steps" was handed the new Back button as if it were merely an update — while
still holding the old panel's live data subscription. Panels are now matched by
their name, so inserting, removing or reordering them keeps each one intact.

The same fix covers embedded areas. Pointing an embedded area at a different view
of the same page — a walkthrough stage moving from *Structure* to *Economics* — used
to swap the underlying live connection underneath a panel that was still running on
the old one, and the whole page would stop updating: buttons still worked, the
server kept going, but nothing on screen changed. An embedded area is now identified
by what it points at, so changing that target cleanly replaces the panel instead.

If you worked around this by rendering a constant shape (always the same panels, in
the same order, hidden with styling instead of removed) or by turning navigation into
full page loads, those workarounds are no longer needed.
