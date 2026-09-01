---
Name: You can remove what you were allowed to add
Category: Fix
Description: Editors can now delete the comments, activity rows and presence entries they are allowed to create — the delete check no longer disagrees with the one that let them write it.
Icon: Sparkle
Order: -20260901
---

# You can remove what you were allowed to add

Some things attached to a node — a comment, an activity entry, a live-presence row, a thread — are
governed by the node they hang off: if you may edit the node, you may add them, and if you may edit
the node, you may take them away again. That is what the platform's rule for them has always said.

The delete path, however, was asking a different question. Before removing anything it demanded a
full delete permission on the node, without consulting that rule — so an **Editor** could add one of
these and then be told they were not allowed to remove it. You could turn something on and not turn
it off, which is the one thing an "off" switch has to be able to do. It showed up wherever a feature
publishes ongoing state on your behalf and expects you to be able to withdraw it.

Deleting now asks the same question the rest of the platform asks, so both answers agree. If the
node type has its own rule, that rule decides; if it does not, the delete permission is required
exactly as before — nothing else became easier to delete. Deleting a **space** or a **partition**
still requires delete rights, unchanged.

Two smaller improvements come with it. A delete that could not be *evaluated* — because a check it
depends on did not answer — now says so plainly instead of reporting it as "permission denied", so
you are not sent off to request rights you already hold. And a delete is never allowed by default
when its check cannot reach an answer: it is refused, and says why.
