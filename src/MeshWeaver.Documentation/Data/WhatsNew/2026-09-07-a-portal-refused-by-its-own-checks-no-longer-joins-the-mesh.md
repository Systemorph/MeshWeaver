---
Name: A portal refused by its own checks no longer joins the mesh
Category: Fix
Description: A pod whose startup validation refused it kept running inside the mesh and kept recording what it had compiled — so a rollout the platform correctly stopped could still break pages on the version that was still serving. A refused process is now genuinely inert.
Icon: ShieldError
Order: -20260907
---

When a new version of the portal rolls out, it checks itself before taking traffic: it rebuilds
every custom type in your space and refuses to serve if something that used to work no longer does.
That check works, and when it fires the rollout stops with the previous version still serving —
which is exactly what you want.

**What it did not do was stop the refused process.** It stayed running, and it kept writing down
what it had just compiled onto the shared records every other replica reads. So for as long as the
stalled rollout sat there, two versions of the platform were describing the same types to each
other, and the version that was actually serving could end up pointed at code built for the other
one. On 2026-09-06 that took out every deal page and every offer page on a client portal for two
hours — while the check that was supposed to be protecting it was doing its job perfectly.

A process that has not passed its own validation is now **inert**, not merely kept out of the load
balancer. It may read and it may compile — it has to, in order to check itself — but nothing it
produces reaches the shared mesh until the check passes:

- everything it compiles is **held**, in order, and written only once its validation succeeds;
- if the validation fails, those results are **discarded and never written**;
- it no longer announces itself as a replica running the current module set;
- and the rule is re-checked continuously, so a version that develops a problem *after* it started
  serving also stops recording — it keeps serving what it already has.

Nothing changes for a deployment that has not switched the startup check on: it behaves exactly as
before.

The refused process deliberately stays up rather than crash-looping, so you can still open its
health page and read which type failed and why. The full design, including that trade-off, is in
[Mesh Admission](/Doc/Architecture/MeshAdmission).
