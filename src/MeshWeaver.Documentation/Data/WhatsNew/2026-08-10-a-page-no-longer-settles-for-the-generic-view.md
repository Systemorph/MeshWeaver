---
Name: A page no longer settles for the generic view
Category: Fix
Description: An item could open with only the platform's default layout — none of its own views, and no error panel for a type that failed to build — and stayed that way until the page was recycled.
Icon: Sparkle
Order: -20260810
---

# A page no longer settles for the generic view

Opening an item builds its page from the type it belongs to. The platform waits for
that type to report a **finished** state — built, or failed to build — before it
decides which views the page gets. That decision is made once and kept for as long
as the page's engine stays warm.

Sometimes the wait ended early, on a snapshot from *before* the build had reported
anything. The page was then built from the platform's generic layout instead of the
type's own: none of the type's views, and — for a type that had genuinely failed to
build — no error panel either. The page was not blank and nothing timed out; data
flowed and the page rendered. It simply never showed what it was supposed to show,
and because the decision is only made once, it stayed that way.

The cause was that the wait and the code acting on its result read the same record
two different ways. A record that has just been created, or that has just arrived
from another part of the mesh, is carried in a raw form until it is unpacked. The
code that acts on the result unpacks it; the wait did not, so to the wait every such
record looked like "not a type at all — nothing to wait for", and it finished on the
spot. Whichever form the record happened to be in at that instant decided whether
your page came out right, which is why it came and went.

Both now read the record the same way. The wait ends when the build has actually
reported, in whichever form the record arrives.
