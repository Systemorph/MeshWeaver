---
Name: Shift+Enter keeps inserting a newline after you use a slash command
Category: Fix
Description: In the chat composer, Shift+Enter sometimes did nothing at all. It happened after a / or @ picker had been open, because closing the picker never gave the keyboard back to the message box.
Icon: KeyboardShiftUppercase
Order: -20260825
---

# Shift+Enter keeps inserting a newline after you use a slash command

Shift+Enter adds a line break in the chat composer instead of sending. Sometimes it did nothing —
the same build, the same page, no pattern anyone could pin down. Typing still worked, sending still
worked, only that one chord went nowhere.

The trigger was the `/` and `@` picker. Opening it moves the keyboard onto the list on purpose, so
the arrow keys scroll the options instead of the message text. Closing it — by picking something,
by pressing Escape, or with the ✕ — never moved the keyboard back. The composer looked ready and
was not: clicking into it restored focus, which is why ordinary typing seemed unaffected and why the
fault looked random rather than tied to what you had just done.

Every way of closing the picker now returns the keyboard to the message box, so Shift+Enter works
immediately afterwards, with no click in between.
