---
Name: Recycling a node waits for an install writing under it
Category: Fix
Description: The operations/MCP recycle verb no longer tears a package root down while that package's own install is writing under it — it defers until the install releases the root, naming the holder in the log, and then runs and answers exactly as before.
Icon: ArrowSync
Order: -20260913
---

# Recycling a node waits for an install writing under it

Recycling a node disposes its hub so the address comes back fresh. Aimed at a **package root while
that package is installing**, it used to strand the install: the root's per-node children go down
with it, the writes those children owed acknowledgements for are never answered, the handler that
owed its reply to one of those acknowledgements never replies, and the install sits until its own
ten-minute bound reports a timeout — against a package that had finished writing its files minutes
earlier.

The framework's own automatic recyclers have deferred against a running install since
MeshWeaver#4009. The **operator-facing** verb — `recycle` over MCP, and every operations surface
that reaches `MeshOperations.Recycle` — did not: it posted its own teardown directly and consulted
nothing, so an operator could reproduce by hand the exact harm the automatic path had been taught to
avoid. It now passes through the same gate, which is one implementation rather than a second copy of
the rule.

What you see: a recycle issued while an install holds that root produces no answer until the install
releases it, and one log line at Information names who holds it and why. It is a **deferral, never a
refusal and never a drop** — the wait ends on a state that always arrives, because an install's lease
is tied to its own subscription, so it is released on completion, on failure and on abandonment
alike. Nothing is retried and no bound anywhere is widened. When the install finishes, the recycle
runs and answers with the same envelope it always did.

Ordinary recycles are untouched: with no install holding the root — the overwhelmingly common case —
the verb proceeds immediately, exactly as before. The permission check still runs first, so a caller
who may not recycle is refused at once and never waits on somebody else's install.
