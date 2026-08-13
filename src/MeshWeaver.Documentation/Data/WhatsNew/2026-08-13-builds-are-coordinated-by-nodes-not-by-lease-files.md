---
Name: Builds are coordinated by nodes, not by lease files
Category: Feature
Description: Who builds, what gets built, and when a server may serve is now decided by durable Build nodes with an arbitrated claim queue — the foundation for chunked, restartable, observable builds.
Icon: Sparkle
Order: -20260813
---

# Builds are coordinated by nodes, not by lease files

Deciding who compiles the platform's dynamic content used to happen outside the mesh — in a lease
file on a shared volume that nothing could observe, query, or reason about. When coordination went
wrong, it went wrong invisibly.

Build coordination is now mesh state. A durable build root records who currently builds (candidates
register, and the node's own arbiter grants the earliest request — a builder that dies is superseded
automatically), and a per-version history of completed builds that never forgets: finishing a new
build cannot revoke an older one's record, so servers on the previous version are never destabilised
by a rollout in progress.

Named chunks — each defined by ordinary mesh queries, as simple as a list of paths or a whole
module — get their own build nodes under the root, each with its claim, its pinned source commits,
and the release paths it produced. That makes a build observable like any other content: you can
see what is building, what is done, and exactly what each part wrote.

Nothing executes against it yet — this protocol is the coordination layer that chunked build
execution and version-aware readiness plug into next.
