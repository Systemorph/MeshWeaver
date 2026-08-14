---
Name: Mobile search and signed-in portals work again
Category: Fix
Description: Search on the mobile app returns results instead of spinning, and connecting to a portal with an API token now works for markdown, files, and uploads.
Icon: Sparkle
Order: -20260814
---

# Mobile search and signed-in portals work again

Searching from the mobile app no longer spins for half a minute and comes back
empty — search now uses the same query service the web portal uses. When you
connect the mobile app to a portal with an API token, markdown rendering, the
file browser, and uploads now sign their requests correctly instead of being
rejected.
