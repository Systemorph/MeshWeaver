---
Name: A click finishes crossing before its page closes
Category: Fix
Description: Clicks, field exits, and dialog choices now keep their live page connection until the owner has accepted them, so navigating immediately cannot discard an action the portal already took.
Icon: Sparkle
Order: -20260911
---

# A click finishes crossing before its page closes

A quick click followed by immediate navigation could close the page's live connection while the
click was still travelling to the part of the portal that owned its action. The interface accepted
the click, but the owner no longer had the page-scoped handler when it arrived, so the requested
work did not run.

User actions now carry an owner-side acceptance receipt. Page teardown already waits for unfinished
receipts, so it keeps the action's connection alive just long enough for the owner to accept the
click, field exit, or dialog choice. Once accepted, teardown continues normally; genuinely stale
actions are still refused rather than retried against an obsolete page.
