---
Name: Files you may not read answer like files that are not there
Category: Fix
Description: A denied content file now returns the same 404 as a missing one, instead of a 500 that named the reason.
Icon: Sparkle
Order: -20260811
---

# Files you may not read answer like files that are not there

Requesting a file from a space you do not have access to used to fail differently from requesting a file that simply does not exist: the first returned a server error, the second a plain "not found". Anyone could tell the two apart without signing in, which made it possible to work out which spaces exist and are private just by trying addresses.

The server error also quoted the reason back to the caller, naming the permission that was missing and the space it belonged to.

Both are fixed. A file you are not allowed to read now answers exactly like a file that is not there — a plain "not found", with no explanation attached. The real reason is still recorded in the server log, where administrators can see it. Nothing changes for files you are allowed to read.
