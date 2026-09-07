---
Name: A frame that changed nothing is no longer read as a lost one
Category: Fix
Description: A mirror that skipped a no-op patch left its clock behind, so the very next frame looked like proof of a lost one and the mirror asked for a snapshot it already held — on every page load whose layout re-rendered an identical control. The clock now moves even when nothing is emitted.
Icon: ArrowSync
Order: -20260907
---

# A frame that changed nothing is no longer read as a lost one

Every frame an owner sends carries the version of the frame it sent before, and a mirror that
receives a frame chained onto a version it never applied knows a frame was lost and asks for a
fresh snapshot. That detector is right to exist — a lost frame used to be a silent, permanent
deficit.

It had one blind spot. A mirror that received a patch which changed nothing — a layout area
re-rendered with an identical control, a value written back as it was — skipped it without
emitting anything, and also without moving its clock. The owner had chained the next frame onto
that skipped one, so the next frame looked like proof of a loss that never happened, and the mirror
re-asked for a snapshot it already held.

Measured on an Education gate run: 164 `Frame loss detected` warnings in one thirty-minute run,
and 164 of them preceded by exactly this skip of the frame the next patch chained onto. Not one was
a real loss. The cost was a round trip per page load and a warning that pointed everyone at the
wrong subsystem for two nights.

A skipped no-op frame now adopts its version silently — no emission, no consumer sees a change,
only the clock moves — so the chain stays intact and the detector fires only on what it was built
for.
