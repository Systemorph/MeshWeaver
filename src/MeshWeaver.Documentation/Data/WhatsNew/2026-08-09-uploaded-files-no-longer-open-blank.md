---
Name: A file you just uploaded no longer opens blank
Category: Fix
Description: Uploading a markdown file and opening it could show an empty page — the content was on disk the whole time. Exporting a subtree could also hang silently instead of finishing.
Icon: Sparkle
Order: -20260809
---

# A file you just uploaded no longer opens blank

Upload a markdown file into a content collection, open it, and — occasionally —
the page came up completely empty. Not "not found", not an error: a blank
document, with the file sitting on disk, intact, the whole time. Reloading did
not help. The only thing that ever brought the text back was something that made
the collection re-read itself from scratch.

The cause was a race between two readers of the same file. When a file is
written, the platform notices it twice: once because it did the writing, and
once because the folder is being watched for changes made by anything else (a
git sync, another person, an external tool). The watcher is told about the file
the instant it is *created* — which is a moment before the contents have been
written into it. Both readers then load the file and both publish what they
found, and whichever finished last won. Usually that was the one that read the
finished file. Sometimes it was the one that read the empty one, and then the
empty version stuck.

Reads now win by **when they were triggered**, not by when they happened to
finish. A read that started before the file was written can no longer overwrite
what a later read published, so the version you end up looking at is always the
newest one. Nothing about the watching changed — changes made outside the
platform are still picked up exactly as before.

## Exporting a subtree no longer hangs

Separately: asking an agent to export a node subtree could simply never come
back. No error, no partial result, no entry in the log — the export sat there
until whatever was waiting on it gave up. It happened only when you had export
rights on at least some of the nodes, which is why it looked so arbitrary.

Checking your permission on each node was holding a lock that the rest of the
export then needed while it opened those nodes and read their files. Two exports
(or an export and any other permission-checked operation) could end up waiting on
each other, and neither could report it. The permission answer is now taken and
released before the export does its work, so the two can no longer block one
another.
