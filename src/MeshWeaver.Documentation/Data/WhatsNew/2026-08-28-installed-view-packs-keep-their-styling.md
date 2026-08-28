---
Name: Installed view packs keep their styling
Category: Fix
Description: Portals that install their views as packs render correctly again — the packs' stylesheets and scripts were being requested but not served, leaving pages unstyled.
Icon: Sparkle
Order: -20260828
---

# Installed view packs keep their styling

The views that draw a portal's buttons, grids, editors and dialogs are installed as packs rather
than built into the portal itself. Each pack brings its own stylesheets and scripts along with it.

A recent change to how a portal stores those packs on disk meant it stopped finding the styling that
came with them. The views themselves still loaded and pages still rendered — but every stylesheet
and script the packs provide came back empty, so pages appeared unstyled and controls could be hard
to read or use. Nothing in the portal's log said so, which made it look like a display problem
rather than a missing file.

Portals now serve those files wherever the packs are stored, and say so plainly when a pack brings
no styling at all. Existing installs pick this up on their next update; nothing needs reinstalling.
