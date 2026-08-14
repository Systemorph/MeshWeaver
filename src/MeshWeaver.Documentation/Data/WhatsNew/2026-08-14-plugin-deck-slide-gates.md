---
Name: Plugin slide decks get full deck navigation and export
Category: Fix
Description: Slides and decks installed by a plugin (e.g. Publish/Slide) now drive the deck navigation, the Export menu, and PDF/HTML export exactly like the built-in types.
Icon: Sparkle
Order: -20260814
---

# Plugin slide decks get full deck navigation and export

Slides whose type comes from a plugin — such as `Publish/Slide` — previously rendered without
their deck context: the counter stuck at "Slide 1 / 1", Prev/Next never appeared, and a
plugin-typed deck offered no Export menu and no PDF export. The platform now recognizes
plugin slide and deck types everywhere the built-in `Slide` and `Deck` types are recognized:
deck navigation, the presenter bar, the Export menu, pixel-faithful PDF export, and HTML export.
