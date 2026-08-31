---
Name: A portal host fix now ships on its own, instead of waiting for an unrelated change
Category: Fix
Description: The portal's own configuration lives in a second repository, and delivery could not see changes there — a fix could sit unshipped, with nothing that would ever build it, until some unrelated change happened to trigger a rebuild. Delivery now recognises those changes and ships them by itself.
Icon: Sparkle
Order: -20260831
---

# A portal host fix now ships on its own, instead of waiting for an unrelated change

The portal image is assembled from two repositories: the platform, and a second one holding the
portal's own host configuration. Delivery identified each published image by the platform commit it
was built from — and only that. A change to the host configuration would therefore ship inside the
next image built, but nothing ever noticed that a new one was *due*.

The hourly check that keeps delivery honest asks a single question: does the current platform commit
have a complete set of published images? When only the host configuration had changed, the answer was
yes, so the check did nothing. The fix would sit unshipped with no producer that would ever build it,
until somebody merged an unrelated platform change and the rebuild carried it along by accident.

This happened. A fix for engine activation on a fresh install merged one afternoon, and the newest
image had been built minutes earlier — so the fix had no route to production at all.

Published portal images now carry both commits in their identity, so the hourly check can ask the
question it was always meant to ask: is the published image built from the *current* host
configuration? When it is not, the image set counts as incomplete and delivery rebuilds on its own.
A host fix now ships within the hour, without anyone having to notice it or ask for it by hand.

The manual override added earlier stays available for the cases no identity can capture, but it is no
longer the only thing standing between a merged fix and a shipped one.
