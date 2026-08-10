---
Name: Your exact plugin set can be rebuilt for verification
Category: Feature
Description: The combination of modules a portal actually runs can now be materialised at its recorded versions and handed to the plugin test gate — so an update is verified against exactly what you have installed, not against whatever a repository ships today.
Icon: ShieldCheckmark
Order: -20260810
---

# Your exact plugin set can be rebuilt for verification

Before an update can be trusted, one question has to be answered honestly: does
the new version work with the modules *this* portal actually runs? Not with the
plugin repository at its latest commit — with your set, at the versions you have.

The platform can now rebuild that set. Every module a portal carries records
where it came from and the exact point it was taken at, and a new assembly step
reads that record and lays the whole combination out as files, ready for the
plugin test gate to compile and run — the same gate that already checks the
plugin repository on every change.

Honesty is the point, so the assembler is strict about what counts as evidence:

- A module pinned to an exact commit is fetched at exactly that commit.
- A module that records its own content fingerprint is fetched and then has to
  *prove* it matches — if the branch it came from has moved on, that is a named
  failure, not a silent substitution.
- A module pinned only to a moving branch is refused outright. You can override
  that explicitly, and the result is then stamped as unpinned so it can never be
  mistaken for a reproducible check.

Every run leaves a manifest naming what was materialised — each module, the
commit it resolved to, and a content hash — so a verdict built on top of it can
always say precisely what it verified. One broken module never hides another:
the run continues past every failure and reports them all.

This is groundwork. The next step wires it into the update flow, so a portal
checks a candidate release against its own set before adopting it — and refuses
one that would break what you have.
