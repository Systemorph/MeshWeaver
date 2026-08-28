---
Name: Pages no longer fail on a brief database blip
Category: Fix
Description: Layout areas now retry a momentary database connection timeout instead of failing the render.
Icon: Sparkle
Order: -20260828
---

# Pages no longer fail on a brief database blip

Occasionally a page area would show a render error during a short burst while the rest of the
portal kept working — the moment the database was briefly slow to accept a new connection. Queries
behind page rendering now retry such momentary connection timeouts a few times with a short,
increasing pause before giving up, so the page renders normally once the blip passes. Real errors
are still reported immediately.
