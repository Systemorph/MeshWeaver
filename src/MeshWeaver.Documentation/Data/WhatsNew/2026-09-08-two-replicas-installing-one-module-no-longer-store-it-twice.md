---
Name: Two replicas installing one module no longer store it twice
Category: Fix
Description: When several replicas of an installation adopted the same published module at the same moment, each wrote the identical bytes into its own folder — so they disagreed about which module set the installation runs, left the extra copies behind, and reported a conflict about nothing on every restart. A module folder is now named after its contents, so the same bytes are stored once and the replicas agree.
Icon: Cube
Order: -20260908
---

# Two replicas installing one module no longer store it twice

An installation that runs several replicas has them all watch the same plugin registry, so when a
module is published they all adopt it — usually within the same second. Each replica downloaded the
module and wrote it into a folder named with a fresh random id. The bytes were identical; the
folder names were not.

That difference propagated. The installation records *one* module set — the exact folder of every
module it runs, so every replica runs the same thing — and each replica derived that set from the
folder *it* had just written. Two replicas therefore proposed two different sets for the same
moment in the same installation. One was picked (deterministically, so nobody disagreed about
which), nothing was lost, and the other replica's copy stayed on the volume forever. The rejected
proposal stayed too, and every replica that started afterwards re-read it and reported the same
already-settled conflict again — an error line on every start, about a state that was correct.

On the public instance this had accumulated to 100 such conflicts, 687 bookkeeping records and 843
module folders that nothing could clear.

**A module folder is now named after its contents.** Two replicas adopting the same published
module compute the same name, so the second one finds the module already there and adopts it
instead of storing a second copy. They derive the same module set, the second proposal is simply
"nothing changed", and no conflict is recorded — because there is no disagreement to record. Two
replicas that genuinely adopt *different* modules still produce two sets, and that conflict is still
reported: it now means something.

A conflict that does occur is also settled once. The rejected proposal is retired by the same
housekeeping pass that clears superseded records, so it is reported where it is decided rather than
on every start of every replica from then on. Nothing is lost by retiring it: the next update wave
derives the set from what is actually installed, never from the previous proposal.

One consequence is worth knowing: if a module is republished with a new version number but
byte-identical contents, its folder does not change — the new version is recorded, and what runs is
what already ran, because the bytes are the same.

See [Module Set Convergence](/Doc/Architecture/ModuleSetConvergence) for the mechanism.
