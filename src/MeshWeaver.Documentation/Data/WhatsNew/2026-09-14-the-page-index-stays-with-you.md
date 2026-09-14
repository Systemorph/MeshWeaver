---
Name: The page index stays with you
Category: Feature
Description: A document tree's left-hand index now follows you into its sub-pages and marks where you are, the divider beside it drags, and a button in the top bar hides and shows it — the way a browser's sidebar does.
Icon: PanelLeftContract
Order: -20260914
---

# The page index stays with you

A document with sub-pages shows an index of them on the left. Until now that index listed the page
you were on — so it was there on the document's root page and gone the moment you clicked into a
sub-page, which has no sub-pages of its own. You landed on the sub-page with no index and nothing
telling you where in the document you were.

**The index is now the document's whole tree, on every page of it.** It is rooted one level below
the Space — `Infrastructure/Inference`, say — and every page beneath that root shows the same index:
the root's pages in their order, a page with sub-pages as a collapsible group, the groups on your
path open, and **the page you are reading marked** with the accent bar. Clicking into a sub-page
keeps the index and moves the marker. A page directly under a Space with nothing beside or below it
still renders full width, as before.

Two pieces of chrome came with it:

- **The divider drags.** The line between the index and the content is a real resize handle
  (180–480px). It was meant to be all along; a rule that hides the chat panel's divider when that
  panel is closed was written broadly enough to hide this one too.
- **A button in the top bar hides the index** — beside the logo, where a browser keeps its sidebar
  button. Hidden, the button turns into the one that brings it back, and your choice follows you from
  page to page and across reloads. The button appears only on pages that have an index.

A course keeps its own index (the whole course, in the course's order) and gains the same divider and
button.
