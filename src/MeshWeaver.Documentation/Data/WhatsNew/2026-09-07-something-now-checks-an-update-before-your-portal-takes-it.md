---
Name: Something now checks an update before your portal takes it
Category: Fix
Description: Your portal has always had a gate that asks whether a new platform version can still serve the modules you actually run — and nothing in the fleet ever answered it, so every update rolled out unchecked and said so in a log nobody was reading. Delivery now runs that check per instance and lands the answer where the gate looks.
Icon: ShieldCheckmark
Order: -20260907
---

# Something now checks an update before your portal takes it

Your portal updates itself. Before it does, it consults a verdict about the specific combination in
front of it: *can this new platform version still serve the modules this instance has installed?* A
new version can be perfectly good in general and still be wrong for one instance, because that
instance carries modules pinned at particular commits.

The verdict has to be produced somewhere else. Producing one means pulling the candidate image,
materialising every module at the exact ref this instance runs, and executing the module tests
inside that image — which needs a machine with Docker, a scratch disk and repository access. A
portal does not have those, deliberately.

The tool that does it was written, documented and built — and connected to nothing. No workflow
anywhere in the fleet ever ran it, so no verdict was ever produced, and the gate consulted an empty
list every time. The portal was honest about it in its own log:

> applied update 3.1.0-ci.7841 … UNVERIFIED — no combo verification has been recorded for
> '3.1.0-ci.7841' on this instance … nothing has checked whether that image can serve the modules
> this instance runs.

Note the first word. It applied the update anyway. A gate that is never given data does not hold
anything back.

Delivery now runs the check. After a release is published, each declared instance is asked which
version it would roll to and which modules it actually runs; the candidate is verified against that
exact combination on a build machine; and the answer is written back to the instance, where its own
gate reads it before the next update check. A verdict that says the candidate cannot serve an
instance now holds that instance back — and the run that produced it goes red, so the problem is
seen by the people shipping rather than only by the portal refusing.

Two smaller things came with it. Your portal can now state its own module inventory to an
authenticated operator, which is what made the check possible at all. And the "nothing was
checkable" outcome is treated as a failure rather than a pass — a check that could not run is not a
check that passed.

Until an operator provisions the per-instance credentials the check needs, the new workflow fails
with a message naming exactly what is missing. That red is the point: it is the difference between
"we looked" and "nobody ever looked".
