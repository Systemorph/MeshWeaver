---
Name: Content repos wait for the upstreams they build on
Category: Feature
Description: A content repo no longer rebuilds against a new platform release until the repos it depends on have published for it — it stops immediately and is restarted by their publication, so the whole family rebuilds in the right order without anything scheduling it.
Icon: ArrowSortDown
Order: -20260817
---

# Content repos wait for the upstreams they build on

A platform release tells every content repository to rebuild, and until now they all started at
once. That is the wrong order whenever one of them builds on another: a repo that starts before its
upstream has finished checks its content against an upstream that no longer matches the platform it
is building for. The result looks clean and means nothing.

Now a repo declares what it builds on, and checks before it starts. If an upstream has not published
yet, the run **stops immediately** — it does not build, it does not half-build, and it does not
report success. Nothing waits in a queue and nothing polls.

What restarts it is the upstream itself: finishing a publication now notifies the repos that depend
on it. So the simultaneous start sorts itself out — the repos with nothing above them go first, and
each finished publication releases the ones waiting on it — with nothing anywhere scheduling the
order.

Two details make that safe rather than merely tidy:

- **Stopping is visible.** A run that did not build is reported as such, never as a pass. A repo
  quietly skipped for a release would be indistinguishable from one that succeeded.
- **A lost notification is a failure, not a shrug.** A repo that stopped is waiting for exactly one
  signal, so if that signal cannot be delivered the run says so loudly — otherwise that repo would
  simply never rebuild for the release, and nothing would show it.
