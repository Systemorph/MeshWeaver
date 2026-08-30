---
Name: A red that says which half broke
Category: Fix
Description: A self-update gate test reported "it did not hold within 30 s" for two opposite situations — the poller never ran a check at all, and a check ran and decided not to hold. The wait is now two stages, so the failure names which one happened and quotes the verdict.
Icon: Branch
Order: -20260830
---

# A red that says which half broke

The self-update availability gate refuses to roll an install onto a release whose packages have no
usable artifacts. Its tests prove that by waiting for the refusal to land on the policy node.

One wait, one message: *"it did not hold within 30 s"*. That sentence covers two situations with
opposite fixes:

- **the poller never ran a check** — it is wedged, or its trigger never fired, and nothing about the
  gate is implicated;
- **a check ran and decided not to hold** — the gate itself is wrong, which is a real regression.

They arrived as the same red. When one failed once on a documentation-only merge — a change that
cannot reach any of this code — there was no way to tell which had happened, and the obvious
response, widening the 30 seconds, would only have made a real regression slower to surface.

The wait is now two stages. The first waits for the poller to record *any* check, which every check
stamps beside its verdict — that is the positive signal that the poller is alive and has evaluated
once. Only then does it wait for the hold, and if that second wait times out it reports when the
check ran, what triggered it, and the verdict it recorded, verbatim.

A failure now reads:

> the poller **did** run a check (trigger=Startup, verdict="HOLDING 9999.0.0-ci.1 — no
> release-availability gate is registered on this host…") but never held 9999.0.0-ci.1. This is the
> gate deciding **not** to hold — a real regression — not a poll that had not fired yet.
