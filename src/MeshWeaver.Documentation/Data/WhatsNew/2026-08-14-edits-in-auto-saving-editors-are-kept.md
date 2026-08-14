---
Name: Edits in auto-saving editors are kept
Category: Fix
Description: On the React and mobile front-ends, typing in an editor that saves as you go looked fine but never wrote anything back.
Icon: Sparkle
Order: -20260814
---

# Edits in auto-saving editors are kept

Some editors save as you go rather than behind a Save button — the ones that edit a page or a code
file directly. On the React and mobile front-ends those edits went nowhere. The text stayed on
screen, nothing reported a problem, and the work was gone the next time the view loaded.

They now write back the way the main portal does: a short pause in typing saves the text, a burst of
keystrokes becomes one save, and anything still unsaved is written out when you navigate away.
