---
Name: An area whose node is gone always shows its card
Category: Fix
Description: A layout area bound to a node that no longer exists now shows the "Not found" card whichever arrives first, the view or the mesh's answer — it used to render nothing at all when the answer came first.
Icon: DocumentError
Order: -20260913
---

# An area whose node is gone always shows its card

A layout area whose target node has been deleted renders a small, localized "Not found" card in
its place. That card depends on the view seeing the mesh's answer that the node is gone. When the
answer arrived *before* the view had bound to its stream — which happens routinely on the second
look at the same missing path, because the mesh answers a miss it has already resolved in a few
milliseconds — the view was handed a stream that quietly completed instead of one that reported the
fault. With nothing to react to, the view drew nothing: no card, no error, an empty area.

A stream that has already faulted now re-delivers its fault to every later subscriber, exactly as a
stream that faults after the subscription does. The view enters its error branch either way and
draws the card. A stream that was disposed rather than faulted still completes quietly, as before.

The fault-before-bind order is now pinned on both sides: the platform test
`GetControlStream_OnAFaultedStream_ReDeliversTheFault` and the view guard
`TheNodeGoneArea_StillDrawsTheCard_WhenTheFaultLandedBeforeTheViewBound`, which renders the real
view against a real routing miss and asserts the card is in the markup.
