---
Name: Markdown workbenches are editable
Category: Fix
Description: A runnable code cell written as a fence in a markdown page is now an editor, not a static block, for anyone who may edit the page.
Icon: Sparkle
Order: -20260821
---

# Markdown workbenches are editable

An exercise, lesson or documentation page can carry a runnable code cell in two ways: as an embedded
code node, or as a fenced block written straight into the page's own text. The first kind has been
editable for a while. The second kind was not — it rendered as a static block, so on the pages where
you were asked to write the answer, you could not type.

Both kinds now behave the same. If you may edit the page, its runnable cells are real editors: type
in them, press Run, and your work is saved back into the page as you go. If you may not edit the
page — someone else's course, a published document — nothing changes: you still see the code and can
still run it, exactly as before. Code blocks that are not runnable stay read-only for everyone,
because they are material to read rather than a place to work.
