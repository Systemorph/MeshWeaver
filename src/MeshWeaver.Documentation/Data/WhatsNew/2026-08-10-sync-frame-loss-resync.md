---
Name: Live views recover from lost sync frames
Category: Fix
Description: A page could sit on "awaiting first data" forever when one data-sync frame was lost in transport; mirrors now detect the gap and resync automatically.
Icon: Sparkle
Order: -20260810
---

# Live views recover from lost sync frames

Opening a page could occasionally leave one area stuck on its loading placeholder forever, even
though the server had rendered the content — one synchronization frame was lost on the way to your
browser session, and later updates kept applying cleanly around the hole, so nothing ever noticed.
Every synchronized view now chains its updates together: when a frame goes missing, the mirror
detects the gap immediately and fetches a fresh snapshot from the owner, so the page catches up
within moments instead of hanging. Freshly opened sessions also no longer lose the very first
answer to their subscription when it raced the connection setup.
