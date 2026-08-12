---
Name: Cards, links and dialogs honour their styling too
Category: Fix
Description: Node cards, thumbnails, catalog cards, navigation links, dialogs, the node picker and the role editor discarded the size, spacing and styling their author set — and the node picker also ignored the width and height it was given.
Icon: Sparkle
Order: -20260813
---

# Cards, links and dialogs honour their styling too

The last group of elements that were quietly throwing their styling away: node cards and
thumbnails, catalog cards, navigation links, dialogs, the node picker and the inline role editor.

Two of these were harder to spot than a plain omission. The cards and thumbnails looked like they
handled styling — they set a size and a name on themselves — but those were fixed values written
into the element, which quietly replaced whatever the author asked for. And a navigation link's
name was overwritten by the "currently open" marker, so a link's own styling name survived only
while the link was not the open one.

The node picker had a third problem: as a form field it should take a width and a height like every
other field, and it ignored both.

All seven now carry the styling to the element you actually see — a card's box rather than the
invisible link wrapped around it, so a margin is applied once and not twice — and the author's
values come after the built-in ones, so setting a width on a card or a dialog now replaces the
default instead of losing to it. The node picker takes its width and height like the other fields.
Anything whose author set no styling looks exactly as it did before.
