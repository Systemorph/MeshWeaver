---
Name: Installed view packs keep their styles
Category: Fix
Description: A view pack installed from the store now arrives with its stylesheets and scripts, not just its code.
Icon: Sparkle
Order: -20260825
---

# Installed view packs keep their styles

A package installed from the store arrives as a set of compiled files plus the stylesheets and
scripts its screens need. The registry was handing on only the code half, so a pack that no longer
travels inside the portal image could render without its own styling — form fields and other
components inherited whatever the page around them looked like instead of their intended design.

Packages published from now on carry their styles and scripts all the way through to the portals
that install them. Packages published earlier need to be published once more to pick this up.
