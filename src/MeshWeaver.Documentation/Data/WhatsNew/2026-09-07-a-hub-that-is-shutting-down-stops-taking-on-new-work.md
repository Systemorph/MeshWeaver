---
Name: A hub that is shutting down stops taking on new work
Category: Fix
Description: A component that had begun shutting down still accepted new requests it had no time left to answer, so the caller waited out its full budget and got a confusing failure at the end. It now turns such a request away immediately, with an answer that says "ask again in a moment" — while replies to work it had already accepted still get through.
Icon: DoorArrowLeft
Order: -20260907
---

# A hub that is shutting down stops taking on new work

Every component of the mesh shuts down in phases. The first phase is a drain: it stops starting
things and waits, for a fixed budget, for the answers it is already owed. Only then does it take
its children down and go away.

The door was being closed one phase too late. Throughout the drain — right up to and including
the moment a component had finished draining and had nothing outstanding at all — it kept
accepting new requests, kept running their handlers, and kept issuing follow-up requests of its
own. Work taken on at that point cannot finish: the budget it would have needed has already been
spent, so the next phase cancels it and the caller, who has been waiting in silence, gets a
"disposed before response" error with nothing to act on.

This was not rare. Across three delivery runs on 2026-09-06, six of the nine outstanding replies
that a component had to cancel on its way out had been taken on *after* it started shutting down.

The door now closes at the first instant of shutdown. A request arriving after that is turned away
at once, with the same answer a shutting-down component already gave in the later phase: a
transient refusal naming the component and promising that the address may come back, so a caller
can simply ask again — instead of waiting out its whole budget for a reply that was never going
to arrive.

Two things deliberately still get through, because a shutting-down component is not yet a silent
one. Replies that settle work it accepted *before* the shutdown are exactly what the drain is
waiting for. And traffic merely passing through on its way to a child is not this component's
work at all — those children are still running and are not taken down until the next phase.

The fix is the door, not the budget. Giving the drain more time would only have made the same
leak slower.
