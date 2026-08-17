---
Name: Faster portal startup
Category: Fix
Description: Restarting a portal no longer re-adopts pre-built code that was already in place.
Icon: Sparkle
Order: -20260817
---

# Faster portal startup

Every time a portal started, it re-installed all of the pre-built code that ships with it — even
when that code was already in place and unchanged from the previous start. Reinstalling each piece
meant waking up the component that owns it, re-uploading its compiled bytes and re-saving its
record, so a large portal spent a noticeable part of every startup confirming that nothing had
changed.

The portal now reads each package's index first and only re-installs the pieces that actually
differ. A restart where nothing changed does none of that work. Startup is correspondingly quicker
and quieter, and the shared code cache stops accumulating a fresh copy of every component on each
restart.

Nothing is skipped on trust: if the compiled bytes are genuinely missing — after the code cache is
cleared, remounted or restored from a backup — that piece is re-installed exactly as before.
