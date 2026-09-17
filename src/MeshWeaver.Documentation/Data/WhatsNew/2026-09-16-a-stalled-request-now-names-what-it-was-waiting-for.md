---
Name: A stalled request now names what it was actually waiting for
Category: Fix
Description: When a request was held back while a part of the system was still starting up and then gave up, the explanation it produced could name nothing at all — or name the wrong thing. It now reports what actually held the request, and says separately whether that has since cleared.
Icon: Timer
Order: -20260916
---

# A stalled request now names what it was actually waiting for

Parts of the platform hold incoming work briefly while they start up — a workspace loading its
data, a node type being compiled. A request that arrives in that window is parked, and released the
moment start-up finishes. If it is never released, the sender is told so rather than left waiting.

That explanation is the subject here, and it could be **empty or wrong**.

## What was going wrong

The list of things a component is still waiting for is a live list: the moment one of them is
ready, it is taken off. The explanation was written at the end — when the request gave up — and it
read that live list *then*. So it did not describe what had held the request; it described what the
component happened to still be waiting for at the moment of the complaint.

In the worst case everything had finished by then and the list was **empty**, which does not read
as "no information". It reads as a statement: *nothing was holding it.* The exact opposite of what
happened. That answer went out 364 times over six days before it was noticed, and in one form the
sentence beside it — "without ever finishing start-up" — was plainly untrue: start-up had finished,
and the request simply never got its turn.

## What you get now

Two facts, labelled as two facts:

- **What held this request**, recorded at the instant it was parked, so it cannot be overwritten by
  anything that happens afterwards.
- **Whether those are still outstanding**, read fresh — because the two answers mean completely
  different things. Still outstanding means something really is stuck and that is where to look.
  All finished means the component started fine and the request never got its turn, which is a
  different problem entirely, and until now the two were indistinguishable.

The same pair now goes to whoever was waiting, not only into the log — a caller is often in a
different process and can see none of the component's own diagnostics.

## What did not change

No wait got longer, nothing retries, and nothing is now hidden. Every report keeps the level and
the detail it had; the part that was missing is added beside it.
