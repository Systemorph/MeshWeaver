---
Name: A NodeType's data model no longer loses a collection
Category: Fix
Description: Opening the Data model of a NodeType whose instances read their own node — the Store catalog among them — showed that collection empty for anyone but a privileged user, and logged an access error per viewer. The short-lived hub behind that view now answers such a read truthfully instead of denying it.
Icon: Bug
Order: -20260901
---

The **Data model** section of a NodeType — and the `$Model` area behind it — is served by a hub that
exists for a few microseconds: it applies the NodeType's instance configuration, is read once, and is
thrown away. That is how the portal can show you what a type's instances look like when no instance
exists.

Instance configuration is written for real nodes, so it routinely reads *its own node* — a loader
reading its configuration, a list a view is driven from. The Store catalog's package feed does
exactly that. On the short-lived hub there is no node to read, and the read was being treated as if
there might be: the platform evaluated the viewer's permissions on an address that is not a node,
found none, and refused it with **"User 'x' lacks Read permission on '$model-probe/…'"**. The
collection that issued the read was then frozen empty, so the Data model rendered **without it** —
and one error was logged for every viewer who opened the page. Users with broad rights saw the
section complete; everyone else saw a hole, with nothing on screen to say why.

The refusal was false in both halves: nothing had been denied to that user, and there was nothing
there to deny. A read of such an address is now answered directly — *there is no node here, and there
never will be* — before any permission check or routing happens. The collection initializes normally,
the data model is complete for every viewer, and the error lines are gone. Reads of any real path
from the same hub are unchanged, and writes are still refused loudly rather than silently accepted.

Full detail — the four probe kinds, the two read seams that must answer directly, and the three
failure shapes an unguarded seam produced — is in
[Transient Node Probes](/Doc/Architecture/TransientNodeProbes).
