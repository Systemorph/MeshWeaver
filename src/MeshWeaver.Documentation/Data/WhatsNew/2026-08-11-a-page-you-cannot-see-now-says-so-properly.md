---
Name: A page you cannot see now says so properly
Category: Fix
Description: Opening content you lack permission for showed a technical "This area failed to render" panel quoting an internal error. It now shows the standard, localized "Access denied" message.
Icon: Sparkle
Order: -20260811
---

# A page you cannot see now says so properly

When you opened a page whose content you did not have permission to read, the portal showed a
broken-looking "⚠️ This area failed to render." panel quoting an internal error message —
including your user name and the exact node path the check refused. That read as a crash, when
in fact everything worked exactly as designed: you simply were not allowed to see the content.

Such a page now shows the same clean, localized "Access denied" message the rest of the portal
uses, with the usual hint to ask the owner for access. Genuine rendering errors keep their
detailed panel so real problems stay visible.

Behind the scenes the same event was also logged as an error with a nameless area
("Rendering failed for area (null)"), which paged operators for a non-problem and hid which
page was involved. It is now recorded as a routine warning naming the actual area.
