---
Name: Slide shows can carry the whole deck at once
Category: Feature
Description: The presenter view can now be handed every slide pre-rendered, so advancing a slide is instant instead of a page load.
Icon: Sparkle
Order: -20260826
---

# Slide shows can carry the whole deck at once

Presenting used to reload the page on every keypress: each arrow re-resolved the next slide on the
server and rendered it from scratch, which is slow on a cold deck and simply fails when that round
trip breaks.

The slide show now accepts a whole deck up front — every slide already rendered — together with the
slide to open on and the shape of the address-bar link. The presenter holds all of them and switches
between them in the browser on the usual keys and on click, so advancing is immediate and cannot
fail on a lost round trip, while the address bar keeps the current slide's shareable link and Back
still leaves the deck in one step. Links inside a slide keep navigating normally, and Escape still
exits the presentation.

This is the presenter's side of the change; the deck view starts handing it whole decks next, and
until it does, decks present exactly as they do today.
