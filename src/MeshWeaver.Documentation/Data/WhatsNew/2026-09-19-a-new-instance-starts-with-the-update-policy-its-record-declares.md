---
Name: A new instance starts with the update policy its record declares
Category: Feature
Description: >-
  Until now every new installation started on Stable with no pattern, whatever its deployment
  record said, and an operator had to open the instance and set Continuous by hand — or forget to.
  The record's policy and pattern now reach the instance as its first update policy.
Icon: ArrowSync
Order: -20260919
---

# A new instance starts with the update policy its record declares

An instance's deployment record has said for weeks how it should update — Continuous on the
`3.0.0-ci*` line for every instance in the fleet — while the instance itself started on **Stable
with no pattern** and stayed there until someone opened Settings → Updates and changed it by hand.
One instance that nobody opened sat on a week-old build with its own policy field missing, telling
no one.

## What changed

- The record's `updatePolicy` and the new `updatePattern` render into the portal's configuration as
  the self-updater's **seed** values. The first time an instance creates its `Admin/UpdatePolicy`
  node, that is what it starts with.
- A record that says nothing renders nothing, so an instance keeps the image's own default.
- An existing node is never touched by configuration: what you set on the instance stays.

Nothing changes for instances already running. For the fleet, the records already say Continuous;
they gain the pattern next.
