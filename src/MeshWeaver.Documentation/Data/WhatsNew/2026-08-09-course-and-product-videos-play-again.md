---
Name: Course and product videos play again
Category: Fix
Description: Video players on course and product pages had been coming up empty, and share previews showed no image. The files were fine the whole time — the pages were asking for them at an address that had been retired.
Icon: Sparkle
Order: -20260809
---

# Course and product videos play again

Open a course, or a product page like Claims or Underwriting, and the two-minute
intro video was simply not there. Not an error, not a spinner — an empty black
player, on every page that had one. Sharing any of those pages produced a link
preview with no picture. It looked exactly like a permissions problem, and it was
reported as one: *this person cannot see the videos*.

It was not a permissions problem. Nobody could see them. The videos, posters and
preview images were all sitting in place, readable by anyone, including visitors
who are not signed in — and they still are. What had gone stale was the
**address the pages used to ask for them**.

Files stored on the platform used to be reachable at a second, unguarded address
as well as their real one. That shortcut was removed a while back, because it
handed out *every* space's uploads to anyone who could guess a URL — a genuine
leak, and closing it was right. But the pages that pointed at media through the
old shortcut were never updated, so from that moment on they were asking for
their own videos at an address that no longer answers.

Every one of those pages now asks at the real address: course intros and module
videos, the Claims and Underwriting product films, cover images and share
previews. Nothing about who may view what has changed — material that was public
stays public, and material that is not stays protected, now on the one route that
actually checks.
