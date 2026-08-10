---
Name: A broken page now says what is wrong with it
Category: Fix
Description: Opening something whose building blocks failed to build sometimes reported a plain "it failed" instead of naming the cause, so the page could not offer the right help.
Icon: Sparkle
Order: -20260810
---

# A broken page now says what is wrong with it

When a page is built from a type that does not currently compile, the platform is
supposed to answer immediately and *specifically*: this failed to build, here is
which building block, here is what to do about it. That answer is what lets the
page show a proper error panel with the compiler output instead of a shrug.

Sometimes it shrugged. Same page, same fault, same wording in the message — but
the machine-readable part that says **what kind** of failure this was arrived
blank, and a page that cannot tell "this type failed to build" from "something
went wrong" cannot offer the right next step. It was intermittent, which made it
look like a fluke rather than a fault.

The cause was two answers being sent for one question. The part of the platform
that stands in for a broken type sends a detailed rejection — naming the failure
kind and the building block at fault — and then marks the request failed. The act
of marking it failed made a *second*, generic rejection go out for the same
request: same human-readable sentence, no classification. Whichever arrived first
is the one you got. Locally that was almost always the detailed one, which is why
this hid so well.

A request now gets exactly one answer, and it is the detailed one. Nothing about
the wording changes; what changes is that the part your page actually acts on is
always there.
