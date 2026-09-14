---
Name: An update restores content that went missing
Category: Fix
Description: Updating a plugin now brings back any of its files whose node has disappeared from the mesh — not only the files that changed since the last install.
Icon: ArrowSync
Order: -20260914
---

# An update restores content that went missing

Updating an installed plugin used to fetch only the files whose content had changed since the last
install. That is the right thing to do when nothing else moved — but if one of the plugin's nodes
had disappeared from the mesh in the meantime, its file was unchanged, so the update skipped it and
the node stayed gone. Every later update skipped it again, and each one reported success.

An update now checks the mesh before it fetches. Any file the plugin ships whose node is missing is
re-fetched and written back, alongside the files that genuinely changed — so the ordinary "update
the plugin" action repairs the gap instead of stepping over it.

Two smaller things travel with it:

- If the mesh cannot be read at that moment, the update says so and does **not** quietly proceed as
  if everything were present.
- If the source returns fewer files than the update asked for, that shortfall is now named instead
  of passing unnoticed.

The symptom this removes: a plugin whose views render empty, or whose node type will not compile,
because source files the install record still lists are not actually there.
