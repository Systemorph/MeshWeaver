---
Name: List fields can't be wiped by a stray keystroke
Category: Fix
Description: The node Edit form no longer lets a single character overwrite a whole list-valued field — collections now display read-only instead of as a destructive text box.
Icon: ShieldCheckmark
Order: -20260806
---

# List fields can't be wiped by a stray keystroke

Editing a node whose type has a list-valued field is now safe. Such a field used to render in the Edit form as a text box that looked empty; typing a single character into it replaced the entire list with that text, corrupting the node — and once corrupted, the node could no longer be repaired through the form.

Those fields now display read-only in the Edit form, so an edit can no longer destroy the list. A proper in-form list editor is a separate future improvement; until then, the data stays intact.
