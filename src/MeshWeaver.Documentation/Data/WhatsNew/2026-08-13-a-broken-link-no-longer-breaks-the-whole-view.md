---
Name: A broken reference no longer breaks the whole view
Category: Fix
Description: A page pointing at something that no longer exists now says so in plain language, instead of showing an internal error message.
Icon: Sparkle
Order: -20260813
---

# A broken reference no longer breaks the whole view

When a page referred to something that had been deleted, renamed, or never created — a slide missing
from a presentation, an embedded view whose target is gone — the page showed a raw internal error
message instead of an explanation. It was always in English, and it exposed wording meant for
developers rather than readers.

Such a page now shows a short, translated notice naming the item it could not find, so whoever
maintains the content knows exactly what to fix. It is also clearly different from a page that is
still loading, so nothing appears broken while it is simply not ready yet. And because a broken
reference is a content problem rather than a system fault, it no longer raises an operational alert.
