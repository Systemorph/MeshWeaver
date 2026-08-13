---
Name: One builder, even across clusters
Category: Fix
Description: A dedicated build process and a starting server no longer both run the build — the claim is now a deterministic election with a short convergence window, and a dedicated builder always outranks a serving pod.
Icon: ShieldCheckmark
Order: -20260813
---

# One builder, even across clusters

The first production run of the dedicated build process surfaced a coordination gap: it and a
starting server each believed they had won the build claim, and each ran the full build. Nothing
corrupted — every build output is content-addressed, so both wrote the same artifacts — but the
server paid the build's cost that the dedicated process exists to spare it.

The claim decision is now a deterministic election. Registrations get a short convergence window
so that candidates from different clusters see each other before anyone decides; then every
decider computes the same winner from the same data, so even concurrent decisions agree.
A dedicated build process always outranks a serving server — which is what makes servers stand
down exactly when a dedicated builder is present, while remaining their own fallback when none
is. Takeovers of a departed builder stay immediate, and per-part claims stay instant.
