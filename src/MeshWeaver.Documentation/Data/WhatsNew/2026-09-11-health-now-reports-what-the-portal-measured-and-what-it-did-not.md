---
Name: The health page now reports what the portal measured — and what it did not
Category: Feature
Description: A portal's health page now publishes two readings that previously existed only in its startup log: what its NodeType bake found, and how its source discovery behaved. A reading that is clean is printed too, so "nothing was measured" can no longer be mistaken for "everything is fine".
Icon: Pulse
Order: -20260911
---

# The health page now reports what the portal measured — and what it did not

A portal's health page lists whatever is currently wrong with it. That is the right thing for a
verdict, but it made two useful numbers impossible to read: how much of the portal's content was
already prepared when it started, and whether it managed to gather that content in one piece. Both
were written to the startup log and nowhere else, which meant that answering a question about them
needed access to the log store rather than a browser.

Both now appear on the health page, and they are reachable without signing in.

**What the portal's own preparation found.** When a portal starts, it checks which of its content
types already have a prepared, ready-to-use build and which it will have to build itself. The health
page now carries that result: how many types it looked at, how many were ready, how many were
pending, and how many it had to reclassify because its own preparation had moved on faster than the
list it was working from. The count of already-prepared items is printed beside it, together with a
note that the two numbers count different things — a pair that has been misread as a contradiction
more than once.

**Whether the content was gathered in one piece.** When a portal does have to build content, it first
collects the source for everything at once. That collection finishes when the answers stop arriving,
so an unusually long pause in the middle can end it early and hand the builder an incomplete set. The
health page now reports how many batches of answers arrived, where the collection settled, and the
longest pause between batches measured against the window that ends it. A long pause is flagged; a
short one is stated plainly, which is just as informative.

**A clean reading is printed, not silent.** Both entries print whatever they find. That is deliberate:
a reading that appeared only when something was wrong would be indistinguishable from a portal that
never took the reading at all, which is exactly the ambiguity these two numbers were stuck in. So
"this portal has no reading" and "this portal has a reading and it is clean" are now two different
sentences on the page, and neither is silence. A portal that never produced a preparation result at
all says so and is marked as needing attention — an absent measurement is never reported as a clean
one.

Nothing here changes whether a portal is considered healthy enough to serve traffic, or to be
restarted: these readings are published for people to read, and they carry no authority over either
decision.
