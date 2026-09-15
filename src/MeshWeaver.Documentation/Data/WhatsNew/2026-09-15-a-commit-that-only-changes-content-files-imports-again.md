---
Name: A commit that only changes content files imports again
Category: Fix
Description: >-
  A synced repository commit that only added, changed or removed a file under content/ was treated
  as already imported. The new file never appeared, and a deleted one stayed in the space.
Icon: ArrowSync
Order: -20260915
---

# A commit that only changes content files imports again

A space synced from a repository mirrors the files under `content/` into its content collection.
Until this fix, a commit that touched **only** those files was skipped. A new video or image
never appeared in the space, and a file deleted in the repository stayed there.

The import decides whether a commit is new by fingerprinting what the repository holds. That
fingerprint covered the nodes but not the content files. So a content-only commit looked
identical to the previous import. This went unnoticed while the space root stamped a fresh
creation time on every read, which made every import look new. Once the root became stable, the
gap showed.

The fingerprint now includes every inline content file by its place and the hash of its bytes.
A space without inline content keeps the fingerprint it had. A space with content imports once
after the upgrade, and from then on a content-only commit lands like any other.
