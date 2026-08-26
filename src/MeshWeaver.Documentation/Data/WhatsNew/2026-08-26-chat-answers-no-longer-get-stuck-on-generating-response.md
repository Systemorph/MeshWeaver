---
Name: Chat answers no longer get stuck on "Generating response…"
Category: Fix
Description: A finished chat reply could stay stuck showing the "Generating response…" placeholder instead of the real answer; delegated sub-conversations also now clean up after themselves reliably.
Icon: Sparkle
Order: -20260826
---

# Chat answers no longer get stuck on "Generating response…"

A chat message could occasionally finish — the round showed as done — while the reply still
displayed the "Generating response…" placeholder instead of the actual answer. Reloading the page
sometimes fixed it, which made it look like a rendering glitch rather than something reproducible.

It was not a glitch. When a reply is written to the mesh in one update covering several fields at
once (the finished status together with the finished text), a stale write for just ONE of those
fields could occasionally arrive slightly out of order. The system used to accept the fields that
still matched and silently drop the one that did not — landing a reply that was marked finished
without ever receiving its text. Updates like this are now applied as a single all-or-nothing step:
either every field of the update lands together, or none of them do and the write is retried
automatically against the latest state. A finished reply can no longer show anything but its real
answer.

A related fix closes a resource leak in delegated sub-conversations (an agent asking another agent
for help). The mesh watch that waits for the sub-conversation to finish and clean up could, in one
narrow case, fail to recognise that the sub-conversation was actually done — leaving that watch
running indefinitely instead of releasing it. Left unnoticed for long enough this could accumulate
and slow the whole system down; it is now recognised correctly every time, so delegated
sub-conversations always release their resources once finished.
