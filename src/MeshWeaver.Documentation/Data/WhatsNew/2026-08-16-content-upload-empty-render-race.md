---
Name: Uploaded files can no longer open as an empty page
Category: Fix
Description: A rare race could make a just-uploaded markdown file open as an empty page until the collection reloaded — the stale snapshot can no longer win.
Icon: Sparkle
Order: -20260816
---

# Uploaded files can no longer open as an empty page

Uploading a file into a content collection and opening it right away could — very rarely — show
an empty page instead of the file's content, and the page stayed empty until the collection was
reloaded. The cause was a race between the upload's own read of the file and the file-system
watcher's read of the moment the file was created: the stale, still-empty snapshot could be
applied after the complete one and stick. The ordering guard is now applied atomically with the
update itself, so the complete content always wins and the freshly uploaded file renders
immediately.
