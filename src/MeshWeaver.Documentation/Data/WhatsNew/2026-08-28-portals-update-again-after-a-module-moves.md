---
Name: Portals update again after a module moves
Category: Fix
Description: Content that uses a module now builds ahead of time as well as at run time, so installs no longer stop updating when a feature moves into a module.
Icon: Sparkle
Order: -20260828
---

# Portals update again after a module moves

Content can use the features a portal has installed — an installer that adds a plugin's skills to
your AI settings, a page that draws a map. When one of those features moved out of the platform and
became a module you install, that content still worked in the portal but could no longer be built
ahead of time. The prepared build is what an install checks for before it updates itself, so
installs correctly declined every new version and stayed on the last one they had a build for.

Everything reported success while that happened, which is what made it hard to see: the checks that
run against a live portal passed, because a live portal has its modules. Only the ahead-of-time
build was missing them, and its failure looked like the content was broken rather than like a
feature had moved.

The ahead-of-time build now uses exactly the modules a portal does, so content that uses an
installed feature builds the same way in both places, and updates resume on their own.
