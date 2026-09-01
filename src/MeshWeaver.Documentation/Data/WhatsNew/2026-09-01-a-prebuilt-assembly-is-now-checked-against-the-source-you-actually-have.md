---
Name: A prebuilt assembly is now checked against the source you actually have
Category: Fix
Description: A NodeType could adopt a compiled assembly built from older source and report perfect health while running it; the bundle now records which source it was built from, and a portal refuses bytes that do not match the source it is holding.
Icon: ShieldCheckmark
Order: -20260901
---

# A prebuilt assembly is now checked against the source you actually have

Installing or syncing a package does not usually recompile it. If a bundle already carries a
compiled assembly for a NodeType, the portal **adopts** those bytes instead of running Roslyn —
which is why an install takes seconds rather than minutes.

Adoption had no way to tell whether those bytes belonged to the source the mesh was holding. Worse,
the act of adopting made it *look* as though they did: the two signals an operator is taught to
check — `compilationStatus: Ok`, and `compiledSources` matching `currentSourceVersions` — are both
written by the adoption itself. So a sync that pulled a fix and then adopted an assembly built
before that fix reported success at every step, and the next run executed the old code. On
30 August that cost four client documents their entire body text; one had no earlier version and
could not be recovered.

A bundle now records, per assembly, a fingerprint of the source it was compiled from, and the
owning NodeType compares it against a fingerprint of the source **this** mesh holds:

- **They match** → the assembly is adopted and marked verified. Nothing recompiles; the fast path
  is exactly as fast as before.
- **They differ** → the adoption is **refused**. The bytes are not stamped as current, the rejected
  assembly stops serving, and a real compile of the live source is dispatched. The log names the
  type and both fingerprints.
- **The bundle records none** (anything built before this change) → it still adopts, and is marked
  *unverified* rather than silently treated as checked. Refusing here would recompile everything on
  every install, and on a deployment that only accepts prebuilt assemblies it would take every such
  type offline — a bigger outage than the bug.

The result is visible on the NodeType as `buildProvenance`: `Compiled`, `AdoptedVerified`,
`AdoptedUnverified`, or `AdoptionRefused`. A successful local compile resets it to `Compiled`, so
the field says what happened to the bytes that are serving right now rather than accumulating
history.

## Why this is shipping now, and not in August

The comparison above was written in August and was correct. It also never ran. The convenience
overload both callers used hard-coded the fingerprint to "none", no bake wrote one, and the live
half was computed only under a condition that the real sequence of events never satisfied. Every
adoption therefore took the "unknown provenance" branch, and nothing anywhere said so — a guard
whose input never arrives is indistinguishable from a guard that passes.

All four links are closed and pinned by tests that drive a real bundle through a real mesh, and the
shortcut that made "unverified" the default answer no longer compiles: a caller that genuinely has
no fingerprint must now say so explicitly, where a reviewer can see it.
