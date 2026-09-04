---
Name: Restoring a version says when there is nothing to restore from
Category: Fix
Description: Rolling back a node or undoing an activity reported "version not found" on portals that keep no version history at all — a data-shaped answer to a configuration fact. It now says plainly that history is not retained on this deployment, so you know the difference between a node that has no earlier version and a portal that records none.
Icon: History
Order: -20260904
---

# Restoring a version says when there is nothing to restore from

Every node counts up a version number as you edit it. On some portals those earlier versions are
kept; on others nothing is recorded behind the counter at all.

Until now you could not tell which kind of portal you were on. Asking to roll a node back, or to
undo an activity, answered **"version not found"** either way — the same message you would get for
a node that genuinely had no earlier version. So a portal that had never recorded a single version
looked exactly like a node you happened to be unlucky with, and the natural next step — try a
different version, try a different node — could never work.

Those surfaces now answer honestly. When the portal retains no history, restoring says so:

> Version history is not retained on this deployment, so there is nothing to restore from. This is
> a configuration fact, not a property of this node.

Nothing about which portals keep history has changed, and nothing you could restore before has
become unavailable. What changed is that a portal no longer reports a settings fact as though it
were something about your document.

If you are looking at a node whose version number is high and whose history is empty, that is this
case, and the message will now tell you.
