---
Name: OpenStreetMap maps actually appear
Category: Fix
Description: OpenStreetMap maps are drawn at a usable size instead of collapsing to nothing, a map that cannot load explains itself on the page, and leaving a page mid-draw is no longer recorded as a failure.
Icon: Globe
Order: -20260826
---

# OpenStreetMap maps actually appear

An OpenStreetMap map used to leave an empty rectangle on the page, and nothing said why. Three
separate reasons could produce that same blank box, and none of them told the viewer anything.

**The map now has a size.** A map is drawn into whatever space its container has, and that container
had no height at all unless the page author happened to give it one — so a map that had loaded
perfectly and was working correctly was still nothing to look at. It now comes with a sensible
default height, and a page that wants a different one still just says so.

**A map that cannot be drawn explains itself.** Where the map's own drawing code cannot be fetched,
the page now shows the same short notice the portal already uses for any view that fails to render:
what happened, a suggestion to reload, and the technical reason — including the exact address that
could not be fetched, so the problem can be reported and fixed instead of guessed at.

**Leaving the page is no longer a failure.** Navigating away while maps are still being drawn
cancels that work, which is entirely routine — but it was being recorded with the same severity as
a map that genuinely failed. Ordinary navigation is now recorded as what it is, which means what
remains under that heading is worth looking at.
