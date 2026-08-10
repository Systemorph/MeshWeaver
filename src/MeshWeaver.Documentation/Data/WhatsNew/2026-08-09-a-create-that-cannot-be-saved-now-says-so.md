---
Name: A create that cannot be saved now says so
Category: Fix
Description: Creating a node whose path no storage backend claims used to wait forever instead of reporting the problem.
Icon: Sparkle
Order: -20260809
---

# A create that cannot be saved now says so

Creating a node asks the storage layer to write it. Storage is a chain of backends, and each one
answers "yes, that path is mine" or "no, not mine" so the next one gets a turn. When *nobody*
claimed the path, one of the two ways that chain can be assembled reported a clean failure — and
the other simply went quiet. Nothing was written, nothing was said, and whoever asked for the
node waited indefinitely: no error, no timeout, no clue.

Which of the two you got depended entirely on how the portal's storage happened to be wired
underneath, which is not something the caller can see or control. That made it look random and
made it very hard to reproduce — the same action would fail politely on one deployment and hang
on another.

Both paths now answer the same way, and the failure names the backend that declined so the
misconfiguration can be found instead of guessed at. More generally, the create pipeline now has
a backstop: if it ever finishes without producing a node *and* without explaining why, it replies
with that fact rather than leaving the request unanswered. Waiting forever is never a legitimate
outcome — an answer you can act on always is.
