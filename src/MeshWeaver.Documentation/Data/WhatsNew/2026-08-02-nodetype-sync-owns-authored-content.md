---
Name: GitHub sync now compares what you authored, not compile bookkeeping
Category: Feature
Description: Type definitions sync on their authored content and code; compile state stays live on the mesh and out of your repo.
Icon: Sparkle
Order: -20260802
---

# GitHub sync now compares what you authored, not compile bookkeeping

A type definition carries two kinds of data: what you authored (its configuration, sources and
code) and what the mesh recorded about compiling it (status, timestamps, assembly pointers). The
sync used to treat both the same, with two bad effects: exported repo files carried a compile
verdict that was stale the moment the next compile ran, and importing such a file stamped that
stale verdict back over the live type — which could park the type after a restart and made
plugin-source deploys need a force-sync followed by manual recompiles.

Now the sync knows the difference. Exports commit only the authored definition, imports always
keep the live compile state (even from older repos whose files still embed it), and change
detection compares the authored content — including every code file — so a plain "Update to
latest" deploys a source-only change correctly, with no force and no recompile ritual.
