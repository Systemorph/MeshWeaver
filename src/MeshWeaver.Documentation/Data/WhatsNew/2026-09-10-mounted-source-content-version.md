---
Name: Mounted source fingerprints detect content edits
Category: Fix
Description: Mounted package source fingerprints now detect edits even when the file size stays the same.
Icon: FolderSync
Order: -20260910
---

Mounted package sources now include file contents in their fingerprints. Editing text or replacing an image with the same byte length changes the source fingerprint used as the version of packages without a declared release version. Unchanged files keep their fingerprint.
