---
Name: Fixed a race that could silently break interactive markdown kernels and activity cancel
Category: Fix
Description: A just-created progress activity could briefly answer "not found", stopping interactive code blocks and cancel requests from working.
Icon: Sparkle
Order: -20260826
---

# Fixed a race that could silently break interactive markdown kernels and activity cancel

Right after a background operation (a GitHub sync, a content-indexing run, an interactive
markdown kernel) recorded its progress activity, a handful of internal watchers immediately
tried to read that activity back — before the mesh had fully caught up with the fact that it
existed. Under load, that read could come back "not found" even though the activity was already
saved, which meant an interactive code block in a markdown page could fail to start, and a
cancel request on a running activity could silently be missed.

These watchers now confirm the activity is visible before subscribing to it, so a brief lag in
the mesh no longer looks like the activity was never created. Users should see interactive
markdown kernels start reliably and cancel buttons on long-running operations work consistently,
even under heavier load.
