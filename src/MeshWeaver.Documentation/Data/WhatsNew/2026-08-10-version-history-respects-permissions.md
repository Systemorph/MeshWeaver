---
Name: Version history respects your read permissions
Category: Fix
Description: Version history no longer shows content you are not allowed to read.
Icon: Sparkle
Order: -20260810
---

# Version history respects your read permissions

Every node keeps a version history, and that history contains the node's full content at
each point in time. Until now the version tools read that history directly, without asking
whether you were allowed to see the node at all — so content that was correctly hidden from
you everywhere else (search, direct reads, diagnostics) could still be read back through its
version history.

The version tools now apply exactly the same permission check as a normal read. If you
cannot read a node, its version history answers the same way a missing node would — it does
not reveal that anything exists at that path. Readers with access see no difference.
