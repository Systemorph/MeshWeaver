---
Name: Installed plugin pages stop rendering blank
Category: Fix
Description: Pages belonging to an installed plugin could come up empty, because the content one part of the portal had built could not be read by the rest of it.
Icon: Sparkle
Order: -20260809
---

# Installed plugin pages stop rendering blank

Plugins bring their own kinds of content with them — an underwriting guideline, a chess game, a
course. Those kinds are created by the portal itself when the plugin is installed, so only the part
of the portal that created them recognised them. Everywhere else the content arrived as raw data
that nothing knew how to display, and the page came up empty — sometimes reporting that it could not
find the view it was meant to show.

Because a plugin's front page is itself one of these kinds of content, this could affect an entire
plugin at once rather than a single page inside it.

The portal now shares what it knows about a plugin's content across all of its parts, so a page
renders wherever it is opened.
