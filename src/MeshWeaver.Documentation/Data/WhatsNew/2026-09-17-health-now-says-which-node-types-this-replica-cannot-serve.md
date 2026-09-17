---
Name: Health now says which node types a replica cannot serve
Category: Feature
Description: The public /health endpoint now publishes an outcome census beside the bake plan - how many node types this replica ended up with no usable assembly for, and in which partitions - so a page that renders "area not found" can be traced to its cause without a credential or a log.
Icon: ClipboardTaskListLtr
Order: -20260917
---

# Health now says which node types a replica cannot serve

When a page renders **Area not found** where content should be, the cause is usually that the node
type behind it never reached a usable assembly on the replica serving you. Until now nothing said
so. The portal's `/health` endpoint published the *bake plan* — how many node types the shared store
already covered and how many were left to build — which describes what the platform **intended**,
not what happened. A replica could publish a perfectly clean plan and still fail to compile half of
it, and the only record of that lived in a boot log.

`/health` now carries the **outcome** beside the plan: how many node types this replica has no usable
assembly for, and which partitions they live in. It is public and unauthenticated, like the rest of
that endpoint, so anyone can read it without a credential and without asking the platform to run
anything.

Three things it will not do:

* It never stays silent. *"No sweep reported here"*, *"everything measured is servable"* and
  *"these are not"* are three different sentences, so an unmeasured replica can never be mistaken
  for a clean one.
* It always prints its denominator — how many types reached a verdict, of how many there are —
  because a zero without one means *"nothing to see"* and *"I could not look"* equally.
* It does not cry wolf on retirements. A node type its own repository withdrew is counted
  separately, never as breakage.

Node **titles** are deliberately not printed: the partition is enough to route a finding to its
owner, and that endpoint is public.

What it measures and how to read it: [Measuring a Live Portal,
Read-Only](/Doc/Architecture/MeasuringALivePortalReadOnly).
