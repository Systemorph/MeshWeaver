---
Name: GitSync updates recompile what they change
Category: Feature
Description: A synced Space's update now recompiles every affected NodeType by itself — including types in other spaces that share the changed sources — so an update means new behavior, not just new files.
Icon: ArrowSync
Order: -20260806
---

Updating a GitHub-synced Space now finishes the job. When a sync lands code — a `Source/*.cs`
file, a test, an edited NodeType definition — the sync itself requests a recompile of every
NodeType that compiles the changed nodes: the owning type, and any type anywhere on the mesh that
pulls those sources in via a `shared=@…` reference. Recompiles run dependencies-first, and the
sync's activity log names exactly which types were rebuilt.

Previously a sync updated the nodes and stopped there, so the mesh kept executing the previous
assemblies until someone recompiled the affected types by hand — the deploy step that read
"sync → recompile the changed NodeTypes → verify compiledSources", easy to forget and expensive
to diagnose when forgotten, because the sources on screen looked current while the behavior was
stale.

Content-only syncs are unaffected: a sync that touches no code requests no recompiles, so routine
document updates stay as cheap as they were.
