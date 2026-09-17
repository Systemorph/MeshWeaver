---
Name: A node type's sources wait for the build made from them
Category: Fix
Description: When a content sync would give one node type source files that the portal's compiled build was not made from, that type now keeps the sources it has and says what it is waiting for — while the rest of the space syncs normally.
Icon: LockClosed
Order: -20260917
---

# A node type's sources wait for the build made from them

A portal serves a node type from a compiled build, and that build was made from one exact set of
source files. Keeping the two in step is what stops a type from suddenly rendering last week's code
over today's data — or failing to compile at all.

Until now that was kept in step **per repository**: a content sync brought a space to the commit the
portal's builds were made from. That is right, and it is not enough. The builds a portal holds are
published per node type, and a publication can be complete, at the right commit, and still not
contain the build for one particular type. The sync then moved that type's sources anyway, and the
portal declined its own build as "not made from these sources" — the type kept serving, marked as
behind, until a newer build arrived.

Now the sync asks per type. A type whose compiled build is **verified** against the sources it has
keeps those sources unless a build made from the repository's newer ones is already on the portal:

- **Everything else in the space syncs normally** — the hold is one type's sources, not the space.
- **The space's sync record says so**: it still shows the commit it genuinely holds, names each held
  type, and says which build it is waiting for.
- **The sync activity says so too**, in your language, with the types named.
- It releases by itself when that build arrives, when the repository produces a commit whose build
  the portal has, or when the portal is rolled onto a platform that has one.

Nothing is held when there is nothing to protect: a type the portal compiles itself, a type already
behind its sources, a space whose repository the portal runs no compiled builds of, and a portal that
consumes no published builds at all are all unaffected. Nor is anything held when the portal cannot
READ the builds it holds — the arrival of a build is what ends a hold, so a hold taken without being
able to see the builds would have nothing that could end it; the sync proceeds and the type says it
is behind, as it did before.

The design, including what a partially-held space means for the recorded commit:
[Adopt Then Sync, Per NodeType](/Doc/Architecture/AdoptThenSyncPerNodeType).
