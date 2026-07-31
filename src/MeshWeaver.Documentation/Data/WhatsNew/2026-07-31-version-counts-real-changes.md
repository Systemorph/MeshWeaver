---
Name: Version numbers now count your edits — and saving nothing no longer makes a version
Category: What's New
Description: A node's version used to jump by unrelated amounts (3 → 47) and could climb even when nothing was edited. It is now a plain revision counter: +1 per real change, and untouched by a save that changes nothing.
Icon: Sparkle
---

# Version numbers now count your edits

A node's version used to be stamped from the owning hub's internal message clock, so it jumped by
whatever that hub happened to be busy with — a document edited twice could go from version 3 to
version 47 — and it moved backwards after the hub restarted. It also climbed on saves that changed
nothing at all: re-importing unchanged content, re-installing a plugin, or an editor re-asserting
the state it already had each minted a version and a version-history entry for an edit that never
happened.

Version is now simply the node's own revision counter. Every real change adds exactly one, so
"version 7" means the node has been changed seven times, and the version history contains one
entry per actual edit. A save that turns out to change nothing is completed normally but leaves
the version, the last-modified stamp, and the history untouched.

Alongside this, markdown-backed nodes now keep their version across a reload — previously the
number was dropped when the file was written, so a later edit could quietly overwrite the previous
entry in the node's version history.
