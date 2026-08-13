---
Name: A momentary database hiccup no longer lasts until the next restart
Category: Fix
Description: A single slow moment reaching the database could leave one live list permanently stuck on that error — every later reader got the same stale failure until the portal was restarted. Failed lists are now retired instead of remembered.
Icon: ArrowSync
Order: -20260813
---

# A momentary database hiccup no longer lasts until the next restart

Lists that stay live — the set of files a piece of code is built from, the members of a group, the
items behind a picker — are kept once and shared by everyone who needs them. Building one is
expensive, so the platform keeps the result and hands the same live list to every later reader.

That sharing had a sharp edge. If the very first attempt to build a list failed — a few seconds
where the database was slow to answer, and nothing more — the failure was kept alongside the list
and replayed to every reader afterwards. Not retried, not re-checked: handed the identical error,
instantly, forever. The database recovered seconds later and it made no difference. Only restarting
the portal cleared it.

The damage was quiet and out of proportion. One piece of code could no longer see which files it was
built from, so it never looked rebuilt — and because "we could not read the files" and "the code is
broken" were indistinguishable from the outside, it was treated as broken and held back a release.
The files were fine. Nobody had changed anything. A few slow seconds hours earlier were still being
replayed as the answer.

A list that ends in failure is now retired rather than remembered. The reader that hit the problem
still sees the real error — nothing is swallowed — but the next reader gets a genuine fresh attempt,
so a moment of slowness lasts a moment. Healthy lists are untouched and keep being shared exactly as
before.
