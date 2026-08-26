---
Name: A map that cannot load says so
Category: Fix
Description: An OpenStreetMap map that fails to draw now explains itself on the page instead of leaving an empty box, and leaving a page mid-draw is no longer recorded as a failure.
Icon: Globe
Order: -20260826
---

# A map that cannot load says so

When an OpenStreetMap map could not be drawn, the page showed an empty rectangle. Nothing said
whether the map was still loading, had nothing to show, or had failed outright — and the only
record of what had actually happened was a server log the viewer cannot read. On a page with
several maps, all of them looked the same: blank.

A map that cannot be drawn now replaces that empty rectangle with the same short notice the portal
already shows for any view that fails to render: what happened, a suggestion to reload, and the
technical reason — including the exact address that could not be fetched, so the problem can be
reported and fixed instead of guessed at.

The other half of the change is about the logs. Leaving a page while its maps are still being drawn
cancels that work, which is entirely routine — but it was being recorded with the same severity as
a map that genuinely failed to build. Ordinary navigation is now recorded as what it is, which
means the remaining entries under this heading are real failures worth looking at.
