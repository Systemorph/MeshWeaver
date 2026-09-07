---
Name: The daily platform-pin update can finally write the change it proposes
Category: Fix
Description: Every add-on repository pins the exact platform build it is tested against, and a scheduled job was supposed to propose moving that pin forward each day. It had never once succeeded — it was not allowed to edit the file the pin lives in — and because it ran overnight against nobody's branch, its daily failure was never seen.
Icon: Wrench
Order: -20260907
---

# The daily platform-pin update can finally write the change it proposes

Each add-on repository records the exact platform build it is compiled and tested against. Pinning
it on purpose is what makes an add-on's build reproducible, and what makes a platform regression
show up as a reviewable proposal rather than as a silently changed build input.

A scheduled job proposes moving that pin forward once a day. **It had never once succeeded.**

## What was wrong

The pin is recorded inside the repository's own build-configuration file, so proposing a move means
editing that file — and the job's credential was granted permission to change ordinary files but
**not** build configuration. Every attempt was refused at the final step, three minutes in, naming
a permission nobody had asked for.

The job was otherwise well behaved: it refused to paper over its own failure, so it went red
honestly, every night. But it ran overnight, on a schedule, against nobody's changes, where a
failure is not attached to anything anyone is looking at. *"The pin is not being maintained"* is
precisely the statement the job exists to make, and it had been making it daily into an empty
room.

## What changes

The credential now asks for the permission the job structurally requires, and it asks **up front**
— so if it is ever missing again, the failure is reported by the step whose name says what it
wanted, rather than by a push failing later for a reason that reads as unrelated.

## What this does not do by itself

Asking for a permission is not the same as having it. The grant is a repository setting, and no
job can give it to itself or even read whether it has it. If it has not been granted, this change
turns a confusing late failure into an obvious early one — which is the point — but the pin still
will not move until someone grants it.
