---
Name: A half-sealed platform set now heals itself
Category: Fix
Description: When a platform release seals without its plugin publication, delivery re-attempts that one step instead of waiting for an unrelated change.
Icon: ArrowSync
Order: -20260917
---

A platform release could be published while the plugin content that belongs to it was not, if that
one step lost its connection to the image registry. Everything built on the platform then waited for
content that nobody was going to publish, and the wait only ended when an unrelated change happened
to trigger a fresh build — sometimes hours later.

Delivery now checks, on its own schedule, whether the published release actually carries the plugin
content for the exact platform it describes, and re-runs just that step when it does not. A release
that already carries it is left alone, and a check that cannot be answered is reported rather than
treated as "nothing to do".
