---
Name: Retired plugin nodes are pruned on update
Category: Fix
Description: A node a plugin repo removes is now deleted from the mesh on the next update instead of surviving forever.
Icon: Sparkle
Order: -20260828
---

# Retired plugin nodes are pruned on update

When a plugin's source repository deletes a node — a NodeType, a document, an agent — updating
the installed plugin now removes that node from the mesh instead of leaving an orphaned copy
behind forever. Previously this only happened on a narrow "incremental update" path; a full
re-install (which also runs whenever a plugin changes shared source code) silently kept every
node the repo had already retired. A left-behind NodeType with no source left to compile against
could later fail to rebuild and delay the affected instances from starting up. Updates are now
consistent: whatever the repo no longer ships is removed from the installed partition.
