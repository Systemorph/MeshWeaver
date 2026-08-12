---
Name: Styling hooks now reach eight more parts of the page
Category: Fix
Description: Text labels, rendered documents, tab strips, editors, number inputs, node collections, chat bubbles and whole page areas accepted a styling hook from their author and then dropped it, so a theme written against one of them never applied.
Icon: Sparkle
Order: -20260813
---

# Styling hooks now reach eight more parts of the page

Anything placed on a page can be given two things by its author: a piece of direct styling, and a
*styling hook* — a name that a stylesheet elsewhere can target to restyle every element with that
name at once. The hook is how a space gives its own pages a consistent look without repeating the
same instructions on every element.

Eight parts of the page took the hook and threw it away. Direct styling worked on all of them, so
the failure was easy to misread as "styling does not work here" when in fact only half of it was
missing: a rule written against the hook simply never matched anything, with no warning and nothing
in the page to show the name had been dropped. The affected parts were text labels, rendered
documents, tab strips, the form editor, number inputs, node collections, chat message bubbles, and
whole embedded page areas.

All eight now carry the hook through to the page. Anything whose author set no hook renders exactly
as before, down to the character — the parts that already had built-in names of their own keep those
names untouched, so no existing appearance shifts.
