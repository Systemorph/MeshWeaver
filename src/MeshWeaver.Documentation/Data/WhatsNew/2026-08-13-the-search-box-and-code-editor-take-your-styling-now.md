---
Name: The search box and code editor take your styling now
Category: Fix
Description: The search box, the mesh search panel and the code editor ignored the size, spacing and styling their author set — and unlike the other elements, they never received it in the first place.
Icon: Sparkle
Order: -20260813
---

# The search box and code editor take your styling now

Three elements are left over from the styling sweep: the search box, the mesh search panel and the
code editor. Setting a width on any of them did nothing.

They look like the same bug as the others, and they are not. Everywhere else the element received
the styling you set and then forgot to write it out — a one-line omission in how it draws itself.
These three were built on a different foundation than every other element on the page, and that
foundation is the part that hands an element its styling. So there was nothing to write out: the
value never arrived.

That difference is invisible from the outside, which is why it survived a full audit of the other
forty-seven. All three are now on the same foundation as everything else, so they take a width, a
height, spacing and a styling name like any other element. The code editor has no box of its own —
it *is* the editor — so its styling goes onto the editor's frame rather than a new box wrapped
around it, and your values come after the built-in ones, so a height you set replaces the default
instead of losing to it.

The page cannot drift back. Registering an element built on the wrong foundation is now refused
when the product is built, naming the element and what it should have been — so this cannot be
introduced again without someone seeing it.
