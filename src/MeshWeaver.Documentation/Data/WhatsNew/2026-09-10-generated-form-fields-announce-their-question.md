---
Name: Generated form fields announce their question
Category: Fix
Description: Every input a generated editor produces now carries its question as an accessible name, so screen readers and keyboard users hear the field they are in.
Icon: AccessibilityCheckmark
Order: -20260910
---

A field the platform generates from your type paints its question beside the input rather than
inside it — the `Edit` form as the term of a definition list, the node editor's click-to-edit form as
a caption above the value. The input itself was left with no accessible name, so a screen reader
announced it as a bare "textbox" and gave no way to tell one question from the next.

Every generated input now carries its question as its accessible name, read from the same
`[Display(Name = …)]` / `[DisplayName(…)]` the visible caption comes from, so the two can never
drift apart. Fields you compose by hand with an explicit label are unchanged.
