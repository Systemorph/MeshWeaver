---
Name: Bulk imports no longer lose content to the storm guard
Category: Fix
Description: A large import or sync could have some of its writes silently discarded by the runaway-message guard; the guard now tells a wide fan-out apart from a genuine loop.
Icon: Sparkle
Order: -20260812
---

# Bulk imports no longer lose content to the storm guard

The platform carries a safety guard that watches for runaway message loops and cuts them off
before they can freeze a workspace. The guard judged traffic only by who sent it, who it was for,
and what kind of message it was — it could not see *what each message was about*. A large import or
sync sends thousands of writes that share all three of those, one per page or record, so the guard
mistook the whole batch for a single message repeating itself and discarded part of it.

The result was content that simply never appeared: an import would report itself finished while
some of its pages, or some of its progress log, were missing, with nothing in the interface to say
anything had been dropped.

The guard now also considers which page, node or stream each message concerns. Thousands of writes
to thousands of different places are recognised as normal work and pass through untouched, while a
genuine loop — the same one thing repeating thousands of times a second — is still stopped just as
before. As a bonus, when the guard does fire it now names the exact item that was looping, so the
underlying cause can be found instead of guessed at.
