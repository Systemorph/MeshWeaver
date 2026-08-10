---
Name: A new version is now checked against everything that depends on it
Category: Feature
Description: New architecture documentation sets out how a module's build is verified against every dependent before it is published, so a removed capability cannot reach a portal unnoticed.
Icon: Sparkle
Order: -20260809
---

# A new version is now checked against everything that depends on it

Building blocks in the platform depend on one another, and on the platform itself. Until now a new
version of one could be published while something that relied on it had quietly stopped working —
the problem only appeared later, on a portal, as a page that would not load.

The Candidate Release Protocol describes a different order of events. A new version is first
published as a *candidate* rather than a release. Everything that depends on it is then rebuilt
against that candidate, and the version is only promoted to a real release once the whole chain
comes back clean.

When something does break, the run reports every affected item rather than stopping at the first
one, and the candidate is kept as a preview so it can be inspected and corrected without starting
over.
