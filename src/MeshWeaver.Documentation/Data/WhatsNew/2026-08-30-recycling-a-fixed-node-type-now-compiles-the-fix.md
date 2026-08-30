---
Name: Recycling a fixed NodeType now compiles the fix, not the broken source
Category: Fix
Description: Recycle stamped its recompile trigger and tore the node's hub down in the same breath, so the reactivated hub could recompile the source you had just fixed — reporting the old errors and leaving the type parked. The teardown now waits for the trigger to land.
Icon: Bug
Order: -20260830
---

# Recycling a fixed NodeType now compiles the fix, not the broken source

Fix a NodeType that failed to compile, hit **Recycle**, and the type could come back reporting the
**same errors you just fixed** — still parked, still refusing to serve. Recycling again sometimes
helped, which made it look like a slow cache rather than a bug.

## What was happening

`Recycle` does two things to a NodeType: it stamps a **release request** on the node (the trigger
that un-parks the type and asks for a fresh compile), and it **disposes the node's hub** so the next
access re-initialises it. Those two were issued back to back, and the stamp was *fire-and-forget* —
the dispose did not wait for it.

Nothing enforced the order. When the dispose won the race, the hub came back, re-ran its source
query against a half-invalidated state, matched **zero** source nodes, and recompiled the
**pre-fix** source. The type then settled at `Error` carrying the old diagnostics and stayed
parked — indistinguishable, from the outside, from "your fix did not work".

The ordering had been holding only because both calls happened to be issued inside the caller's
hub turn. That is not a guarantee, and it stopped being true as soon as an unrelated change stopped
resuming continuations inline on the signalling thread.

## What changed

The dispose is now **sequenced onto the stamp's completion** instead of racing it, so the
reactivated hub always sees the trigger — and the fixed source. If the stamp itself fails, the
recycle still proceeds (the hub bounce is what you asked for) and says so in the log, rather than
quietly degrading to the old behaviour.

The regression test that caught this deliberately does **not** wait for the fix to become visible
before recycling — that wait would hide the defect it exists to detect.
