---
Name: The assistant stops asking connected users to reconnect
Category: Fix
Description: A credential read that timed out was reported as "you have never connected your mailbox", so the Executive Assistant handed a connected user the consent link. The read now says when it could not find out — and it no longer deadlocks the hub it runs on.
Icon: PlugConnected
Order: -20260906
---

# The assistant stops asking connected users to reconnect

Ask the Executive Assistant to look at your mail and it sometimes answered *"I don't have access to
your mailbox and calendar yet. Please connect them here…"* — to someone whose mailbox had been
connected for weeks.

Pressing that link re-consents a grant that was never revoked, so it appeared to work. That is why
this went undiagnosed for so long: the failure looked exactly like a successful recovery from a state
that had never existed.

## What was actually happening

Two things, one on top of the other.

The assistant's mailbox tools run **inside an agent round, on a hub**, and a hub processes one thing
at a time. The code that read your stored credential *waited* for that read to come back — so the
hub was busy waiting, and the reply to the read had to queue behind the wait. It could not arrive.
Ten seconds later the read gave up.

Then the second half: that timeout was caught and turned into the same answer the code gives for a
user who has genuinely never connected. "I could not find out" and "you never connected" were
literally the same value, so the assistant offered the only thing it knew to offer.

## What changed

The credential read no longer waits — it composes and subscribes, so the hub stays free to deliver
the reply it is waiting for. And it now distinguishes three answers instead of two:

- **connected** — proceed;
- **not connected** — the read completed and found nothing, which is the only moment the consent link
  is the truthful thing to show;
- **could not be determined** — the read timed out or faulted. The assistant says so and asks you to
  try again, and it does **not** offer you a link to reconnect something that is probably already
  connected.

Three separate places in that one method used to collapse into "never connected", and one of them —
a content check that silently returned nothing for a shape it did not recognise — left no log line at
all. All three are gone.

Visiting the connect link yourself still works exactly as before, including the deliberate
`?force=true` re-consent for scope changes.

## The lasting part

A new governance test refuses two shapes anywhere in the platform: waiting on a mesh read (which
makes the caller hub-reachable by construction, and therefore a deadlock), and putting a `Task` on
one of the interfaces that hub-side modules compile against — because a `Task` there does not merely
allow the mistake, it forces it. Both rules were watched fail on a re-introduced copy of the original
line before being committed.

The full reasoning is in
[Executive Assistant Credential Reads](/Doc/Architecture/ExecutiveAssistantCredentialReads).
