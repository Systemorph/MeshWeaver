---
Name: Opening a broken page now tells you what is broken
Category: Fix
Description: A page whose code does not compile now reliably reports the compilation error and names the type at fault, instead of sometimes showing a generic failure.
Icon: Sparkle
Order: -20260810
---

# Opening a broken page now tells you what is broken

Opening a page whose underlying code does not compile is supposed to tell you exactly
that: a compilation error, naming the type at fault, with a route to the compile log.
Most of the time it did. Sometimes, for the very same broken page, you got an
unhelpful generic failure instead — no error kind, no type name, nothing to act on.
Reloading could give you the good message, or the bad one again.

The page was being answered **twice**. The part of the system that stands in for a
broken type sent a proper diagnosis — "this NodeType did not compile, here it is" —
and then a second, generic "delivery failed" answer was sent for the same request.
Whichever arrived first is the one you saw, so the quality of the error depended on
timing rather than on what was wrong.

A request now gets exactly one answer, and it is the one from the place that actually
knows what went wrong. The compilation error, the name of the broken type, and the
link to the compile log are what you get, every time.
