---
Name: The chart can no longer describe a startup budget it does not have
Category: Fix
Description: The deployment template said, twice, that the portal's startup-probe budget was 30 minutes. It ships 5. Anyone arming the NodeType bake gate on that sentence would have had every pod killed mid-bake — the exact failure the same comment warns about. The prose now states what ships, and a guard keeps it that way.
Icon: Timer
Order: -20260830
---

# The chart can no longer describe a startup budget it does not have

`PreWarm__GateReadiness` is the portal's safest deploy switch: it holds `/health` red until the new
image's NodeType bake is proven green, so an image that regressed a type stalls its own rollout with
the previous image still serving, instead of erroring in front of users.

It is also the switch with the most prerequisites, and the chart lists them carefully — including
one that is arithmetic: the startup probe's budget (`periodSeconds × failureThreshold`) has to cover
a full cold bake, measured from production at roughly 2.4 seconds per NodeType, sequential, or about
ten minutes on the largest mesh we run. The chart is emphatic about what happens otherwise:
*Kubernetes kills the pod mid-bake, every time, forever.*

And then, two lines later, it told you the budget was already thirty minutes. Twice — once as the
"paired setting", once as what "managed envs set". **Every environment in the fleet runs five
minutes.** So the prerequisite was satisfied only in the reader's head, and the first person to arm
the gate on that sentence would have walked straight into the failure the sentence was warning them
about.

## What changed

The comments now describe what the chart actually ships, and say plainly that arming the gate means
raising the budget *in the same change*.

More usefully, the coupling stopped being a paragraph. A new guard fails the build when:

- any line in the chart **talks about** a startup budget that is not the one `values.yaml` ships —
  unless it marks itself as a suggested setting to move to, which is what a paired-setting note
  should look like; or
- `PreWarm__GateReadiness` is armed without all four of its prerequisites moving with it: a budget
  covering the cold bake plus a plain boot, `PreWarm__DynamicTypes` also on, `maxUnavailable: 0`,
  and a startup probe actually pointed at `/health` — the only endpoint that reads the gate.

The first half is the one that matters most, because it fails **today**, on the drifted comment. A
guard that only woke up once someone armed the gate would have caught nothing on the day the prose
went wrong — which is the whole reason the prose was still there to find.
