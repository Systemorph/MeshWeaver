---
Name: A repository update now reaches every space, not most of them
Category: Fix
Description: When a repository built successfully, some spaces were quietly left on old content — always the same ones, and the more they fell behind the less likely they were to catch up. Every matching space is now updated, and any that is skipped says so by name.
Icon: Sparkle
Order: -20260813
---

# A repository update now reaches every space, not most of them

Spaces that follow a repository refresh themselves whenever that repository builds successfully.
Most of them did. Some never did — and the ones that never did were always the same ones.

The refresh starts by asking a simple question: which spaces follow this repository? That question
was being answered the way a *search box* answers — with a first page of results, the most recently
changed ones, capped at fifty. For a search that is exactly right. For "go through all of them" it
is quietly wrong, and nothing in the answer says it was only a page.

What made it stick was the ordering. Refreshing a space marks it as recently changed, so every
space that *did* refresh moved to the front of the queue, and the ones that missed out drifted
further towards the back. A space that fell off the end could never get back on: it simply stopped
following the repository, while every build reported success. On one installation nine spaces out
of forty-three had been sitting on stale content for up to a week that way — including course
material with fixes their readers never saw.

A read that needs *everything* can now say so, and this one does. The same mistake had already cost
us once elsewhere — a build pass that saw fifty source files out of thousands — so the platform now
also warns whenever it hands back a capped page to a caller that never asked for one, naming the
query, rather than letting it pass for the whole set.

The refresh has also learnt to account for itself. It now reports how many spaces follow the
repository, how many it updated, and for each one it passed over, which one and why — "on a
different branch", "already on this commit". A space that is being skipped is now something you can
read in the log instead of something you have to notice.
