---
Name: An agent that imported as a plain page now says so
Category: Fix
Description: When a file's own node type cannot be read on the importing portal, the node it becomes now names the settings that were dropped — instead of looking complete and quietly doing nothing.
Icon: Bug
Order: -20260914
---

# An agent that imported as a plain page now says so

A `.md` file says at the top what kind of node it is — an agent, a skill, a lesson — and the rest
of its heading carries that kind's settings: an agent's display name, its description, the plugins
it may use, where it should appear. If the part of the portal that understands that kind is not
loaded when the file is imported, the file still becomes a node, and the node still looks right:
correct type, correct name, correct icon, correct category, full body. Only the settings are gone.

Nothing said so. An agent imported that way appeared in the agent picker with no description, no
tools and no context — and the only way to find out was for somebody to notice, which on the
public portal took twelve days.

From now on, such a node carries the list of settings that were dropped. So the same
question — *"why does this agent have no description?"* — is answered by looking at the node,
which names `displayName`, `plugins` and the rest, instead of by comparing it against the file it
came from.

Nothing changes for ordinary pages: a page that declares no node type has no settings to lose and
gets no such list. And nothing changes about the files themselves — exporting content back to its
repository produces exactly the same bytes as before.

The fix for an affected node is unchanged and unaffected: import it again on a portal where its
kind is available, and it comes back complete — at which point the list disappears by itself.
