---
Name: An edit no longer quietly blanks the fields you did not touch
Category: Fix
Description: Saving one field of a record could reset every other field to its default, with no error anywhere. Writes now say which kind of record they are editing, and refuse rather than guess.
Icon: Sparkle
Order: -20260813
---

# An edit no longer quietly blanks the fields you did not touch

Changing one field of something in the mesh — a thread's status, a page's text, an activity's
progress — could silently blank every other field of that record. No error, no warning, nothing in
the log: the value you edited was saved correctly and everything around it came back empty.

It happened whenever the record arrived as plain stored data rather than as a ready-made object,
which is the normal case any time the record is read from the database or from another part of the
mesh. The code doing the saving would try to recognise the record, fail to, and quietly start from a
blank one — so the save wrote your single change on top of a fresh, empty record instead of on top
of the real one. Because the blank record is a perfectly valid thing to save, nothing along the way
had any reason to object.

A save now states up front which kind of record it is editing, so there is nothing to recognise and
nothing to guess. And when the stored data genuinely cannot be read as that kind of record, the save
**fails and says so** — naming the item and what was actually found — instead of falling back to a
blank one. A refused save changes nothing; the real content stays exactly as it was.
