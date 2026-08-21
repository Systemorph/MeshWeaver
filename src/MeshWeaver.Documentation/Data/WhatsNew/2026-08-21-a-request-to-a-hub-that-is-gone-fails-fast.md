---
Name: A request to a hub that is gone now fails immediately
Category: Fix
Description: A page waiting on a part of the mesh that is no longer there gets an immediate, explicit error instead of a minute of silence.
Icon: Sparkle
Order: -20260821
---

# A request to a hub that is gone now fails immediately

Some parts of the portal are reached over an internal broadcast channel rather than by a direct call. That channel has one awkward property: sending to it succeeds even when nobody is listening. If the intended recipient was no longer there, the message was accepted, silently discarded, and the page that was waiting for an answer waited out its full one-minute budget before giving up — with nothing anywhere saying what had happened or to whom.

The sender is now asked to be sure someone is listening before the message goes out, and when nobody is, the waiting request is told so straight away. A page that would have hung for a minute now reports an error in well under a second, and the record left behind names the recipient, the request and what could not be delivered.
