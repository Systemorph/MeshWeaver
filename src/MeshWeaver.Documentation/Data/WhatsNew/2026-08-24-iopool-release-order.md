---
Name: Pages stop failing to load while a portal shuts down
Category: Fix
Description: A part of the page could show an error instead of its content when a portal was restarting.
Icon: Sparkle
Order: -20260824
---

# Pages stop failing to load while a portal shuts down

If a portal was shutting down or restarting at the exact moment a part of your page was still
loading, that part could give up and show an error instead of its content — the comments section
was where it showed up.

The cause was an ordering mistake in how the platform tidies up: the last piece of work to finish
was releasing a resource that the tidy-up had, a moment earlier, already thrown away. Nothing was
lost and nothing was corrupted; the page section simply reported a failure it had no business
reporting. It now finishes cleanly.
