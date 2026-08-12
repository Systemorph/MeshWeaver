---
Name: A request that arrives while something is shutting down now gets an answer
Category: Fix
Description: When part of the platform is being taken down, a request that reaches it at that moment is refused straight away instead of sitting silently for up to half a minute.
Icon: Sparkle
Order: -20260812
---

# A request that arrives while something is shutting down now gets an answer

Parts of the platform are started and stopped constantly — a page's data area, a node whose
definition just changed, a background job that finished. While one of them is starting up it holds
incoming requests briefly and releases them the moment it is ready. That short hold is normal and
invisible.

The problem was what happened when such a part was told to shut down *before* it finished starting.
The hold stayed in place, and nothing would ever lift it — shutting down is precisely the state
after which "ready" never arrives. Anything already waiting behind it simply sat there. No error, no
refusal, nothing on screen: the caller waited out an unrelated internal deadline, up to thirty
seconds, and only then gave up. The same happened to anything that arrived during the rest of the
shutdown.

A caller that is told "no" can do something about it — show a message, ask again, move on. A caller
that is told nothing can only wait. So a hold that can no longer be lifted now **refuses** what it
is holding, immediately and explicitly, and refuses anything that arrives afterwards for the same
reason. The refusal says the component is going away and that the address may come back, which is
the cue to retry — so anything with its own recovery simply reconnects instead of stalling.

Nothing waits longer than before, and no new deadline was introduced: the answer comes from a fact
the platform already knows the instant shutdown begins.
