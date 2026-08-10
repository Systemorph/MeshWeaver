---
Name: Recursive delete now refuses permission denials up front — never half-deletes a subtree
Category: Fix
Description: A recursive delete that hits a missing Delete permission is now refused atomically before anything is removed, with a clear "permission denied" answer instead of an unexpected error after a partial deletion.
Icon: Sparkle
Order: -20260810
---

# Recursive delete now refuses permission denials up front — never half-deletes a subtree

Deleting a folder tree you were not fully allowed to delete could previously remove part of
the tree before stopping: the permission check for each node ran while the deletion was
already underway, so dozens of nodes could be gone by the time the denial hit — and the error
was reported as an unexpected failure rather than a permission problem.

Recursive deletes now decide permissions for the whole subtree before touching anything. If
you lack Delete on any node in the tree, nothing is deleted and you get a clear message naming
the node you are not allowed to delete. A delete that was fully authorized when it started can
also no longer trip over its own access records mid-way — it either refuses up front or
completes fully.
