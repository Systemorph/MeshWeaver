---
Name: The redline moved to where you ask for it
Category: What's New
Description: Documents no longer open covered in tracked changes. The redline now lives in the version comparison, where you say which version you want compared to which — and "compare with current" is one click from every version.
Icon: Sparkle
---

# The redline moved to where you ask for it

Opening a document used to mean opening a review of it. Every markdown page
rendered its recent history as a redline — insertions underlined, deletions
struck through, cards down the side — whether you had come to read the document
or to review it.

Reading is not reviewing. A document page now shows the document.

## Where the redline went

**Versions**. The redline is now what a version comparison looks like, and a
comparison only exists once you have said which two versions it is between.

The version list is the picker — there is no separate dialog to fill in and
nothing to remember between clicks:

- **Compare with current** sits on every version's row. That is the question most
  people arrive with — *what has happened since?* — and it costs one click. It is
  absent on the current version's own row, where it would compare a version with
  itself.
- **From** and **To** claim any two versions as the ends of a comparison. The
  bar at the top says what will be compared; **Compare** stays inert, and says
  why, until both ends are named.
- Claiming an end that would invert the pair releases the other one instead of
  offering a backwards comparison. The picker cannot be put into a state that
  Compare would have to refuse.

## What a comparison shows

Prose renders as the redline you already know: every change between the two
versions marked up inline, with a card naming who made it and when. **Show the
source diff** switches to the side-by-side view when you want to see the raw
markdown — front matter, link syntax — and other content types go there
directly.

Comparing against the **current** document stays live: further edits appear as
they land, and each change can still be reverted with **↩**. Comparing two
past versions pins the view to the document as it stood then — there is nothing
on screen to revert into, so revert is not offered.

Comments are unaffected: they live on the document's page, which is where the
conversation about a document belongs.
