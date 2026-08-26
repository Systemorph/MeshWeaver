---
Name: A burst of merges no longer skips a release
Category: Fix
Description: When several changes landed within a few minutes of each other, each one cancelled the build for the one before it — so none of them was ever compiled together and none of them shipped an update.
Icon: ArrowSync
Order: -20260826
---

# A burst of merges no longer skips a release

Changes are tested individually before they land, and then once more together after they land — that
second build is the only one that ever sees the combination, and it is also what releases the update.

When several changes landed close together, each one cancelled the build still running for the one
before it. The effect was invisible from the outside: no failure was reported, the list of builds
simply showed them stopping. But nothing had compiled the combination the changes formed together,
and because an update is only published once that build reports success, a run of merges could ship
no update at all. On one occasion five changes in ten minutes produced no release and a compile
error that neither change could have shown on its own.

Builds of already-landed changes now always run to completion. Superseding still applies while a
change is being reviewed, where the newer version genuinely replaces the older one.
