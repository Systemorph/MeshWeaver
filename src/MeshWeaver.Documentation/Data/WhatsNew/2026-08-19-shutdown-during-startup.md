---
Name: A portal replaced mid-startup shuts down cleanly
Category: Fix
Description: When a deployment replaces a portal that is still starting, the old pod now stops quietly and says why, instead of ending in a burst of errors that looked like a failure.
Icon: Sparkle
Order: -20260819
---

# A portal replaced mid-startup shuts down cleanly

A rolling update can replace a portal at any moment — including while it is still starting up. When
that happened, the old pod did not go quietly. Its startup check was cut off mid-question, and the
last thing it wrote was a bare "hosting failed to start" with no explanation, followed by a handful
of errors from background work that was still shutting down after the pod had already released the
resources it needed.

Nothing was actually broken — the pod was on its way out either way — but the errors were
indistinguishable from a real failure, so every rollout that caught a pod mid-start looked like a
problem to investigate.

Now the startup check says exactly what happened: it was cancelled because the pod was being
replaced, so nothing about the database was confirmed or faulted. And the background work that
finishes a shutdown no longer depends on resources that may already be gone — it takes what it needs
up front, so it always completes its work and reports the result, on both the ordinary shutdown path
and this one.
