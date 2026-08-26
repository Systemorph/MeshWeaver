---
Name: Chat history survives a cold start
Category: Fix
Description: Sending a message in an existing conversation shortly after the portal restarts no longer fails with an error — the earlier turns are read back correctly instead of being treated as unreadable.
Icon: Sparkle
Order: -20260825
---

# Chat history survives a cold start

Sending a message in a conversation that already had earlier turns could fail outright if the
portal had only just restarted. The round ended in an error rather than an answer, and trying
again a moment later often worked — which made it look like an intermittent glitch rather than
something reproducible.

It was not a glitch. When a conversation is read for the first time after a restart, its earlier
messages briefly arrive in a raw, not-yet-typed form. The step that rebuilds the conversation for
the assistant only recognised the fully-typed form, so it quietly treated every earlier message as
unreadable, waited out its own time limit on each one, and then — correctly refusing to answer
with no history at all — failed the round.

Earlier messages are now read in whichever form they arrive, so the assistant sees the whole
conversation and the round proceeds normally. A message that is genuinely unreadable is still
skipped and still recorded as such, and a conversation with no earlier turns still starts cleanly
rather than erroring.
