---
Name: Stuck threads self-heal — version forks now reconcile instead of wedging
Category: What's New
Description: A thread (or any node) whose storage row got ahead of its live hub used to wedge permanently — every save silently refused. The owner now detects the refusal and rebases onto the durable truth, so the next write lands.
Icon: Sparkle
---

# Stuck threads self-heal — version forks now reconcile instead of wedging

Previously, if a node's durable storage row ended up on a newer version lineage than the live hub
serving it (for example after a reactivation seeded from a stale cache), every write the hub made
was refused by the storage write guard — silently. Chat threads hit by this froze forever: the
status never left "executing", new messages queued but never ran, and even the built-in recovery
watchdog's writes were refused.

Now the owner detects a refused write, adopts the durable truth, and continues from there — the
very next write lands and the thread (or node) recovers on its own. The version stamping that
manufactured these forks after a reactivation has also been fixed at the root, so the situation
arises far less often in the first place.
