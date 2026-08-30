---
Name: A page waiting on a restarting pod recovers in milliseconds instead of a minute
Category: Fix
Description: Requests aimed at a hub whose pod is mid-restart now get an immediate "try again" and recover on their own, instead of waiting out a 30–60 second silence.
Icon: Sparkle
Order: -20260830
---

# A page waiting on a restarting pod recovers in milliseconds instead of a minute

When the portal runs on more than one pod, every message has to find the pod that owns the hub it is
addressed to. During a rolling update — and briefly whenever a hub moves between pods — a message
could arrive while the owning pod had not yet announced itself. The message was then handed to a
shared queue as a fallback, and that queue had two bad days in it: if nobody was listening the
message was accepted and silently thrown away, and if the queue itself was stuck the sender waited
out its entire budget, thirty to sixty seconds, for an answer that was never coming. What you saw
was a view stuck on "loading", a chat reply that never appeared, or a save that seemed to do
nothing — usually clearing up only when you reloaded the page.

Two changes remove that window rather than shortening it.

**A pod now keeps claiming its hubs until the claim sticks.** Previously it tried for about three
seconds and then quietly stopped, after which that hub stayed on the slow fallback for as long as
the pod lived, with nothing said anywhere. The claim now keeps trying — with a backoff, so it costs
nothing — and only stops for a real reason: the hub going away, or the pod shutting down. If a claim
has not landed quickly, that is now reported once, naming the hub, so it can be investigated instead
of going unnoticed for days.

**And a message with nowhere to go is answered immediately.** Instead of being pushed into the
fallback queue, it comes straight back as "that hub is not reachable right now — ask again". Every
part of the portal that waits on data already knows how to handle that answer: live views keep their
subscription and resume, editors retry, and nothing is torn down. So the roll window that used to
cost you a minute of silence now costs a round trip you will not notice.

The old fallback is kept only where it is genuinely the only route available, which in this
deployment is nowhere at all.
