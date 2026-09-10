---
Name: Generated form fields announce their question
Category: Fix
Description: A generated form field now carries its question as an accessible name, so a screen reader says which question you are answering.
Icon: AccessibilityCheckmark
Order: -20260910
---

A field the platform generates from your type paints its question beside the input rather than
inside it — the `Edit` form as the term of a definition list, the node editor's click-to-edit form as
a caption above the value. The input itself was left with no accessible name, so a screen reader
announced it as a bare "textbox" and gave no way to tell one question from the next.

Generated form fields — text, multi-line text, number, date, checkbox, switch, and the option and
mesh-node pickers — now carry their question as an accessible name, read from the same declaration
the visible caption is read from, so the two can never drift apart. Fields you compose by hand with
an explicit label are unchanged, and a property rendered as a full markdown editor is not covered
yet: that editor is a composite with its own toolbar, and naming it is a different question.
