---
Name: A hidden menu entry no longer leaves its divider behind
Category: Fix
Description: The node menu's section dividers were decided before the menu was finished, so hiding an entry could strand the rule that followed it — and an entry a package added could land with no divider at all. They are now derived from the menu you actually see.
Icon: LineHorizontal1
Order: -20260903
---

# A hidden menu entry no longer leaves its divider behind

The node menu is grouped into sections — edit and organise, then content and history, then the
lifecycle actions at the bottom — with a thin rule between them. Those rules were being decided too
early: the built-in list worked out where they belonged from **its own entries**, before two things
that change the answer.

The first is the menu catalog. Hiding an entry there is an ordinary, supported edit — it is most of
what the catalog is for. But if you hid the last entry of a section, the rule that came after it had
already been decided and stayed, leaving a divider with nothing above it.

The second is everything else that adds to the menu: an installed package's entry, a per-type action,
a sync integration. Those arrive after the built-ins have already placed their rules. On your own
home page, where every built-in entry in the first section is deliberately suppressed, no rule was
placed at all — so an entry a package contributed there ran straight into **Files** with nothing
between them.

**The dividers are now worked out last, from the finished menu.** A rule appears wherever two
entries that are actually on screen belong to different sections, which means it can never lead the
menu, never trail it, and never double up — and if a whole section is empty you get one rule, not
two. Hiding an entry now removes its rule with it, and a contributed entry gets the same separation
from its neighbours as a built-in one.

Nothing about which entries you see has changed: the same permissions, the same suppressions on a
protected home, the same catalog edits. Only where the lines fall between them.
