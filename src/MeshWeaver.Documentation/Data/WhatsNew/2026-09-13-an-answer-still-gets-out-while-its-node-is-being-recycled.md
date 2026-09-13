---
Name: An answer still gets out while its node is being recycled
Category: Fix
Description: A saved change whose node was being recycled at that moment — a retype, for example — no longer leaves the caller waiting half a minute for a verdict that had already been produced.
Icon: ArrowReply
Order: -20260913
---

# An answer still gets out while its node is being recycled

Changing a node's type recycles that node's hub — that is how the new type takes effect. A change
saved at that exact moment could be applied and acknowledged by the node and still leave its caller
waiting: the answer was refused on its way out of the hub that had just produced it, because that
hub had entered the next phase of its own teardown a fraction of a millisecond earlier. The caller
heard nothing for 31 seconds and was then told the outcome was unknown, for a change that had in
fact been saved. Importers and repair flows, which retype and then immediately write again, met it
most.

An answer is now treated as an answer for as long as the hub can still send it, so it leaves and
reaches whoever is waiting. In the narrow window where it genuinely cannot leave any more, the
party that is waiting is told so at once — and told that the outcome is unconfirmed and worth
retrying, rather than that it failed, because the change it reported may well have been saved.
