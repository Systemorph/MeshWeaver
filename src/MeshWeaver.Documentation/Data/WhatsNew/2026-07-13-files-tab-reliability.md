---
Name: File browsing and uploads no longer freeze the page
Category: What's New
Description: The Files tab now does all its file work in the background — listing, uploading, creating folders and deleting can no longer stall the page or make files appear to vanish.
Icon: Sparkle
---

Uploading and browsing files in a node's **Files** tab is now fully decoupled from the page you are looking at. Previously, listing a folder or saving an upload ran on the same thread that drives your browser session — on hosted deployments, where file storage sits on a network share, a slow moment in storage could freeze the page, drop the connection and make freshly uploaded files look like they had disappeared. The files were always safe on disk; only the view broke.

All file operations — listing folders, opening files, uploading, creating folders and deleting — now run on a dedicated background I/O lane and stream their results to the page as they complete. A slow storage response can no longer block the page, and the file list refreshes itself once an upload finishes.

Uploads are also attributed correctly: the file write and the follow-up indexing run under the account of the person who uploaded, so permissions and activity records reflect the real author.
