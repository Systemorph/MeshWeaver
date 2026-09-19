---
Name: Recycling a node type now recycles everything built on it
Category: Feature
Description: >-
  "Recycle" on a node type used to restart only that type's own hub, while every page and node
  built on it kept serving the old build until each was recycled by hand. One recycle now reaches
  the whole network — the node types that share its sources and every instance — and touches only
  what is actually running.
Icon: ArrowSync
Order: -20260918
---

# Recycling a node type now recycles everything built on it

After a change reaches a running installation — a merge synced in, a package updated, a node type
rebuilt — the parts already running keep serving what they loaded until they are **recycled**. That
was documented, and it was also the reason people kept pinning installations to a build: the
recycle you could run from a node type's menu restarted *that node type's own hub* and nothing
else, so the pages and nodes built on it went on answering from the old build.

## What changed

**Recycle the node type, and its whole network follows.** One recycle on a node type now reaches

- every node type that shares its sources (and theirs, all the way down), and
- every node of each of those types.

**Only what is actually running is touched.** A node that nobody has opened has nothing to recycle,
and it is left alone — it is not started up just to be shut down again. So a type with ten thousand
nodes and three open pages recycles three things.

**One recycle is one wave.** The network is worked out once, at the node type you recycled; the
parts it reaches do not fan out again, so two node types that depend on each other cannot turn one
recycle into an endless loop.

Nothing about the Recycle button, the `recycle` tool or the `mw recycle` command changed on the
outside. They now do what the word always promised.
