---
Name: The node menu groups related actions into sub-menus
Category: Feature
Description: Related entries now sit behind one parent — the three export actions live under a single Export entry — and nested items finally render as real sub-menus on every client.
Icon: Sparkle
Order: -20260812
---

# The node menu groups related actions into sub-menus

The node menu had grown to roughly fifteen entries in a single column: invite, edit, pin, move,
copy, delete, PDF, email, DOCX, request approval, data, versions, stop synchronisation,
synchronisations, recycle. Every new capability made it taller, and the entries that belonged
together were separated only by a thin divider.

Menu entries can now nest. A parent opens a sub-menu instead of acting, so a group of related
actions takes one row rather than three or four.

The first place you will see it is **📦 Export**, which now holds the three actions that all mean
"take this document somewhere else" — 📄 Export to PDF, 📝 Export to DOCX and 📤 Share as email.
They are in the same order as before, in the same part of the menu; they just no longer occupy three
of its rows. A deck, which offers PDF and email but not DOCX, uses the same Export entry, so both
kinds of document present their export actions in the same place.

Sub-menus open by click or from the keyboard — never on hover alone — and carry the right roles for
a screen reader. On the phone they are a drill-down rather than a flyout: tapping a parent replaces
the list with its contents and offers a way back, because a second panel opening beside the first
needs a pointer and a wide screen.

This also fixes something that was quietly broken. Nested entries had been supported in the data for
a while, but no client actually drew them — the parent was thrown away and its children spliced into
the main list. Where a space had two GitHub synchronisations configured, that produced two identical
sets of "Sync now / Update to latest / Check branch" with nothing to say which repository either
belonged to. Each now sits under its own named entry.

A parent that exists only to hold other entries can no longer be clicked to nowhere, and one whose
contents you do not have permission to see disappears rather than opening onto an empty list.
