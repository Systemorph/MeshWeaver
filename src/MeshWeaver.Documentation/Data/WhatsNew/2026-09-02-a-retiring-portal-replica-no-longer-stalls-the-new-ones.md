---
Name: A retiring portal replica no longer stalls the new ones
Category: Fix
Description: Fixed a fault where a portal replica being replaced during an update kept rewriting shared build records for minutes, so pages of the affected types on the new replicas showed a "preparing" card for two minutes or more after every deployment.
Icon: Bug
Order: -20260902
---

# A retiring portal replica no longer stalls the new ones

When the portal is updated, the old replica keeps serving the people still connected to it for a
while before it exits. Until now it also kept running its background housekeeping during that time
— including the step that adopts pre-built code for your custom types — and that step writes to
records the whole portal shares.

On a replica that is on its way out, that write was worse than useless: it replaced the coordinates
of the build the new replicas were happily serving with an older bundle's, then rejected that bundle
and cleared the coordinates again. Every new replica saw a type with no usable build and fell back to
a "preparing" card until its two-minute self-heal ran. On a busy deployment that was a two-minute
stall per type, on every update.

A replica that has begun shutting down now leaves those shared records alone: it starts no adoption
pass, stamps nothing, clears nothing, and dispatches no compile whose result it might not live to
record. The replicas that stay do that work, once. Separately, a pre-built bundle whose source no
longer matches what the portal holds is now turned away before it writes anything at all, instead of
being written and then rejected — so the build you already have keeps serving while a fresh one
compiles.
