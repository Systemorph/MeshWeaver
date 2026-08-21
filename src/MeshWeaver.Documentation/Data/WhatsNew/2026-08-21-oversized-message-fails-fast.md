---
Name: An impossibly large message now fails instead of hanging
Category: Fix
Description: A request whose payload is too big for the internal transport is refused straight away with a clear reason, instead of disappearing and leaving the page waiting.
Icon: Sparkle
Order: -20260821
---

# An impossibly large message now fails instead of hanging

Messages between parts of the portal travel over an internal queue that can carry at most one megabyte at a time. Anything bigger was accepted, then quietly dropped further down the line — and because the sender had already been told the message was on its way, the page that was waiting for an answer simply waited until it gave up. Nothing in the logs said which page, which request, or how big the payload had been, so there was no way to find out what had produced it.

A message that cannot fit is now refused at the moment it is sent. The waiting page gets an immediate, explicit error rather than a long pause, and the record left behind names the request, its size and where it was going — so an oversized payload can be traced back to whatever produced it instead of vanishing.
